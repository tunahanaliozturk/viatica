using System.Diagnostics;
using System.Globalization;
using System.Text;
using Confluent.Kafka;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Viatica.Search.Application;

namespace Viatica.Search.Infrastructure.Messaging;

/// <summary>Where the service reads events from and how it batches them.</summary>
public sealed class SearchMessagingOptions
{
    public const string Section = "Search:Messaging";

    public string BootstrapServers { get; set; } = "";

    /// <summary>The topics read. Only tests change these, to give each test a topic of its own.</summary>
    public string StaysTopic { get; set; } = Viatica.Contracts.Topics.Stays;

    public string CarsTopic { get; set; } = Viatica.Contracts.Topics.Cars;

    /// <summary>The live consumer group; the design gives each service one group named after it.</summary>
    public string ConsumerGroup { get; set; } = "search";

    /// <summary>Most messages applied in one index round trip. Larger batches index faster and lag by a little more.</summary>
    public int BatchSize { get; set; } = 500;

    /// <summary>How long a consumer waits for the first message of a batch before checking whether it should stop.</summary>
    public TimeSpan PollTimeout { get; set; } = TimeSpan.FromMilliseconds(250);

    /// <summary>First wait while the index is unavailable; doubles up to <see cref="MaxRetryDelay"/>.</summary>
    public TimeSpan RetryDelay { get; set; } = TimeSpan.FromMilliseconds(500);

    public TimeSpan MaxRetryDelay { get; set; } = TimeSpan.FromSeconds(30);
}

