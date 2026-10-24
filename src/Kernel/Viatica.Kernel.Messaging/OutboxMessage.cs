using System.Diagnostics;
using Viatica.Contracts;

namespace Viatica.Kernel.Messaging;

/// <summary>
/// An integration event waiting to be produced to Kafka. Written in the same transaction as the state change that
/// raised it, deleted by the relay once Kafka has acknowledged it.
/// </summary>
public sealed class OutboxMessage
{
    private OutboxMessage()
    {
    }

    /// <summary>
    /// Insert order, from an identity column. The relay sends a key's rows in this order. UUID v7 ids are not a
    /// substitute: two ids minted in the same millisecond compare in random order.
    /// </summary>
    public long Sequence { get; private set; }

    /// <summary>The event id, carried as the envelope <c>id</c>; consumers deduplicate on it.</summary>
    public Guid Id { get; private set; }

    public string Type { get; private set; } = "";

    public string PartitionKey { get; private set; } = "";

    /// <summary>The event's own JSON, the envelope <c>data</c>.</summary>
    public string Payload { get; private set; } = "";

    public DateTimeOffset OccurredAt { get; private set; }

    /// <summary>The W3C trace context of the code that raised the event, so the trace continues on the consumer.</summary>
    public string? TraceParent { get; private set; }

    /// <summary>Failed produce attempts so far. Only the oldest row of a key is ever attempted.</summary>
    public int Attempts { get; private set; }

    public string? LastError { get; private set; }

    /// <summary>When a row that failed may be tried again. Rows behind it on the same key wait with it.</summary>
    public DateTimeOffset? NextAttemptAt { get; private set; }

    internal static OutboxMessage For(IIntegrationEvent @event, IntegrationEventTypes types)
    {
        (string type, string data) = types.Serialize(@event);

        return new OutboxMessage
        {
            Id = Guid.CreateVersion7(),
            Type = type,
            PartitionKey = @event.PartitionKey,
            Payload = data,
            OccurredAt = DateTimeOffset.UtcNow,
            TraceParent = Activity.Current is { IdFormat: ActivityIdFormat.W3C } current ? current.Id : null,
        };
    }
}

/// <summary>
/// An event a consumer group has already applied. Its key is (consumer group, event id), and it is written in the
/// same transaction as the handler's changes, so "applied" and "recorded as applied" cannot disagree.
/// </summary>
public sealed class InboxMessage
{
    private InboxMessage()
    {
    }

    internal InboxMessage(string consumerGroup, Guid eventId, string type)
    {
        ConsumerGroup = consumerGroup;
        EventId = eventId;
        Type = type;
        ProcessedAt = DateTimeOffset.UtcNow;
    }

    public string ConsumerGroup { get; private set; } = "";

    public Guid EventId { get; private set; }

    public string Type { get; private set; } = "";

    public DateTimeOffset ProcessedAt { get; private set; }
}
