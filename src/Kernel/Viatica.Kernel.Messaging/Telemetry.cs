using System.Collections.Concurrent;
using System.Diagnostics;
using System.Diagnostics.Metrics;

namespace Viatica.Kernel.Messaging;

/// <summary>
/// The one <see cref="ActivitySource"/> and the one <see cref="Meter"/> of the messaging library, both named
/// <c>Viatica.Messaging</c>. A host adds that name to its OpenTelemetry tracing and metrics and gets every hop.
/// </summary>
public static class MessagingTelemetry
{
    public const string Name = "Viatica.Messaging";

    internal static readonly ActivitySource Source = new(Name);

    private static readonly Meter Meter = new(Name);

    // One entry per running relay, keyed by the relay itself: two relays of one DbContext type (one per topic, or two
    // hosts in a test) must not share an entry, and one stopping must not remove the other's. The gauge reads whatever
    // the relay measured last, so collecting metrics never runs a query of its own.
    private static readonly ConcurrentDictionary<object, (string Outbox, string Topic, DateTimeOffset? Oldest)> OldestPending = new(ReferenceEqualityComparer.Instance);

    internal static readonly Counter<long> Published = Meter.CreateCounter<long>(
        "viatica.messaging.published", "{message}", "Outbox rows produced to Kafka and acknowledged by every in-sync replica.");

    internal static readonly Counter<long> PublishFailed = Meter.CreateCounter<long>(
        "viatica.messaging.publish_failed", "{message}", "Outbox rows whose produce failed and will be retried.");

    internal static readonly Counter<long> Consumed = Meter.CreateCounter<long>(
        "viatica.messaging.consumed", "{message}", "Messages a subscription finished with, by outcome.");

    internal static readonly Counter<long> HandlerFailed = Meter.CreateCounter<long>(
        "viatica.messaging.handler_failed", "{attempt}", "Handler attempts that threw.");

    // Declared after OldestPending: static fields initialise in order, and the callback reads that dictionary.
    private static readonly ObservableGauge<double> OutboxAge = Meter.CreateObservableGauge(
        "viatica.outbox.age",
        ObserveOutboxAge,
        "s",
        "Age of the oldest row still waiting in an outbox; zero when the outbox is empty.");

    internal static void ReportOldestPending(object relay, string outbox, string topic, DateTimeOffset? oldest) =>
        OldestPending[relay] = (outbox, topic, oldest);

    internal static void ForgetOutbox(object relay) => OldestPending.TryRemove(relay, out _);

    private static IEnumerable<Measurement<double>> ObserveOutboxAge()
    {
        DateTimeOffset now = DateTimeOffset.UtcNow;

        // Relays that share a context type and a topic (two hosts in one process) are one series; the worst age wins.
        foreach (IGrouping<(string Outbox, string Topic), (string Outbox, string Topic, DateTimeOffset? Oldest)> series in
                 OldestPending.Values.GroupBy(static r => (r.Outbox, r.Topic)))
        {
            double age = series.Max(r => r.Oldest is { } at ? Math.Max(0, (now - at).TotalSeconds) : 0);
            yield return new Measurement<double>(
                age,
                new KeyValuePair<string, object?>("outbox", series.Key.Outbox),
                new KeyValuePair<string, object?>("messaging.destination.name", series.Key.Topic));
        }
    }

    /// <summary>Starts a span whose parent is the W3C <paramref name="traceParent"/>, when there is one.</summary>
    internal static Activity? Start(string name, ActivityKind kind, string? traceParent, string topic)
    {
        ActivityContext.TryParse(traceParent, null, out ActivityContext parent);
        Activity? activity = Source.StartActivity(name, kind, parent);
        activity?.SetTag("messaging.system", "kafka");
        activity?.SetTag("messaging.destination.name", topic);
        return activity;
    }
}
