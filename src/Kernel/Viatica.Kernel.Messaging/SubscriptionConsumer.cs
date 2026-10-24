using System.Diagnostics;
using System.Text;
using System.Text.Json;
using Confluent.Kafka;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Viatica.Contracts;

namespace Viatica.Kernel.Messaging;

/// <summary>Resolves the handler for one event type from a message scope and runs it.</summary>
internal delegate Task HandleEvent(IServiceProvider services, IIntegrationEvent message, CancellationToken cancellationToken);

/// <summary>
/// Reads one topic as one consumer group and hands each event to its handler. One instance, and one hosted
/// service, per subscription.
/// </summary>
/// <remarks>
/// <para>
/// The offset of a message is stored only after its outcome is final: applied and committed with its inbox row,
/// found in the inbox already, ignored because this service has no handler for its type, or parked on the dead
/// letter topic. A crash anywhere before that redelivers the message, and the inbox makes the redelivery a no-op.
/// </para>
/// <para>
/// Messages of a partition are handled one at a time, in order. A failing handler is retried on the spot with
/// backoff, which holds the partition for at most the configured attempts; then the message is parked on
/// <c>&lt;topic&gt;.dlq</c> and the partition moves on. A message that cannot be read at all is parked without
/// retries, since reading it again gives the same answer.
/// </para>
/// </remarks>
public sealed class SubscriptionConsumer<TDbContext> : BackgroundService
    where TDbContext : DbContext
{
    private readonly SubscriptionOptions _options;
    private readonly IReadOnlyDictionary<Type, HandleEvent> _handlers;
    private readonly MessagingOptions _messaging;
    private readonly IntegrationEventTypes _types;
    private readonly IProducer<string, byte[]> _producer;
    private readonly IServiceScopeFactory _scopes;
    private readonly ILogger _logger;

    internal SubscriptionConsumer(
        SubscriptionOptions options,
        IReadOnlyDictionary<Type, HandleEvent> handlers,
        IServiceProvider services)
    {
        _options = options;
        _handlers = handlers;
        _messaging = services.GetRequiredService<MessagingOptions>();
        _types = services.GetRequiredService<IntegrationEventTypes>();
        _producer = services.GetRequiredService<IProducer<string, byte[]>>();
        _scopes = services.GetRequiredService<IServiceScopeFactory>();
        _logger = services.GetRequiredService<ILogger<SubscriptionConsumer<TDbContext>>>();
    }

    // Consume blocks its thread until a message arrives; running the loop off the host's startup path keeps one
    // quiet topic from holding up every other hosted service.
    protected override Task ExecuteAsync(CancellationToken stoppingToken) => Task.Run(() => RunAsync(stoppingToken), CancellationToken.None);

    private async Task RunAsync(CancellationToken stoppingToken)
    {
        using IConsumer<string, byte[]> consumer = CreateConsumer();
        ConsumerCreated?.Invoke(consumer);
        consumer.Subscribe(_options.Topic);
        _logger.SubscriptionStarted(_options.ConsumerGroup, _options.Topic);

        try
        {
            while (!stoppingToken.IsCancellationRequested)
            {
                ConsumeResult<string, byte[]> result;
                try
                {
                    result = consumer.Consume(stoppingToken);
                }
                catch (ConsumeException ex)
                {
                    // Typically the topic does not exist yet. The client keeps refreshing metadata; so do we.
                    _logger.ConsumeFailed(_options.Topic, ex);
                    await Task.Delay(TimeSpan.FromSeconds(1), stoppingToken);
                    continue;
                }

                await ProcessAsync(result, stoppingToken);
                try
                {
                    consumer.StoreOffset(result);
                }
                catch (KafkaException ex) when (ex.Error.Code == ErrorCode.Local_State)
                {
                    // A rebalance took the partition away while the message was being handled. The new owner gets
                    // it again from the last committed offset, and the inbox turns that into a no-op.
                    _logger.OffsetNotStored(result.Topic, result.Partition.Value, result.Offset.Value);
                }
            }
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
        {
            // Stopping. A message cut short has no stored offset and comes back to whoever owns its partition next.
        }
        finally
        {
            // Close commits the stored offsets and leaves the group at once, instead of making the group wait for
            // a session timeout before it hands these partitions to another instance.
            consumer.Close();
        }
    }

    /// <summary>Hands a test the Kafka client the loop reads from, so it can take a partition away mid-handler.</summary>
    internal Action<IConsumer<string, byte[]>>? ConsumerCreated { get; set; }

    /// <summary>
    /// Auto commit stays on, but it commits only offsets stored by <see cref="RunAsync"/> after a final outcome,
    /// so it commits nothing early. It saves a broker round trip per message over committing each one by hand.
    /// </summary>
    internal IConsumer<string, byte[]> CreateConsumer() =>
        new ConsumerBuilder<string, byte[]>(new ConsumerConfig
        {
            BootstrapServers = _messaging.BootstrapServers,
            GroupId = _options.ConsumerGroup,
            AutoOffsetReset = AutoOffsetReset.Earliest,
            EnableAutoCommit = true,
            EnableAutoOffsetStore = false,
        }).Build();

    /// <summary>Takes one message to a final outcome. Returns only when its offset may be stored.</summary>
    internal async Task ProcessAsync(ConsumeResult<string, byte[]> result, CancellationToken cancellationToken)
    {
        Incoming incoming;
        try
        {
            incoming = Envelope.Read(result.Message.Value, _types);
        }
        catch (JsonException ex)
        {
            await DeadLetterAsync(result, ex, attempts: 0, cancellationToken);
            return;
        }

        if (incoming.Event is null || !_handlers.TryGetValue(incoming.Event.GetType(), out HandleEvent? handler))
        {
            // Topics carry every event of the producing service; this one is for someone else.
            Count("ignored");
            return;
        }

        using Activity? activity = MessagingTelemetry.Start($"{_options.Topic} process", ActivityKind.Consumer, incoming.TraceParent, _options.Topic);
        activity?.SetTag("messaging.message.id", incoming.Id.ToString());
        activity?.SetTag("messaging.consumer.group.name", _options.ConsumerGroup);

        for (int attempt = 1; ; attempt++)
        {
            try
            {
                Count(await ApplyAsync(incoming, handler, cancellationToken) ? "applied" : "duplicate");
                return;
            }
            catch (Exception ex) when (!cancellationToken.IsCancellationRequested)
            {
                // Anything a handler throws counts against the attempts, an HttpClient timeout included; only the
                // stopping token means shutdown.
                _logger.HandlerFailed(incoming.Type, incoming.Id, attempt, _options.MaxAttempts, ex);
                MessagingTelemetry.HandlerFailed.Add(1, new KeyValuePair<string, object?>("messaging.destination.name", _options.Topic));

                if (attempt >= _options.MaxAttempts)
                {
                    activity?.SetStatus(ActivityStatusCode.Error, ex.GetType().Name);
                    await DeadLetterAsync(result, ex, attempt, cancellationToken);
                    return;
                }

                await Task.Delay(Backoff.For(attempt, _options.RetryDelay, _options.MaxRetryDelay), cancellationToken);
            }
        }
    }

    // True when applied now, false when the inbox shows it was applied before.
    private async Task<bool> ApplyAsync(Incoming incoming, HandleEvent handler, CancellationToken cancellationToken)
    {
        await using AsyncServiceScope scope = _scopes.CreateAsyncScope();
        TDbContext db = scope.ServiceProvider.GetRequiredService<TDbContext>();

        if (await AlreadyAppliedAsync(db, incoming.Id, cancellationToken))
        {
            _logger.Duplicate(incoming.Type, incoming.Id, _options.ConsumerGroup);
            return false;
        }

        // Explicit, so a handler that saves on its own (to read a generated key, say) is still inside it.
        await using IDbContextTransaction transaction = await db.Database.BeginTransactionAsync(cancellationToken);
        await handler(scope.ServiceProvider, incoming.Event!, cancellationToken);
        db.Set<InboxMessage>().Add(new InboxMessage(_options.ConsumerGroup, incoming.Id, incoming.Type));

        try
        {
            await db.SaveChangesAsync(cancellationToken);
        }
        catch (DbUpdateException)
        {
            // The previous owner of this partition, before a rebalance, may have applied the event between the
            // check above and this insert. Only the inbox can say; any other failure is the handler's and is
            // retried like one.
            await transaction.RollbackAsync(CancellationToken.None);
            if (await AlreadyAppliedAsync(db, incoming.Id, cancellationToken))
            {
                return false;
            }

            throw;
        }

        await transaction.CommitAsync(CancellationToken.None);
        return true;
    }

    private Task<bool> AlreadyAppliedAsync(TDbContext db, Guid eventId, CancellationToken cancellationToken) =>
        db.Set<InboxMessage>().AsNoTracking()
            .AnyAsync(m => m.ConsumerGroup == _options.ConsumerGroup && m.EventId == eventId, cancellationToken);

    private async Task DeadLetterAsync(ConsumeResult<string, byte[]> result, Exception error, int attempts, CancellationToken cancellationToken)
    {
        var headers = new Headers();
        foreach (IHeader header in result.Message.Headers ?? [])
        {
            headers.Add(header.Key, header.GetValueBytes());
        }

        // The original bytes go to the dead letter topic untouched; why they are there travels in headers, so a
        // replay tool can put the message back as it was.
        string message = error.Message.Length > 1000 ? error.Message[..1000] : error.Message;
        headers.Add("dlq-error-type", Encoding.UTF8.GetBytes(error.GetType().FullName ?? error.GetType().Name));
        headers.Add("dlq-error-message", Encoding.UTF8.GetBytes(message));
        headers.Add("dlq-attempts", Encoding.UTF8.GetBytes(attempts.ToString(System.Globalization.CultureInfo.InvariantCulture)));
        headers.Add("dlq-topic", Encoding.UTF8.GetBytes(result.Topic));
        headers.Add("dlq-partition", Encoding.UTF8.GetBytes(result.Partition.Value.ToString(System.Globalization.CultureInfo.InvariantCulture)));
        headers.Add("dlq-offset", Encoding.UTF8.GetBytes(result.Offset.Value.ToString(System.Globalization.CultureInfo.InvariantCulture)));
        headers.Add("dlq-consumer-group", Encoding.UTF8.GetBytes(_options.ConsumerGroup));

        var parked = new Message<string, byte[]> { Key = result.Message.Key, Value = result.Message.Value, Headers = headers };

        // Moving on without parking the message would lose it, so the partition waits for the dead letter topic.
        for (int attempt = 1; ; attempt++)
        {
            try
            {
                await _producer.ProduceAsync(_options.DeadLetterTopic, parked, cancellationToken);
                break;
            }
            catch (KafkaException ex)
            {
                _logger.DeadLetterFailed(_options.DeadLetterTopic, ex);
                await Task.Delay(Backoff.For(attempt, _options.RetryDelay, _options.MaxRetryDelay), cancellationToken);
            }
        }

        _logger.DeadLettered(result.Topic, result.Partition.Value, result.Offset.Value, _options.DeadLetterTopic, error.GetType().Name);
        Count("dead_lettered");
    }

    private void Count(string outcome) =>
        MessagingTelemetry.Consumed.Add(
            1,
            new KeyValuePair<string, object?>("messaging.destination.name", _options.Topic),
            new KeyValuePair<string, object?>("messaging.consumer.group.name", _options.ConsumerGroup),
            new KeyValuePair<string, object?>("outcome", outcome));
}
