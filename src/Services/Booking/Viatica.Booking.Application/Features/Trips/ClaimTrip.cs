using Microsoft.EntityFrameworkCore;
using Relevox;
using Viatica.Booking.Domain;

namespace Viatica.Booking.Application.Features.Trips;

/// <summary>
/// A traveller who signs in takes the trip they built before (BKG-1). The proof is the access key they made up; once the
/// trip is theirs the key is gone and the token is the only way in. Claiming your own trip again is a no-op.
/// </summary>
public sealed record ClaimTrip(Guid TripId, Caller Caller) : IRequest<Resource<TripView>>;

public sealed class ClaimTripHandler(IBookingDb db, DirectivePublisher publisher, TimeProvider clock)
    : IRequestHandler<ClaimTrip, Resource<TripView>>
{
    public Task<Resource<TripView>> Handle(ClaimTrip request, CancellationToken cancellationToken) =>
        db.InTransactionAsync(
            async () =>
            {
                Guid traveller = request.Caller.TravellerId ?? throw BookingErrors.TripNotFound();

                await db.LockTripAsync(request.TripId, cancellationToken);
                Trip trip = await db.Trips.Include(static t => t.Items).FirstOrDefaultAsync(t => t.Id == request.TripId, cancellationToken)
                    ?? throw BookingErrors.TripNotFound();

                // The key proves the trip is the caller's to take. A trip that someone else owns is "not found", the
                // same as one that does not exist.
                if (trip.TravellerId != traveller)
                {
                    if (trip.TravellerId is not null || !request.Caller.Owns(trip))
                    {
                        throw BookingErrors.TripNotFound();
                    }

                    trip.Claim(traveller, clock.GetUtcNow());
                    publisher.Publish(trip, trip.TakeDirectives());
                    await db.SaveChangesAsync(cancellationToken);
                }

                return new Resource<TripView>(Views.Of(trip), trip.Version);
            },
            cancellationToken);
}
