using Moongazing.OrionGuard.Core;
using Moongazing.OrionGuard.DependencyInjection;
using Relevox;
using Viatica.Booking.Domain;
using Viatica.Kernel;

namespace Viatica.Booking.Application.Features.Trips;

/// <summary>
/// What goes into the basket. A stay or a car carries the quote token its owner issued, and Booking reads what is in
/// it (the dates, the price, how many rooms, the driver's age) without asking anyone. A flight carries the offer id
/// that Flights re-priced, and the total the traveller was shown for it.
/// </summary>
public sealed record ItemSpec(ItemKind Kind, string? QuoteToken = null, string? OfferId = null, Money? ExpectedTotal = null, string? Description = null);

public sealed record AddItem(Guid TripId, Guid ItemId, ItemSpec Spec, Caller Caller) : IRequest<Resource<TripView>>;

public sealed class AddItemValidator : AbstractValidator<AddItem>
{
    public AddItemValidator()
    {
        RuleFor(static c => c.ItemId != Guid.Empty, "The item id must not be empty.", nameof(AddItem.ItemId)).WithErrorCode("InvalidItemId");
        RuleFor(static c => Enum.IsDefined(c.Spec.Kind), "kind is Stay, Car or Flight.", "kind").WithErrorCode("InvalidKind");
        RuleFor(
            static c => c.Spec.Kind == ItemKind.Flight || c.Spec.QuoteToken is { Length: > 0 and <= 2048 },
            "A stay or a car needs the quoteToken from its quote.",
            "quoteToken").WithErrorCode("QuoteTokenRequired");
        RuleFor(
            static c => c.Spec.Kind != ItemKind.Flight || c.Spec.OfferId is { Length: > 0 and <= 64 },
            "A flight needs the offerId that was re-priced, at most 64 characters.",
            "offerId").WithErrorCode("OfferIdRequired");
        RuleFor(
            static c => c.Spec.Kind != ItemKind.Flight || c.Spec.ExpectedTotal is { Amount: > 0m, Currency: Money.Euro },
            "A flight needs the expectedTotal the traveller was shown, above zero, in EUR.",
            "expectedTotal").WithErrorCode("ExpectedTotalRequired");
        RuleFor(static c => c.Spec.Description is null || c.Spec.Description.Length <= 200, "description is at most 200 characters.", "description")
            .WithErrorCode("InvalidDescription");
    }
}

public sealed class AddItemHandler(IBookingDb db, DirectivePublisher publisher, TimeProvider clock)
    : IRequestHandler<AddItem, Resource<TripView>>
{
    public async Task<Resource<TripView>> Handle(AddItem request, CancellationToken cancellationToken)
    {
        DateTimeOffset now = clock.GetUtcNow();
        NewItem item = Describe(request, now);

        return await db.InTransactionAsync(
            async () =>
            {
                Trip trip = await db.LoadForUpdateAsync(request.TripId, request.Caller, cancellationToken);
                AddItemOutcome outcome = trip.AddItem(item, now);
                publisher.Publish(trip, trip.TakeDirectives());
                await db.SaveChangesAsync(cancellationToken);
                return new Resource<TripView>(Views.Of(trip), trip.Version, IsNew: outcome == AddItemOutcome.Added);
            },
            cancellationToken);
    }

    private static NewItem Describe(AddItem request, DateTimeOffset now)
    {
        ItemSpec spec = request.Spec;

        if (spec.Kind == ItemKind.Flight)
        {
            return new NewItem(
                request.ItemId, ItemKind.Flight, spec.OfferId!, spec.Description is { Length: > 0 } text ? text : "Flight", spec.ExpectedTotal!, 1, null, null);
        }

        // Read, not trusted: the owner checks the signature and the expiry when it places the hold.
        if (!QuotePreview.TryRead(spec.QuoteToken, out QuotePreview? preview) || preview!.Kind != spec.Kind || preview.Total.Currency != Money.Euro)
        {
            throw ApiErrorException.Invalid(BookingErrorCode.QuoteInvalid, "The quote token is not a quote for this kind of item.", "quoteToken");
        }

        if (preview.ExpiresAt <= now)
        {
            throw BookingErrors.QuoteExpired(request.ItemId);
        }

        return new NewItem(
            request.ItemId, preview.Kind, spec.QuoteToken!, preview.Description, preview.Total, preview.Quantity, preview.DriverAge, preview.ExpiresAt);
    }
}
