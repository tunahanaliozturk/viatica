using Microsoft.EntityFrameworkCore;
using Viatica.Booking.Domain;

namespace Viatica.Booking.Application.Features.Trips;

/// <summary>A resource and the row version behind its ETag. <see cref="IsNew"/> tells a 201 from a 200.</summary>
public sealed record Resource<T>(T Value, uint Version, bool IsNew = false);

internal static class TripAccess
{
    /// <summary>
    /// The trip, locked for the rest of the transaction, if the caller may act on it. Someone else's trip and a trip
    /// that does not exist look the same, so an id says nothing about whose it is.
    /// </summary>
    public static async Task<Trip> LoadForUpdateAsync(this IBookingDb db, Guid tripId, Caller caller, CancellationToken cancellationToken)
    {
        await db.LockTripAsync(tripId, cancellationToken);
        Trip? trip = await db.Trips.Include(static t => t.Items).FirstOrDefaultAsync(t => t.Id == tripId, cancellationToken);
        return trip is not null && caller.Owns(trip) ? trip : throw BookingErrors.TripNotFound();
    }

    /// <summary>The trip for a read: its owner, the holder of its key, or an admin.</summary>
    public static async Task<Trip> LoadForReadAsync(this IBookingDb db, Guid tripId, Caller caller, CancellationToken cancellationToken)
    {
        Trip? trip = await db.Trips.AsNoTracking().Include(static t => t.Items).FirstOrDefaultAsync(t => t.Id == tripId, cancellationToken);
        return trip is not null && caller.CanRead(trip) ? trip : throw BookingErrors.TripNotFound();
    }
}
