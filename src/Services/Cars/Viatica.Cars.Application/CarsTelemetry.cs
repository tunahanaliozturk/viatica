using System.Diagnostics;
using System.Diagnostics.Metrics;

namespace Viatica.Cars.Application;

/// <summary>
/// The service's one <see cref="ActivitySource"/> and one <see cref="Meter"/>, both named <c>Viatica.Cars</c>. The
/// host adds that name to its OpenTelemetry setup. The counters are the ones the requirements ask for: holds
/// placed, rejected and expired.
/// </summary>
public static class CarsTelemetry
{
    public const string Name = "Viatica.Cars";

    public static readonly ActivitySource Source = new(Name);

    private static readonly Meter Meter = new(Name);

    public static readonly Counter<long> HoldsPlaced =
        Meter.CreateCounter<long>("cars.holds.placed", "{hold}", "Holds placed: a car is promised on every rental day.");

    public static readonly Counter<long> HoldsRejected =
        Meter.CreateCounter<long>("cars.holds.rejected", "{hold}", "Hold requests refused, by reason.");

    public static readonly Counter<long> HoldsExpired =
        Meter.CreateCounter<long>("cars.holds.expired", "{hold}", "Holds nobody confirmed in time.");

    public static readonly Counter<long> HoldsCommitted =
        Meter.CreateCounter<long>("cars.holds.committed", "{hold}", "Holds confirmed: the car counts as sold.");

    public static readonly Counter<long> QuotesIssued =
        Meter.CreateCounter<long>("cars.quotes.issued", "{quote}", "Quotes priced and signed.");
}
