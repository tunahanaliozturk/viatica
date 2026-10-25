using Microsoft.EntityFrameworkCore;
using Moongazing.OrionGuard.Core;
using Moongazing.OrionGuard.DependencyInjection;
using Relevox;
using Viatica.Booking.Domain;
using Viatica.Kernel;
using Viatica.Kernel.Pii;

namespace Viatica.Booking.Application.Features.Checkout;

/// <summary>
/// Starts the checkout saga for a trip (BKG-2). The trip id is the idempotency key: the same body again changes
/// nothing, another body for the same trip is a <c>409</c>. The answer is an operation to poll, because the saga takes
/// as long as the owners and the card network take.
/// </summary>
public sealed record StartCheckout(Guid TripId, Caller Caller, TravellerDetails Details) : IRequest<Operation>;

public sealed class StartCheckoutValidator : AbstractValidator<StartCheckout>
{
    private static readonly DateOnly Oldest = new(1900, 1, 1);

    public StartCheckoutValidator()
    {
        // Messages never repeat a value the caller sent, and targets name the field and never the value.
        RuleFor(static c => c.Caller.TravellerId is not null, "Sign in to check out.", "Authorization").WithErrorCode("SignInRequired");
        RuleFor(
            static c => c.Details.Contact is { Email.Length: > 2 and <= 254 } contact && contact.Email.Contains('@', StringComparison.Ordinal),
            "contact.email must be an email address.",
            "contact.email").WithErrorCode("InvalidEmail");
        RuleFor(static c => c.Details.Contact.Phone is null || c.Details.Contact.Phone.Length <= 32, "contact.phone is at most 32 characters.", "contact.phone")
            .WithErrorCode("InvalidPhone");
        RuleFor(static c => c.Details.Passengers.Count is >= 1 and <= 9, "passengers needs between 1 and 9 people.", "passengers").WithErrorCode("InvalidPassengerCount");
        RuleFor(
            static c => c.Details.Passengers.All(static p => IsName(p.GivenName) && IsName(p.Surname)),
            "Every passenger needs a givenName and a surname, up to 100 characters each.",
            "passengers").WithErrorCode("InvalidPassengerName");
        RuleFor(
            static c => c.Details.Passengers.All(static p => p.BirthDate >= Oldest && p.BirthDate <= DateOnly.FromDateTime(DateTime.UtcNow)),
            "Every passenger needs a birthDate between 1900 and today.",
            "passengers").WithErrorCode("InvalidBirthDate");
        RuleFor(static c => c.Details.Passengers.All(static p => p.DocumentNumber is null || p.DocumentNumber.Length <= 40), "documentNumber is at most 40 characters.", "passengers")
            .WithErrorCode("InvalidDocumentNumber");
        RuleFor(static c => c.Details.Passengers.Any(static p => p.Type == PassengerType.Adult), "At least one passenger must be an adult.", "passengers")
            .WithErrorCode("NoAdult");
        RuleFor(static c => c.Details.PaymentMethodToken is { Length: > 0 and <= 200 }, "paymentMethodToken is required, at most 200 characters.", "paymentMethodToken")
            .WithErrorCode("PaymentMethodTokenRequired");
    }

    private static bool IsName(string? value) => !string.IsNullOrWhiteSpace(value) && value.Length <= 100;
}

public sealed class StartCheckoutHandler(
    IBookingDb db,
    TravellerVault vault,
    DirectivePublisher publisher,
    BookingOptions options,
    TimeProvider clock) : IRequestHandler<StartCheckout, Operation>
{
    public Task<Operation> Handle(StartCheckout request, CancellationToken cancellationToken) =>
        db.InTransactionAsync(
            async () =>
            {
                Guid traveller = request.Caller.TravellerId!.Value;
                await db.LockTripAsync(request.TripId, cancellationToken);
                Trip trip = await db.Trips.Include(static t => t.Items).FirstOrDefaultAsync(t => t.Id == request.TripId, cancellationToken)
                    ?? throw BookingErrors.TripNotFound();

                // A trip built before signing in is taken by the checkout itself: the caller holds its key, and the
                // alternative is a second request between the two that a double click would get wrong.
                if (trip.TravellerId is null && request.Caller.Owns(trip))
                {
                    trip.Claim(traveller, clock.GetUtcNow());
                    publisher.Publish(trip, trip.TakeDirectives());
                }

                if (trip.TravellerId != traveller)
                {
                    throw BookingErrors.TripNotFound();
                }

                string fingerprint = vault.Fingerprint(trip.Id, request.Details);
                CheckoutResult result = trip.Checkout(
                    fingerprint, vault.Seal(trip.Id, request.Details), Guid.CreateVersion7(), clock.GetUtcNow(), options.Timings);

                switch (result.Outcome)
                {
                    case CheckoutOutcome.Conflict:
                        throw BookingErrors.CheckoutConflict();

                    case CheckoutOutcome.Started:
                        publisher.Publish(trip, result.Directives, request.Details);
                        BookingTelemetry.Checkouts.Add(1, new KeyValuePair<string, object?>("outcome", "started"));
                        await db.SaveChangesAsync(cancellationToken);
                        break;

                    default:
                        // A repeat. The trip is as it was, so there is nothing to save and nothing to send.
                        BookingTelemetry.Checkouts.Add(1, new KeyValuePair<string, object?>("outcome", "repeated"));
                        break;
                }

                return Views.CheckoutOperation(trip);
            },
            cancellationToken);
}
