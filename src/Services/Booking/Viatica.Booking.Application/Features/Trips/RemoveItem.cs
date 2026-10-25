using Relevox;
using Viatica.Booking.Domain;

namespace Viatica.Booking.Application.Features.Trips;

/// <summary>Takes an item out of a draft. An item that is not there already is not an error: the caller wanted it gone.</summary>
public sealed record RemoveItem(Guid TripId, Guid ItemId, Caller Caller) : IRequest<bool>;

public sealed class RemoveItemHandler(IBookingDb db, DirectivePublisher publisher, TimeProvider clock) : IRequestHandler<RemoveItem, bool>
{
    public Task<bool> Handle(RemoveItem request, CancellationToken cancellationToken) =>
        db.InTransactionAsync(
            async () =>
            {
                Trip trip = await db.LoadForUpdateAsync(request.TripId, request.Caller, cancellationToken);
                bool removed = trip.RemoveItem(request.ItemId, clock.GetUtcNow());
                publisher.Publish(trip, trip.TakeDirectives());
                await db.SaveChangesAsync(cancellationToken);
                return removed;
            },
            cancellationToken);
}
