namespace Viatica.Kernel.Messaging;

/// <summary>How a service reaches Kafka.</summary>
public sealed class MessagingOptions
{
    /// <summary>Comma separated <c>host:port</c> list of brokers.</summary>
    public string BootstrapServers { get; set; } = "";
}

/// <summary>Tuning for one <see cref="OutboxRelay{TDbContext}"/>.</summary>
public sealed class OutboxRelayOptions
{
    /// <summary>The topic every event of this outbox goes to, such as <c>stays.v1</c>.</summary>
    public required string Topic { get; init; }

    /// <summary>Most rows sent per round. Also the most keys locked per round.</summary>
    public int BatchSize { get; set; } = 500;

    /// <summary>
    /// How long an idle relay waits before looking again. A round that sent a full batch looks again at once.
    /// </summary>
    public TimeSpan PollInterval { get; set; } = TimeSpan.FromMilliseconds(100);

    /// <summary>First wait after a failed produce; doubles per attempt up to <see cref="MaxRetryDelay"/>.</summary>
    public TimeSpan RetryDelay { get; set; } = TimeSpan.FromSeconds(1);

    public TimeSpan MaxRetryDelay { get; set; } = TimeSpan.FromMinutes(1);
}

/// <summary>Tuning for one subscription: a topic read by one consumer group.</summary>
public sealed class SubscriptionOptions
{
    public required string Topic { get; init; }

    public required string ConsumerGroup { get; init; }

    /// <summary>Handler attempts before the message goes to <c>&lt;topic&gt;.dlq</c>.</summary>
    public int MaxAttempts { get; set; } = 5;

    /// <summary>Wait after the first failed attempt; doubles per attempt up to <see cref="MaxRetryDelay"/>.</summary>
    public TimeSpan RetryDelay { get; set; } = TimeSpan.FromMilliseconds(200);

    public TimeSpan MaxRetryDelay { get; set; } = TimeSpan.FromSeconds(10);

    internal string DeadLetterTopic => Topic + ".dlq";
}

internal static class Backoff
{
    public static TimeSpan For(int attempt, TimeSpan first, TimeSpan max) =>
        TimeSpan.FromTicks(Math.Min(max.Ticks, first.Ticks * (1L << Math.Clamp(attempt - 1, 0, 20))));
}
