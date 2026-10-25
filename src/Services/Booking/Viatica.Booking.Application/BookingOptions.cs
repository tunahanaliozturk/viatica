using Viatica.Booking.Domain;

namespace Viatica.Booking.Application;

/// <summary>The saga's clocks and the scheduled check's rhythm. Section <c>Booking</c>; every value has a default.</summary>
public sealed class BookingOptions
{
    public const string Section = "Booking";

    /// <summary>Taken off the shortest hold expiry or ticketing limit to get the saga's deadline (docs/design.md: 2 minutes).</summary>
    public TimeSpan DeadlineMargin { get; set; } = TimeSpan.FromMinutes(2);

    /// <summary>How long Stays and Cars hold for. They fix it at 15 minutes and say so only in the hold event.</summary>
    public TimeSpan AssumedHoldLifetime { get; set; } = TimeSpan.FromMinutes(15);

    /// <summary>How long an unanswered release, void or cancel waits before it is sent again.</summary>
    public TimeSpan ResendAfter { get; set; } = TimeSpan.FromSeconds(30);

    /// <summary>How long a cancellation waits for its owner. Flights never answers a cancel for an order it did not make.</summary>
    public TimeSpan CancelTimeout { get; set; } = TimeSpan.FromMinutes(2);

    /// <summary>How often the scheduled check looks for trips with something due.</summary>
    public TimeSpan CheckInterval { get; set; } = TimeSpan.FromSeconds(5);

    public int CheckBatchSize { get; set; } = 50;

    /// <summary>How often a progress stream looks for a change. It reads one narrow column; a change sends the whole trip.</summary>
    public TimeSpan ProgressPollInterval { get; set; } = TimeSpan.FromMilliseconds(500);

    /// <summary>How long a progress stream may stay silent before it sends a comment line to keep the connection open.</summary>
    public TimeSpan ProgressKeepAlive { get; set; } = TimeSpan.FromSeconds(15);

    public SagaTimings Timings => new(DeadlineMargin, AssumedHoldLifetime, ResendAfter, CancelTimeout);
}
