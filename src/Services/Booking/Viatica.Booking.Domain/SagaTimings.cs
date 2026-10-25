namespace Viatica.Booking.Domain;

/// <summary>The saga's clocks. Options in the host bind to this; tests pick their own.</summary>
/// <param name="DeadlineMargin">Taken off the shortest hold expiry or ticketing limit, so the unwinding finishes before an owner's sweeper acts.</param>
/// <param name="AssumedHoldLifetime">
/// How long a stay or car hold lives, from the moment it is placed. Stays and Cars both use 15 minutes and neither
/// says so in the hold event until it is placed, so the deadline starts from this and is tightened by what is reported.
/// </param>
/// <param name="ResendAfter">How long an unanswered release, void or cancel waits before it is sent again.</param>
/// <param name="CancelTimeout">How long a traveller's cancellation waits for its owner before it is marked timed out.</param>
public sealed record SagaTimings(
    TimeSpan DeadlineMargin,
    TimeSpan AssumedHoldLifetime,
    TimeSpan ResendAfter,
    TimeSpan CancelTimeout)
{
    public static SagaTimings Default { get; } = new(
        DeadlineMargin: TimeSpan.FromMinutes(2),
        AssumedHoldLifetime: TimeSpan.FromMinutes(15),
        ResendAfter: TimeSpan.FromSeconds(30),
        CancelTimeout: TimeSpan.FromMinutes(2));
}
