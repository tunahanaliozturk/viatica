using System.Diagnostics;
using System.Diagnostics.Metrics;

namespace Viatica.AirlineSim;

/// <summary>
/// The sim's own signals. They answer one question a system test run raises: did the airline misbehave because we
/// told it to? Every injected delay, outage, timeout, refusal and price change is counted and traced by airline.
/// </summary>
internal static class Telemetry
{
    public const string Name = "Viatica.AirlineSim";

    public static readonly ActivitySource Source = new(Name);

    public static readonly Meter Meter = new(Name);

    public static readonly Histogram<double> InjectedLatency =
        Meter.CreateHistogram<double>("airlinesim.injected_latency", "ms", "Delay added to a supplier call on purpose.");

    public static readonly Counter<long> Faults =
        Meter.CreateCounter<long>("airlinesim.faults", "{fault}", "Failures injected by the behaviour knobs, by airline and kind.");

    public static readonly Counter<long> OrderTransitions =
        Meter.CreateCounter<long>("airlinesim.order.transitions", "{order}", "Orders booked, ticketed, voided and cancelled.");

    public static void Fault(string airline, string kind) =>
        Faults.Add(1, new("airline", airline), new("fault", kind));

    public static void Transition(string airline, string transition) =>
        OrderTransitions.Add(1, new("airline", airline), new("transition", transition));
}

/// <summary>
/// Source-generated log messages. Booking references and airline codes only: passenger names and birth dates are
/// personal data and never reach a log line.
/// </summary>
internal static partial class Log
{
    [LoggerMessage(1, LogLevel.Information, "{Airline} booked order {BookingReference} in {FareClass} class for {Seats} seats")]
    public static partial void OrderBooked(ILogger logger, string airline, string bookingReference, char fareClass, int seats);

    [LoggerMessage(2, LogLevel.Information, "{Airline} answered a repeated order creation with existing order {BookingReference}")]
    public static partial void OrderReplayed(ILogger logger, string airline, string bookingReference);

    [LoggerMessage(3, LogLevel.Information, "{Airline} ticketed order {BookingReference}")]
    public static partial void OrderTicketed(ILogger logger, string airline, string bookingReference);

    [LoggerMessage(4, LogLevel.Warning, "{Airline} refused ticketing for order {BookingReference} (injected)")]
    public static partial void TicketingRefused(ILogger logger, string airline, string bookingReference);

    [LoggerMessage(5, LogLevel.Information, "{Airline} voided order {BookingReference}")]
    public static partial void OrderVoided(ILogger logger, string airline, string bookingReference);

    [LoggerMessage(6, LogLevel.Information, "{Airline} cancelled order {BookingReference} with a refund of {Refund} EUR")]
    public static partial void OrderCancelled(ILogger logger, string airline, string bookingReference, decimal refund);

    [LoggerMessage(7, LogLevel.Warning, "{Airline} is timing out an order creation (injected); order created anyway: {CreatedAnyway}")]
    public static partial void OrderTimeout(ILogger logger, string airline, bool createdAnyway);

    [LoggerMessage(8, LogLevel.Information, "{Airline} knobs changed to {Knobs}")]
    public static partial void KnobsChanged(ILogger logger, string airline, string knobs);

    [LoggerMessage(9, LogLevel.Information, "All airline knobs reset to their defaults")]
    public static partial void KnobsReset(ILogger logger);

    [LoggerMessage(10, LogLevel.Information, "Database schema is in place")]
    public static partial void SchemaReady(ILogger logger);
}
