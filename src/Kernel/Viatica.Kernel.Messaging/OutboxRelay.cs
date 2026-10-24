using System.Diagnostics;
using Confluent.Kafka;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Metadata;
using Microsoft.EntityFrameworkCore.Storage;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace Viatica.Kernel.Messaging;

/// <summary>
/// Moves outbox rows of one <typeparamref name="TDbContext"/> to Kafka. Any number of instances can run against the
/// same database; each key is owned by one of them at a time, so per key order survives scaling out.
/// </summary>
/// <remarks>
/// <para>
/// A round locks the oldest pending row of up to <see cref="OutboxRelayOptions.BatchSize"/> keys with
/// <c>FOR UPDATE SKIP LOCKED</c>. Holding a key's oldest row is what owns the key: another instance's round skips
/// that row and cannot pick a later row of the same key, because a later row is never the oldest. Locking every
/// pending row instead would let two instances split one key between them and race each other to Kafka.
/// </para>
/// <para>
/// Within a key, rows go out one after another and the key stops at its first failure; different keys go out
/// concurrently. Sending a key's rows without waiting would be faster, but a row that fails for good (too large,
/// say) would let the rows behind it overtake it. Rows Kafka acknowledged are deleted in the transaction that
/// holds the locks. A crash between the acknowledgement and that commit sends the rows again, which the consumers'
/// inbox absorbs: delivery is at least once.
/// </para>
/// </remarks>
public sealed class OutboxRelay<TDbContext>(
    OutboxRelayOptions options,
    IServiceScopeFactory scopes,
    IProducer<string, byte[]> producer,
    ILogger<OutboxRelay<TDbContext>> logger) : BackgroundService
    where TDbContext : DbContext
{
    private static readonly string Outbox = typeof(TDbContext).Name;

    private string? _lockOldestPerKey;

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        logger.RelayStarted(Outbox, options.Topic);

        try
        {
            while (!stoppingToken.IsCancellationRequested)
            {
                TimeSpan wait;
                try
                {
                    // A full batch means more rows are probably waiting, so the next round starts at once.
                    wait = await RelayOnceAsync(stoppingToken) >= options.BatchSize ? TimeSpan.Zero : options.PollInterval;
                }
                catch (Exception ex) when (ex is not OperationCanceledException || !stoppingToken.IsCancellationRequested)
                {
                    // The database or the broker is down. The loop survives and slows down rather than spinning.
                    logger.RelayRoundFailed(Outbox, ex);
                    wait = options.RetryDelay;
                }

                await Task.Delay(wait, stoppingToken);
            }
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
        {
            // Stopping. A round cut short left its locks to the rolled back transaction.
        }
        finally
        {
            MessagingTelemetry.ForgetOutbox(Outbox);
        }
    }

    /// <summary>Runs one round and returns how many rows it sent.</summary>
    internal async Task<int> RelayOnceAsync(CancellationToken cancellationToken)
    {
        await using AsyncServiceScope scope = scopes.CreateAsyncScope();
        TDbContext db = scope.ServiceProvider.GetRequiredService<TDbContext>();
        DbSet<OutboxMessage> outbox = db.Set<OutboxMessage>();

        MessagingTelemetry.ReportOldestPending(
            Outbox,
            await outbox.OrderBy(static m => m.Sequence).Select(static m => (DateTimeOffset?)m.OccurredAt).FirstOrDefaultAsync(cancellationToken));

        await using IDbContextTransaction transaction = await db.Database.BeginTransactionAsync(cancellationToken);

        List<string> keys = await db.Database
            .SqlQueryRaw<string>(LockOldestPerKey(db), options.BatchSize, DateTimeOffset.UtcNow)
            .ToListAsync(cancellationToken);
        if (keys.Count == 0)
        {
            return 0;
        }

        List<OutboxMessage> rows = await outbox.AsNoTracking()
            .Where(m => keys.Contains(m.PartitionKey))
            .OrderBy(static m => m.Sequence)
            .Take(options.BatchSize)
            .ToListAsync(cancellationToken);

        KeyResult[] results = await Task.WhenAll(
            rows.GroupBy(static r => r.PartitionKey).Select(key => SendKeyAsync(key, cancellationToken)));

        // From here on the work is done at Kafka; recording it must not be abandoned because the host is stopping,
        // or every acknowledged row would be sent a second time on the next start.
        long[] sent = [.. results.SelectMany(static r => r.Sent)];
        if (sent.Length > 0)
        {
            await outbox.Where(m => sent.Contains(m.Sequence)).ExecuteDeleteAsync(CancellationToken.None);
        }

        foreach ((_, OutboxMessage? failed, Exception? error) in results)
        {
            if (failed is not null)
            {
                await RecordFailureAsync(outbox, failed, error!);
            }
        }

        await transaction.CommitAsync(CancellationToken.None);

        var topic = new KeyValuePair<string, object?>("messaging.destination.name", options.Topic);
        MessagingTelemetry.Published.Add(sent.Length, topic);
        return sent.Length;
    }

    private async Task<KeyResult> SendKeyAsync(IEnumerable<OutboxMessage> rows, CancellationToken cancellationToken)
    {
        var sent = new List<long>();
        foreach (OutboxMessage row in rows)
        {
            try
            {
                await ProduceAsync(row, cancellationToken);
                sent.Add(row.Sequence);
            }
            catch (KafkaException ex)
            {
                return new KeyResult(sent, row, ex);
            }
        }

        return new KeyResult(sent, null, null);
    }

    private async Task ProduceAsync(OutboxMessage row, CancellationToken cancellationToken)
    {
        // The producer span continues the trace of the code that raised the event, and the envelope carries this
        // span on, so the consumer's span hangs under it. With no listener there is no span, and the raising
        // code's context goes out unchanged.
        using Activity? activity = MessagingTelemetry.Start($"{options.Topic} publish", ActivityKind.Producer, row.TraceParent, options.Topic);
        activity?.SetTag("messaging.message.id", row.Id.ToString());

        byte[] envelope = Envelope.Write(row.Id, row.Type, row.OccurredAt, activity?.Id ?? row.TraceParent, row.Payload);
        await producer.ProduceAsync(
            options.Topic,
            new Message<string, byte[]> { Key = row.PartitionKey, Value = envelope },
            cancellationToken);
    }

    private async Task RecordFailureAsync(DbSet<OutboxMessage> outbox, OutboxMessage row, Exception error)
    {
        int attempt = row.Attempts + 1;
        logger.ProduceFailed(row.Id, row.Type, attempt, error);
        MessagingTelemetry.PublishFailed.Add(1, new KeyValuePair<string, object?>("messaging.destination.name", options.Topic));

        DateTimeOffset next = DateTimeOffset.UtcNow + Backoff.For(attempt, options.RetryDelay, options.MaxRetryDelay);
        await outbox.Where(m => m.Sequence == row.Sequence).ExecuteUpdateAsync(
            setters => setters
                .SetProperty(m => m.Attempts, attempt)
                .SetProperty(m => m.LastError, error.Message)
                .SetProperty(m => m.NextAttemptAt, next),
            CancellationToken.None);
    }

    // Built once from the model, so a service that maps the outbox into its own schema is still found. Only the
    // table name is spliced in; the values are parameters.
    private string LockOldestPerKey(TDbContext db)
    {
        if (_lockOldestPerKey is { } sql)
        {
            return sql;
        }

        IEntityType entity = db.Model.FindEntityType(typeof(OutboxMessage))
            ?? throw new InvalidOperationException(
                $"{typeof(TDbContext).Name} does not map the outbox. Call modelBuilder.{nameof(MessagingModel.AddMessaging)}() in OnModelCreating.");
        string table = db.GetService<ISqlGenerationHelper>().DelimitIdentifier(entity.GetTableName()!, entity.GetSchema());

        return _lockOldestPerKey =
            "SELECT o.partition_key AS \"Value\" FROM " + table + " AS o " +
            "WHERE (o.next_attempt_at IS NULL OR o.next_attempt_at <= {1}) " +
            "AND NOT EXISTS (SELECT 1 FROM " + table + " AS p WHERE p.partition_key = o.partition_key AND p.sequence < o.sequence) " +
            "ORDER BY o.sequence LIMIT {0} " +
            "FOR UPDATE OF o SKIP LOCKED";
    }

    private readonly record struct KeyResult(List<long> Sent, OutboxMessage? Failed, Exception? Error);
}