/// <summary>
/// Reads one topic (<c>stays.v1</c> or <c>cars.v1</c>) as the <c>search</c> group and keeps the index current.
/// </summary>
/// <remarks>
/// <para>
/// Messages are read in batches, applied with <see cref="BatchApplier"/>, and only then do their offsets become
/// eligible for commit. A crash anywhere in between redelivers the batch, and the projection makes redelivery
/// harmless: an event older than the document is dropped. That is the whole idempotency story, which is why the
/// consumer has no inbox: there is no relational store to put one in, and none is needed.
/// </para>
/// <para>
/// Messages the batch applier gives up on go to <c>&lt;topic&gt;.dlq</c> with the error in headers, like every
/// other consumer of the platform.
/// </para>
/// </remarks>
internal sealed partial class ProjectionConsumer(
    string topic,
    BatchApplier applier,
    SearchMessagingOptions options,
    IProducer<string, byte[]> producer,
    ILogger<ProjectionConsumer> logger) : BackgroundService
{
    // Consume blocks its thread; running the loop off the host's startup path keeps one quiet topic from holding up
    // every other hosted service.
    protected override Task ExecuteAsync(CancellationToken stoppingToken) =>
        Task.Run(() => RunAsync(stoppingToken), CancellationToken.None);

    private async Task RunAsync(CancellationToken stoppingToken)
    {
        using IConsumer<string, byte[]> consumer = new ConsumerBuilder<string, byte[]>(new ConsumerConfig
        {
            BootstrapServers = options.BootstrapServers,
            GroupId = options.ConsumerGroup,
            AutoOffsetReset = AutoOffsetReset.Earliest,
            EnableAutoCommit = true,
            EnableAutoOffsetStore = false,

            // A batch waits for an index that may be restarting. Ten minutes is longer than a restart; past that the
            // group rebalances and the batch is redelivered, which is harmless.
            MaxPollIntervalMs = 600_000,
        }).Build();
        consumer.Subscribe(topic);
        LogStarted(logger, options.ConsumerGroup, topic);

        try
        {
            while (!stoppingToken.IsCancellationRequested)
            {
                List<ConsumeResult<string, byte[]>> batch = ReadBatch(consumer, out bool failed);
                if (failed)
                {
                    await Task.Delay(TimeSpan.FromSeconds(1), stoppingToken);
                }

                if (batch.Count == 0)
                {
                    continue;
                }

                using Activity? activity = SearchTelemetry.Source.StartActivity($"{topic} project", ActivityKind.Consumer);
                activity?.SetTag("messaging.system", "kafka");
                activity?.SetTag("messaging.destination.name", topic);
                activity?.SetTag("messaging.batch.message_count", batch.Count);

                foreach (Poisoned poison in await applier.ApplyAsync(batch, stoppingToken))
                {
                    await ParkAsync(poison, stoppingToken);
                }

                // The last message of each partition stands for everything before it on that partition.
                foreach (ConsumeResult<string, byte[]> last in batch.GroupBy(static r => r.Partition.Value).Select(static g => g.Last()))
                {
                    try
                    {
                        consumer.StoreOffset(last);
                    }
                    catch (KafkaException ex) when (ex.Error.Code == ErrorCode.Local_State)
                    {
                        // A rebalance took the partition while the batch was being applied. Whoever owns it now
                        // reads the batch again; applying it twice changes nothing.
                        LogOffsetNotStored(logger, last.Partition.Value, last.Offset.Value);
                    }
                }
            }
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
        {
            // Stopping. A batch cut short has no stored offset and comes back to whoever owns its partitions next.
        }
        finally
        {
            consumer.Close();
        }
    }

    private List<ConsumeResult<string, byte[]>> ReadBatch(IConsumer<string, byte[]> consumer, out bool failed)
    {
        failed = false;
        List<ConsumeResult<string, byte[]>> batch = [];
        try
        {
            ConsumeResult<string, byte[]>? first = consumer.Consume(options.PollTimeout);
            if (first is null || first.IsPartitionEOF)
            {
                return batch;
            }

            batch.Add(first);
            while (batch.Count < options.BatchSize && consumer.Consume(TimeSpan.Zero) is { IsPartitionEOF: false } next)
            {
                batch.Add(next);
            }
        }
        catch (ConsumeException ex)
        {
            // Typically the topic does not exist yet. The client keeps refreshing metadata; so do we.
            LogConsumeFailed(logger, topic, ex);
            failed = true;
        }

        return batch;
    }

    private async Task ParkAsync(Poisoned poison, CancellationToken cancellationToken)
    {
        ConsumeResult<string, byte[]> message = poison.Message;
        var headers = new Headers();
        foreach (IHeader header in message.Message.Headers ?? [])
        {
            headers.Add(header.Key, header.GetValueBytes());
        }

        // The original bytes go to the dead letter topic untouched; why they are there travels in headers.
        string error = poison.Error.Message.Length > 1000 ? poison.Error.Message[..1000] : poison.Error.Message;
        headers.Add("dlq-error-type", Encoding.UTF8.GetBytes(poison.Error.GetType().FullName ?? poison.Error.GetType().Name));
        headers.Add("dlq-error-message", Encoding.UTF8.GetBytes(error));
        headers.Add("dlq-attempts", Encoding.UTF8.GetBytes(poison.Attempts.ToString(CultureInfo.InvariantCulture)));
        headers.Add("dlq-topic", Encoding.UTF8.GetBytes(message.Topic));
        headers.Add("dlq-partition", Encoding.UTF8.GetBytes(message.Partition.Value.ToString(CultureInfo.InvariantCulture)));
        headers.Add("dlq-offset", Encoding.UTF8.GetBytes(message.Offset.Value.ToString(CultureInfo.InvariantCulture)));
        headers.Add("dlq-consumer-group", Encoding.UTF8.GetBytes(options.ConsumerGroup));

        var parked = new Message<string, byte[]> { Key = message.Message.Key, Value = message.Message.Value, Headers = headers };

        // Moving on without parking the message would lose it, so the partition waits for the dead letter topic.
        for (int attempt = 1; ; attempt++)
        {
            try
            {
                await producer.ProduceAsync(topic + ".dlq", parked, cancellationToken);
                break;
            }
            catch (KafkaException ex)
            {
                LogDeadLetterFailed(logger, topic + ".dlq", ex);
                await Task.Delay(options.RetryDelay, cancellationToken);
            }
        }

        LogDeadLettered(logger, message.Topic, message.Partition.Value, message.Offset.Value, poison.Error.GetType().Name);
    }

    [LoggerMessage(Level = LogLevel.Information, Message = "Projecting {Topic} as consumer group {Group}")]
    private static partial void LogStarted(ILogger logger, string group, string topic);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Reading {Topic} failed; trying again")]
    private static partial void LogConsumeFailed(ILogger logger, string topic, Exception exception);

    [LoggerMessage(Level = LogLevel.Information, Message = "Partition {Partition} moved away before offset {Offset} was stored; its new owner re-reads the batch")]
    private static partial void LogOffsetNotStored(ILogger logger, int partition, long offset);

    [LoggerMessage(Level = LogLevel.Error, Message = "Could not write to {Topic}; trying again")]
    private static partial void LogDeadLetterFailed(ILogger logger, string topic, Exception exception);

    [LoggerMessage(Level = LogLevel.Error, Message = "Parked {Topic}[{Partition}]@{Offset} on its dead letter topic after {ErrorType}")]
    private static partial void LogDeadLettered(ILogger logger, string topic, int partition, long offset, string errorType);
}
