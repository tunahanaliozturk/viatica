using System.Diagnostics;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Viatica.Booking.Domain;

namespace Viatica.Booking.Application;

/// <summary>
/// Applies one cause to one trip: lock it, load it, let the aggregate decide, queue what it decided. Every handler of
/// an owner's answer goes through here, which is what makes them all behave the same way when the trip is missing
/// or the answer means nothing any more.
/// </summary>
public sealed class TripRunner(IBookingDb db, DirectivePublisher publisher, TimeProvider clock, ILogger<TripRunner> logger)
{
    public async Task ApplyAsync(
        Guid tripId,
        string cause,
        Func<Trip, DateTimeOffset, IReadOnlyList<SagaDirective>> step,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(step);
        using Activity? activity = BookingTelemetry.Source.StartActivity(cause);
        activity?.SetTag("booking.trip.id", tripId);

        // The consumer's transaction is open already. Locking first means a second handler for the same trip waits
        // here and then reads what the first one wrote, instead of racing it and retrying on a stale version.
        await db.LockTripAsync(tripId, cancellationToken);
        Trip? trip = await db.Trips.Include(static t => t.Items).FirstOrDefaultAsync(t => t.Id == tripId, cancellationToken);
        if (trip is null)
        {
            // Nothing here to act on, and nothing a retry would fix: the trip is created before any request leaves.
            logger.UnknownTrip(cause, tripId);
            BookingTelemetry.Ignored.Add(1, new KeyValuePair<string, object?>("cause", cause), new KeyValuePair<string, object?>("why", "unknown_trip"));
            return;
        }

        TripStatus before = trip.Status;
        int revision = trip.Revision;
        IReadOnlyList<SagaDirective> directives = step(trip, clock.GetUtcNow());

        if (directives.Count == 0 && revision == trip.Revision)
        {
            // The answer was one the trip had moved past, or one it had already taken: the redelivered, the reordered
            // and the late-but-harmless all end here, and the books are untouched.
            BookingTelemetry.Ignored.Add(1, new KeyValuePair<string, object?>("cause", cause), new KeyValuePair<string, object?>("why", "no_change"));
            logger.NoChange(cause, tripId, trip.Status);
            return;
        }

        publisher.Publish(trip, directives);
        Record(trip, before);
    }

    private void Record(Trip trip, TripStatus before)
    {
        if (before == trip.Status || trip.Status is not (TripStatus.Confirmed or TripStatus.Failed) || trip.CheckoutStartedAt is not { } started)
        {
            return;
        }

        string outcome = trip.Status.ToString().ToLowerInvariant();
        BookingTelemetry.Sagas.Add(
            1, new KeyValuePair<string, object?>("outcome", outcome), new KeyValuePair<string, object?>("reason", trip.Failure?.ToString() ?? "none"));
        BookingTelemetry.SagaDuration.Record((clock.GetUtcNow() - started).TotalSeconds, new KeyValuePair<string, object?>("outcome", outcome));
        logger.SagaEnded(trip.Id, trip.Status, trip.Failure?.ToString() ?? "none");
    }
}

internal static partial class TripRunnerLog
{
    [LoggerMessage(1, LogLevel.Warning, "{Cause} names trip {TripId}, which this service does not have; it is dropped")]
    public static partial void UnknownTrip(this ILogger logger, string cause, Guid tripId);

    [LoggerMessage(2, LogLevel.Debug, "{Cause} for trip {TripId} changed nothing; the trip is {Status}")]
    public static partial void NoChange(this ILogger logger, string cause, Guid tripId, TripStatus status);

    [LoggerMessage(3, LogLevel.Information, "Trip {TripId} ended {Status} (failure: {Reason})")]
    public static partial void SagaEnded(this ILogger logger, Guid tripId, TripStatus status, string reason);
}
