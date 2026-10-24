using System.Diagnostics;
using System.Diagnostics.Metrics;
using Viatica.Search.Application.Projection;

namespace Viatica.Search.Application;

/// <summary>
/// The one <see cref="ActivitySource"/> and the one <see cref="Meter"/> of the service, both named
/// <c>Viatica.Search</c>. The metric that matters is <c>viatica.search.projection.lag</c>: how long a change waited
/// between the moment its service committed it and the moment search had it.
/// </summary>
public static class SearchTelemetry
{
    public const string Name = "Viatica.Search";

    public static readonly ActivitySource Source = new(Name);

    private static readonly Meter Meter = new(Name);

    /// <summary>
    /// Event <c>occurredAt</c> to the write that carries it into the index being acknowledged. A document is
    /// searchable one refresh later (<c>refresh_interval</c>, 1 s), which this does not include. Dropped events are
    /// measured too: the projection is as current as its last handled event, applied or not.
    /// </summary>
    public static readonly Histogram<double> ProjectionLag = Meter.CreateHistogram<double>(
        "viatica.search.projection.lag", "s", "Seconds from an event's occurredAt to the index write that handled it.");

    public static readonly Counter<long> Events = Meter.CreateCounter<long>(
        "viatica.search.events", "{event}", "Events read from stays.v1 and cars.v1, by type and outcome (applied, dropped, ignored, dead_lettered).");

    public static readonly Counter<long> Rebuilds = Meter.CreateCounter<long>(
        "viatica.search.rebuilds", "{rebuild}", "Index rebuilds finished, by result.");

    public static void Handled(string type, ProjectionOutcome outcome, TimeSpan lag)
    {
        Events.Add(1, new("type", type), new("outcome", outcome == ProjectionOutcome.Applied ? "applied" : "dropped"));
        ProjectionLag.Record(Math.Max(0, lag.TotalSeconds), new KeyValuePair<string, object?>("type", type));
    }

    public static void Skipped(string type, string outcome) =>
        Events.Add(1, new("type", type), new("outcome", outcome));
}
