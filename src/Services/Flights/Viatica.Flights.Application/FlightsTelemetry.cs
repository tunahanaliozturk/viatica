using System.Diagnostics;
using System.Diagnostics.Metrics;

namespace Viatica.Flights.Application;

/// <summary>
/// The service's one <see cref="ActivitySource"/> and one <see cref="Meter"/>, both named <c>Viatica.Flights</c>.
/// Tags carry airline codes and outcome names only: never an offer id, a booking reference or anything about a
/// passenger.
/// </summary>
public static class FlightsTelemetry
{
    public const string Name = "Viatica.Flights";

    public static ActivitySource Source { get; } = new(Name);

    private static readonly Meter Meter = new(Name);

    /// <summary>Every call to an airline, by airline, operation and outcome: the supplier error rate.</summary>
    public static Counter<long> SupplierCalls { get; } = Meter.CreateCounter<long>("flights.supplier.calls");

    /// <summary>How long an airline took to answer, by airline and operation.</summary>
    public static Histogram<double> SupplierLatency { get; } =
        Meter.CreateHistogram<double>("flights.supplier.duration", unit: "ms");

    /// <summary>Searches by whether every airline answered in time.</summary>
    public static Counter<long> Searches { get; } = Meter.CreateCounter<long>("flights.searches");

    public static Histogram<double> SearchDuration { get; } =
        Meter.CreateHistogram<double>("flights.search.duration", unit: "ms");

    /// <summary>Airlines left out of a search, by airline and reason.</summary>
    public static Counter<long> IncompleteSuppliers { get; } = Meter.CreateCounter<long>("flights.search.incomplete");

    /// <summary>Re-prices by result: unchanged, changed, gone, unavailable.</summary>
    public static Counter<long> Reprices { get; } = Meter.CreateCounter<long>("flights.offers.repriced");

    /// <summary>Order requests by outcome: created, or rejected with the reason.</summary>
    public static Counter<long> Orders { get; } = Meter.CreateCounter<long>("flights.orders");

    /// <summary>Ticketing, void and cancel requests by operation and outcome.</summary>
    public static Counter<long> OrderOperations { get; } = Meter.CreateCounter<long>("flights.order.operations");
}
