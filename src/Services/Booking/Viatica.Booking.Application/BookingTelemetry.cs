using System.Diagnostics;
using System.Diagnostics.Metrics;

namespace Viatica.Booking.Application;

/// <summary>
/// The service's one <see cref="ActivitySource"/> and one <see cref="Meter"/>, both named <c>Viatica.Booking</c>.
/// Tags carry outcome names and item kinds only: never a name, an email, a passenger or a payment token.
/// </summary>
public static class BookingTelemetry
{
    public const string Name = "Viatica.Booking";

    public static ActivitySource Source { get; } = new(Name);

    private static readonly Meter Meter = new(Name);

    /// <summary>Checkouts accepted, by whether the request started a saga or repeated one.</summary>
    public static Counter<long> Checkouts { get; } = Meter.CreateCounter<long>("booking.checkouts");

    /// <summary>Sagas that reached an end, by outcome (confirmed, failed) and the failure reason.</summary>
    public static Counter<long> Sagas { get; } = Meter.CreateCounter<long>("booking.sagas");

    /// <summary>Seconds from checkout to the saga's end, by outcome.</summary>
    public static Histogram<double> SagaDuration { get; } = Meter.CreateHistogram<double>("booking.saga.duration", unit: "s");

    /// <summary>Compensating requests sent (release, void, cancel) by kind, and how many of them were for an answer that came late.</summary>
    public static Counter<long> Compensations { get; } = Meter.CreateCounter<long>("booking.compensations");

    /// <summary>Events ignored because the trip had moved past them, by event type.</summary>
    public static Counter<long> Ignored { get; } = Meter.CreateCounter<long>("booking.events.ignored");

    public static Counter<long> Cancellations { get; } = Meter.CreateCounter<long>("booking.cancellations");

    public static Counter<long> Deadlines { get; } = Meter.CreateCounter<long>("booking.deadlines.reached");
}
