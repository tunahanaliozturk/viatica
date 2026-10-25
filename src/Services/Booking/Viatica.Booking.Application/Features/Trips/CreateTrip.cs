using Microsoft.EntityFrameworkCore;
using Moongazing.OrionGuard.Core;
using Moongazing.OrionGuard.DependencyInjection;
using Relevox;
using Viatica.Booking.Domain;
using Viatica.Kernel;

namespace Viatica.Booking.Application.Features.Trips;

/// <summary>
/// Starts a draft trip under an id the client chose, so a retry after a lost response finds the trip it made
/// (BKG-1). A signed-in traveller owns it from the start. Anyone else sends a secret of their own in
/// <c>Trip-Access-Key</c>, which only a hash of is kept, and claims the trip when they sign in.
/// </summary>
public sealed record CreateTrip(Guid TripId, Caller Caller) : IRequest<Resource<TripView>>;

public sealed class CreateTripValidator : AbstractValidator<CreateTrip>
{
    public CreateTripValidator()
    {
        RuleFor(static c => c.TripId != Guid.Empty, "The trip id must not be empty.", nameof(CreateTrip.TripId)).WithErrorCode("InvalidTripId");
        RuleFor(
            static c => c.Caller.TravellerId is not null || c.Caller.AccessKey is { Length: >= Caller.MinimumAccessKeyLength and <= 128 },
            "An anonymous trip needs a Trip-Access-Key header of 22 to 128 characters that only you know. Sign in, or make one up with a random number generator.",
            "Trip-Access-Key").WithErrorCode("AccessKeyRequired");
    }
}

public sealed class CreateTripHandler(IBookingDb db, DirectivePublisher publisher, TimeProvider clock)
    : IRequestHandler<CreateTrip, Resource<TripView>>
{
    public async Task<Resource<TripView>> Handle(CreateTrip request, CancellationToken cancellationToken)
    {
        Trip? existing = await db.Trips.Include(static t => t.Items).AsNoTracking().FirstOrDefaultAsync(t => t.Id == request.TripId, cancellationToken);
        if (existing is not null)
        {
            // The same caller asking again is the retry this method exists for. Anyone else has chosen an id that is
            // taken; saying so is the honest answer to a request that cannot succeed, and ids are not guessable.
            return request.Caller.Owns(existing)
                ? new Resource<TripView>(Views.Of(existing), existing.Version)
                : throw ApiErrorException.Conflict(BookingErrorCode.TripAlreadyExists, "A trip with this id exists already.");
        }

        Guid? traveller = request.Caller.TravellerId;
        var trip = Trip.Create(
            request.TripId,
            traveller,
            traveller is null ? Caller.HashOf(request.Caller.AccessKey!) : null,
            clock.GetUtcNow());

        db.Trips.Add(trip);
        publisher.Publish(trip, trip.TakeDirectives());
        await db.SaveChangesAsync(cancellationToken);

        return new Resource<TripView>(Views.Of(trip), trip.Version, IsNew: true);
    }
}
