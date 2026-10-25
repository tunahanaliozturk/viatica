using Relevox;

namespace Viatica.Booking.Application.Features.Trips;

public sealed record GetTrip(Guid TripId, Caller Caller) : IRequest<Resource<TripView>>;

public sealed class GetTripHandler(IBookingDb db) : IRequestHandler<GetTrip, Resource<TripView>>
{
    public async Task<Resource<TripView>> Handle(GetTrip request, CancellationToken cancellationToken)
    {
        var trip = await db.LoadForReadAsync(request.TripId, request.Caller, cancellationToken);
        return new Resource<TripView>(Views.Of(trip), trip.Version);
    }
}
