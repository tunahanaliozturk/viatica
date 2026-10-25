using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Viatica.Booking.Domain;

namespace Viatica.Booking.Application;

/// <summary>
/// What the clock alone causes, found and done one trip at a time: a trip that reached its deadline unwinds, a release
/// nobody answered is sent again, a cancellation its owner never confirmed is marked timed out. No event has to arrive
/// for any of it (docs/design.md, "Trip states"). The host's background service calls these on an interval.
/// </summary>
public sealed class ScheduledChecks(IBookingDb db, DirectivePublisher publisher, TimeProvider clock, ILogger<ScheduledChecks> logger)
{
    /// <summary>The ids of trips with something due, most overdue first. The next-check column is indexed for exactly this query.</summary>
    public async Task<IReadOnlyList<Guid>> FindDueAsync(int batchSize, CancellationToken cancellationToken)
    {
        DateTimeOffset now = clock.GetUtcNow();
        return await db.Trips.AsNoTracking()
            .Where(t => t.NextCheckAt != null && t.NextCheckAt <= now)
            .OrderBy(static t => t.NextCheckAt)
            .Select(static t => t.Id)
            .Take(batchSize)
            .ToListAsync(cancellationToken);
    }

    public async Task RunAsync(Guid tripId, CancellationToken cancellationToken)
    {
        await using var transaction = await db.Database.BeginTransactionAsync(cancellationToken);

        await db.LockTripAsync(tripId, cancellationToken);
        Trip? trip = await db.Trips.Include(static t => t.Items).FirstOrDefaultAsync(t => t.Id == tripId, cancellationToken);
        if (trip is null)
        {
            return;
        }

        TripStatus before = trip.Status;
        IReadOnlyList<SagaDirective> directives = trip.RunScheduledChecks(clock.GetUtcNow());
        if (directives.Count == 0)
        {
            return;
        }

        if (before != trip.Status && trip.Failure == FailureReason.DeadlineReached)
        {
            BookingTelemetry.Deadlines.Add(1, new KeyValuePair<string, object?>("stage", before.ToString()));
            logger.DeadlineReached(tripId, before);
        }

        publisher.Publish(trip, directives);
        await db.SaveChangesAsync(cancellationToken);
        await transaction.CommitAsync(cancellationToken);
    }
}

internal static partial class ScheduledChecksLog
{
    [LoggerMessage(1, LogLevel.Warning, "Trip {TripId} reached its deadline while {Stage}; it is being unwound")]
    public static partial void DeadlineReached(this ILogger logger, Guid tripId, TripStatus stage);
}
