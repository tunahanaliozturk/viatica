using System.Globalization;
using Confluent.Kafka;
using Microsoft.Extensions.Logging;
using Viatica.Contracts;
using Viatica.Kernel.Messaging;
using Viatica.Search.Application;
using Viatica.Search.Application.Projection;
using Viatica.Search.Infrastructure.Indexing;

namespace Viatica.Search.Infrastructure.Messaging;

/// <summary>What a rebuild did, for the log and for tests.</summary>
public sealed record RebuildReport(
    IReadOnlyDictionary<IndexKind, string> NewIndices,
    IReadOnlyList<string> RemovedIndices,
    long EventsRead,
    TimeSpan Elapsed);

/// <summary>
/// Builds both indices again from the beginning of <c>stays.v1</c> and <c>cars.v1</c> and switches the aliases over
/// to them, while the service keeps answering queries from the old indices and keeps projecting live events.
/// </summary>
/// <remarks>
/// <para>
/// The order is what makes it safe. (1) Create empty indices nobody reads. (2) Replay each topic from its first
/// offset to the end as it was when the replay began, into the new indices, using a consumer group of its own so
/// the live group's offsets are untouched. (3) Replay what arrived meanwhile, until a pass finds almost nothing.
/// (4) Switch the aliases in one atomic request. (5) Replay once more from where step 3 stopped: events the live
/// consumer wrote to the old index just before the switch are in the topic after that offset, so they reach the new
/// index now. Every replay is safe to overlap with every other, because applying an event that is not newer than
/// the document changes nothing. (6) Delete the old indices.
/// </para>
/// <para>
/// A rebuild is only as complete as the topics: Kafka's retention (seven days by default) decides how far back the
/// replay can go. The listing events are full snapshots, so a long-lived property is rebuilt from its latest one as
/// long as that one is inside retention.
/// </para>
/// </remarks>
public sealed partial class RebuildRunner(
    IndexAdmin admin,
    OpenSearchRest rest,
    IntegrationEventTypes types,
    SearchMessagingOptions options,
    TimeProvider clock,
    ILogger<RebuildRunner> logger)
{
    // A pass that reads fewer events than this is "caught up enough" to switch the aliases; the replay after the
    // switch closes whatever gap is left.
    private const int CaughtUp = 1000;
    private const int MaxCatchUpPasses = 6;

    public async Task<RebuildReport> RunAsync(CancellationToken cancellationToken)
    {
        DateTimeOffset started = clock.GetUtcNow();
        Dictionary<IndexKind, string> created = [];
        foreach (IndexKind kind in Enum.GetValues<IndexKind>())
        {
            string name = admin.NewIndexName(kind);
            await admin.CreateAsync(kind, name, cancellationToken);
            created[kind] = name;
        }

        string group = $"search-rebuild-{started.ToString("yyyyMMddHHmmss", CultureInfo.InvariantCulture)}";
        LogStarted(logger, group, created[IndexKind.Stays], created[IndexKind.Cars]);

        try
        {
            var writer = new ProjectionWriter(
                new DocumentStore<StayDocument>(rest, created[IndexKind.Stays]),
                new DocumentStore<CarLocationDocument>(rest, created[IndexKind.Cars]),
                clock,
                measureLag: false);
            var applier = new BatchApplier(writer, types, options, logger);

            using IConsumer<string, byte[]> consumer = new ConsumerBuilder<string, byte[]>(new ConsumerConfig
            {
                BootstrapServers = options.BootstrapServers,
                GroupId = group,
                AutoOffsetReset = AutoOffsetReset.Earliest,
                EnableAutoCommit = false,
            }).Build();

            Dictionary<TopicPartition, long> position = [];
            long total = 0;
            for (int pass = 1; pass <= MaxCatchUpPasses; pass++)
            {
                long read = await ReplayAsync(consumer, applier, position, cancellationToken);
                total += read;
                LogPass(logger, pass, read);
                if (read < CaughtUp)
                {
                    break;
                }
            }

            foreach (string index in created.Values)
            {
                await admin.RefreshAsync(index, cancellationToken);
            }

            List<string> old = [];
            foreach (IndexKind kind in created.Keys)
            {
                old.AddRange(await admin.BehindAsync(IndexDefinitions.AliasOf(kind), cancellationToken));
            }

            await admin.SwapAsync(created, cancellationToken);
            LogSwapped(logger);

            total += await ReplayAsync(consumer, applier, position, cancellationToken);
            foreach (string index in created.Values)
            {
                await admin.RefreshAsync(index, cancellationToken);
            }

            foreach (string index in old)
            {
                await admin.DeleteAsync(index, cancellationToken);
            }

            SearchTelemetry.Rebuilds.Add(1, new KeyValuePair<string, object?>("result", "ok"));
            return new RebuildReport(created, old, total, clock.GetUtcNow() - started);
        }
        catch
        {
            SearchTelemetry.Rebuilds.Add(1, new KeyValuePair<string, object?>("result", "failed"));

            // Nothing reads the new indices before the switch, so a failed rebuild is undone by deleting them. After
            // the switch they are live and stay; the next rebuild replaces them.
            foreach ((IndexKind kind, string index) in created)
            {
                if (!(await admin.BehindAsync(IndexDefinitions.AliasOf(kind), CancellationToken.None)).Contains(index))
                {
                    await admin.DeleteAsync(index, CancellationToken.None);
                }
            }

            throw;
        }
    }

    /// <summary>
    /// Reads every partition from where <paramref name="position"/> left it to the end as it is now, applying the
    /// events. Returns how many messages that was.
    /// </summary>
    private async Task<long> ReplayAsync(
        IConsumer<string, byte[]> consumer,
        BatchApplier applier,
        Dictionary<TopicPartition, long> position,
        CancellationToken cancellationToken)
    {
        // The end as it is at this moment. Whatever arrives after is the next pass's business.
        Dictionary<TopicPartition, long> end = [];
        List<TopicPartitionOffset> assignment = [];
        foreach (string topic in (string[])[options.StaysTopic, options.CarsTopic])
        {
            using IAdminClient admin = new AdminClientBuilder(new AdminClientConfig { BootstrapServers = options.BootstrapServers }).Build();
            Metadata metadata = admin.GetMetadata(topic, TimeSpan.FromSeconds(30));
            foreach (PartitionMetadata partition in metadata.Topics[0].Partitions)
            {
                var tp = new TopicPartition(topic, partition.PartitionId);
                WatermarkOffsets marks = consumer.QueryWatermarkOffsets(tp, TimeSpan.FromSeconds(30));
                long from = Math.Max(position.GetValueOrDefault(tp, marks.Low), marks.Low);
                position[tp] = from;
                end[tp] = marks.High;
                if (from < marks.High)
                {
                    assignment.Add(new TopicPartitionOffset(tp, from));
                }
            }
        }

        if (assignment.Count == 0)
        {
            return 0;
        }

        consumer.Assign(assignment);
        HashSet<TopicPartition> open = [.. assignment.Select(static a => a.TopicPartition)];
        long read = 0;
        List<ConsumeResult<string, byte[]>> batch = [];

        DateTimeOffset lastProgress = clock.GetUtcNow();
        while (open.Count > 0)
        {
            ConsumeResult<string, byte[]>? result = consumer.Consume(TimeSpan.FromSeconds(5));
            if (result is null || result.IsPartitionEOF)
            {
                if (batch.Count > 0)
                {
                    await FlushAsync(applier, batch, cancellationToken);
                }

                // The ends were read from the broker a moment ago, so messages are there. Minutes without one
                // means the broker is not delivering, and waiting longer will not change that.
                if (clock.GetUtcNow() - lastProgress > TimeSpan.FromMinutes(2))
                {
                    throw new TimeoutException($"No message arrived for two minutes with {open.Count} partition(s) still to read.");
                }

                continue;
            }

            lastProgress = clock.GetUtcNow();

            TopicPartition tp = result.TopicPartition;
            if (!open.Contains(tp))
            {
                continue;
            }

            batch.Add(result);
            read++;
            position[tp] = result.Offset.Value + 1;
            if (position[tp] >= end[tp])
            {
                open.Remove(tp);
            }

            if (batch.Count >= options.BatchSize)
            {
                await FlushAsync(applier, batch, cancellationToken);
            }
        }

        await FlushAsync(applier, batch, cancellationToken);

        // The next pass assigns afresh. Anything this pass read past its end was skipped and is read again there.
        consumer.Unassign();
        return read;
    }

    private async Task FlushAsync(BatchApplier applier, List<ConsumeResult<string, byte[]>> batch, CancellationToken cancellationToken)
    {
        if (batch.Count == 0)
        {
            return;
        }

        IReadOnlyList<Poisoned> poisoned = await applier.ApplyAsync(batch, cancellationToken);
        batch.Clear();

        // A message nobody can read changed nothing in the live index either; the live consumer parked it. One that
        // was read but could not be applied is different: skipping it would build an index that differs from the one
        // it replaces and swap it in, so the rebuild stops.
        foreach (Poisoned unreadable in poisoned.Where(static p => p.Attempts == 0))
        {
            LogSkipped(logger, unreadable.Message.Topic, unreadable.Message.Partition.Value, unreadable.Message.Offset.Value);
        }

        if (poisoned.FirstOrDefault(static p => p.Attempts > 0) is { } first)
        {
            throw new InvalidOperationException(
                $"A message could not be applied: {first.Message.Topic}[{first.Message.Partition.Value}]@{first.Message.Offset.Value}: {first.Error.Message}",
                first.Error);
        }
    }

    [LoggerMessage(Level = LogLevel.Information, Message = "Rebuilding as consumer group {Group} into {Stays} and {Cars}")]
    private static partial void LogStarted(ILogger logger, string group, string stays, string cars);

    [LoggerMessage(Level = LogLevel.Information, Message = "Replay pass {Pass} read {Count} messages")]
    private static partial void LogPass(ILogger logger, int pass, long count);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Skipped the unreadable message {Topic}[{Partition}]@{Offset}")]
    private static partial void LogSkipped(ILogger logger, string topic, int partition, long offset);

    [LoggerMessage(Level = LogLevel.Information, Message = "Aliases switched to the new indices; replaying what arrived meanwhile")]
    private static partial void LogSwapped(ILogger logger);
}
