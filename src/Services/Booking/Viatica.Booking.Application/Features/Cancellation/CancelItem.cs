using Relevox;
using Viatica.Booking.Application.Features.Trips;
using Viatica.Booking.Domain;
using Viatica.Kernel;

namespace Viatica.Booking.Application.Features.Cancellation;

/// <summary>
/// Asks the owner of one confirmed item to give it back (BKG-4). The item's own policy decides the refund, when the
/// owner has confirmed: see <see cref="CancellationPolicies"/> for stays and cars, and the airline's fare rules, which
/// Flights reports, for flights. Asking again while it is pending, or after it is done, changes nothing.
/// </summary>
public sealed record CancelItem(Guid TripId, Guid ItemId, Caller Caller) : IRequest<Operation>;

public sealed class CancelItemHandler(IBookingDb db, DirectivePublisher publisher, BookingOptions options, TimeProvider clock)
    : IRequestHandler<CancelItem, Operation>
{
    public Task<Operation> Handle(CancelItem request, CancellationToken cancellationToken) =>
        db.InTransactionAsync(
            async () =>
            {
                Trip trip = await db.LoadForUpdateAsync(request.TripId, request.Caller, cancellationToken);
                IReadOnlyList<SagaDirective> directives = trip.RequestCancellation(request.ItemId, clock.GetUtcNow(), options.CancelTimeout);

                if (directives.Count > 0)
                {
                    publisher.Publish(trip, directives);
                    BookingTelemetry.Cancellations.Add(1, new KeyValuePair<string, object?>("outcome", "requested"));
                    await db.SaveChangesAsync(cancellationToken);
                }

                return Views.CancellationOperation(trip, trip.Items.Single(i => i.Id == request.ItemId));
            },
            cancellationToken);
}
