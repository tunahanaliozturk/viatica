using Microsoft.Extensions.Logging;

namespace Viatica.Kernel.Messaging;

// Ids, types, topics and offsets only. Event data never reaches a log line: it can carry passenger details.
internal static partial class Log
{
    [LoggerMessage(1, LogLevel.Information, "Outbox relay for {Outbox} started, producing to {Topic}")]
    public static partial void RelayStarted(this ILogger logger, string outbox, string topic);

    [LoggerMessage(2, LogLevel.Error, "Outbox relay round for {Outbox} failed; trying again")]
    public static partial void RelayRoundFailed(this ILogger logger, string outbox, Exception exception);

    [LoggerMessage(3, LogLevel.Warning, "Producing outbox row {EventId} ({Type}) failed on attempt {Attempt}; the rows behind it on its key wait")]
    public static partial void ProduceFailed(this ILogger logger, Guid eventId, string type, int attempt, Exception exception);

    [LoggerMessage(10, LogLevel.Information, "Subscription {ConsumerGroup} on {Topic} started")]
    public static partial void SubscriptionStarted(this ILogger logger, string consumerGroup, string topic);

    [LoggerMessage(11, LogLevel.Warning, "Consuming from {Topic} failed; carrying on")]
    public static partial void ConsumeFailed(this ILogger logger, string topic, Exception exception);

    [LoggerMessage(12, LogLevel.Warning, "Handler for {Type} {EventId} failed on attempt {Attempt} of {MaxAttempts}")]
    public static partial void HandlerFailed(this ILogger logger, string type, Guid eventId, int attempt, int maxAttempts, Exception exception);

    [LoggerMessage(13, LogLevel.Error, "Message at {Topic}/{Partition}@{Offset} sent to {DeadLetterTopic}: {Reason}")]
    public static partial void DeadLettered(this ILogger logger, string topic, int partition, long offset, string deadLetterTopic, string reason);

    [LoggerMessage(14, LogLevel.Debug, "{Type} {EventId} already applied by {ConsumerGroup}; skipped")]
    public static partial void Duplicate(this ILogger logger, string type, Guid eventId, string consumerGroup);

    [LoggerMessage(15, LogLevel.Error, "Producing to {DeadLetterTopic} failed; the partition waits until it succeeds")]
    public static partial void DeadLetterFailed(this ILogger logger, string deadLetterTopic, Exception exception);
}
