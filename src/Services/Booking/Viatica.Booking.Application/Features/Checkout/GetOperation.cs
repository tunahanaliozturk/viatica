using Microsoft.EntityFrameworkCore;
using Relevox;
using Viatica.Booking.Application.Features.Trips;
using Viatica.Booking.Domain;
using Viatica.Kernel;

namespace Viatica.Booking.Application.Features.Checkout;

/// <summary>
/// Where a long running operation stands. The id is a trip's (its checkout) or an item's (its cancellation): both are
/// ids the client chose, so the client already has the id of the thing it started.
/// </summary>
public sealed record GetOperation(Guid Id, Caller Caller) : IRequest<Operation>;

public sealed class GetOperationHandler(IBookingDb db) : IRequestHandler<GetOperation, Operation>
{
    public async Task<Operation> Handle(GetOperation request, CancellationToken cancellationToken)
    {
        Trip? byTrip = await db.Trips.AsNoTracking().Include(static t => t.Items).FirstOrDefaultAsync(t => t.Id == request.Id, cancellationToken);
        if (byTrip is not null)
        {
            return request.Caller.CanRead(byTrip) ? Views.CheckoutOperation(byTrip) : throw BookingErrors.TripNotFound();
        }

        Guid? tripId = await db.Items.AsNoTracking().Where(i => i.Id == request.Id).Select(static i => (Guid?)i.TripId).FirstOrDefaultAsync(cancellationToken);
        if (tripId is null)
        {
            throw BookingErrors.TripNotFound();
        }

        Trip trip = await db.LoadForReadAsync(tripId.Value, request.Caller, cancellationToken);
        return Views.CancellationOperation(trip, trip.Items.Single(i => i.Id == request.Id));
    }
}
