using System.Diagnostics;
using System.Diagnostics.Metrics;

namespace Viatica.Stays.Application;

/// <summary>
/// The service's own signals, under the one name the host registers: holds placed, rejected, committed, released,
/// expired and cancelled, and quotes issued. A rejected hold carries its reason, so a rise in
/// <c>SoldOut</c> reads differently from a rise in <c>QuoteInvalid</c>.
/// </summary>
public static class StaysTelemetry
{
    public const string Name = "Viatica.Stays";

    public static readonly ActivitySource Source = new(Name);

    private static readonly Meter Meter = new(Name);

    public static readonly Counter<long> HoldsPlaced =
        Meter.CreateCounter<long>("stays.holds.placed", "{hold}", "Holds placed.");

    public static readonly Counter<long> HoldsRejected =
        Meter.CreateCounter<long>("stays.holds.rejected", "{hold}", "Hold requests refused, by reason.");

    public static readonly Counter<long> HoldsCommitted =
        Meter.CreateCounter<long>("stays.holds.committed", "{hold}", "Holds turned into sales.");

    public static readonly Counter<long> HoldsReleased =
        Meter.CreateCounter<long>("stays.holds.released", "{hold}", "Holds given back before they expired.");

    public static readonly Counter<long> HoldsExpired =
        Meter.CreateCounter<long>("stays.holds.expired", "{hold}", "Holds the sweeper gave back.");

    public static readonly Counter<long> BookingsCancelled =
        Meter.CreateCounter<long>("stays.bookings.cancelled", "{booking}", "Committed stays cancelled.");

    public static readonly Counter<long> QuotesIssued =
        Meter.CreateCounter<long>("stays.quotes.issued", "{quote}", "Quotes issued.");
}
