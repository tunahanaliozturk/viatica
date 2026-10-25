using System.Diagnostics;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Viatica.Contracts;
using Viatica.Kernel;
using Viatica.Payments.Application.Provider;
using Viatica.Payments.Domain;

namespace Viatica.Payments.Application.Operations;

/// <summary>
/// Booking asks for the trip total to be held on the traveller's card (PAY-2). Ends in exactly one of
/// <see cref="PaymentAuthorized"/> or <see cref="PaymentDeclined"/>, however often the request is delivered and
/// whatever the provider does meanwhile.
/// </summary>
/// <remarks>
/// <para>
/// Nothing here sees a card: Booking sends the method token Payments issued, and only the provider's own token goes
/// to the provider. A token that is not one of ours is a decline, not an exception.
/// </para>
/// <para>
/// When the provider could not be reached at all (an open circuit, a 503) the answer is a decline with the reason
/// <c>ProviderUnavailable</c>, at once. Nothing was held, the saga is waiting with holds that expire, and failing fast
/// lets it release them and tell the traveller to try again. A call that may have reached the provider is settled by
/// <see cref="OperationDesk"/> instead, and never ends in a guess.
/// </para>
/// </remarks>
public sealed class PaymentAuthorizationRequestedHandler(
    IPaymentsDb db,
    IPaymentProvider provider,
    OperationDesk desk,
    TimeProvider time,
    ILogger<PaymentAuthorizationRequestedHandler> logger) : IIntegrationEventHandler<PaymentAuthorizationRequested>
{
    public const string ProviderUnavailable = nameof(ProviderUnavailable);

    public async Task HandleAsync(PaymentAuthorizationRequested message, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(message);
        using Activity? activity = PaymentsTelemetry.Source.StartActivity("payments.authorize");

        if (await desk.HasSeenAsync(message.TripId, OperationKind.Authorize, message.PaymentId, cancellationToken))
        {
            PaymentLog.AlreadyDone(logger, nameof(OperationKind.Authorize), message.TripId, message.PaymentId);
            return;
        }

        Money? amount = PaymentEvents.ToMoney(message.Amount);
        Money shown = amount ?? Money.Zero(IsCurrency(message.Amount.Currency) ? message.Amount.Currency : "XXX");

        // One payment per trip. A second payment id for a trip that has one is declined without a row of its own,
        // because the trip's row is the payment and there is no room for two.
        if (await db.Payments.AsNoTracking().AnyAsync(p => p.TripId == message.TripId && p.Id != message.PaymentId, cancellationToken))
        {
            Decline(message, shown, "PaymentAlreadyExistsForTrip", storePayment: false);
            return;
        }

        if (amount is null || amount.Amount <= 0)
        {
            Decline(message, shown, PaymentRefusals.InvalidAmount, storePayment: true);
            return;
        }

        PaymentMethod? method = await db.Methods.AsNoTracking()
            .FirstOrDefaultAsync(m => m.Token == message.PaymentMethodToken, cancellationToken);
        if (method is null)
        {
            Decline(message, amount, "PaymentMethodInvalid", storePayment: true);
            return;
        }

        string key = OperationRecord.KeyFor(message.TripId, OperationKind.Authorize, message.PaymentId);
        Resolution resolution = await desk.ResolveAsync(
            OperationKind.Authorize,
            message.TripId,
            key,
            token => provider.AuthoriseAsync(method.ProviderToken, amount, message.TripId, message.PaymentId, key, token),
            cancellationToken);

        switch (resolution)
        {
            case Resolved { Answer: var answer } resolved:
                switch (answer)
                {
                    case ProviderSucceeded succeeded:
                        Payment payment = Payment.Authorize(
                            message.PaymentId, message.TripId, amount, message.PaymentMethodToken, succeeded.Reference, time.GetUtcNow());
                        db.Payments.Add(payment);
                        desk.Conclude(
                            message.TripId, payment.Id, OperationKind.Authorize, message.PaymentId, amount,
                            OperationResult.Succeeded, null, succeeded.Reference, resolved.ByLookup, PaymentEvents.Authorized(payment));
                        break;

                    case ProviderRefused refused:
                        Decline(message, amount, refused.Code, storePayment: true, resolved.ByLookup);
                        break;

                    default:
                        throw new UnreachableException("A provider answer was never set.");
                }

                break;

            case NothingSent:
                Decline(message, amount, ProviderUnavailable, storePayment: true);
                break;

            default:
                throw new UnreachableException("A resolution was never set.");
        }
    }

    private void Decline(PaymentAuthorizationRequested message, Money amount, string reason, bool storePayment, bool byLookup = false)
    {
        if (storePayment)
        {
            db.Payments.Add(Payment.Decline(message.PaymentId, message.TripId, amount, message.PaymentMethodToken, reason, time.GetUtcNow()));
        }

        desk.Conclude(
            message.TripId, message.PaymentId, OperationKind.Authorize, message.PaymentId, amount,
            OperationResult.Declined, reason, null, byLookup, PaymentEvents.Declined(message.PaymentId, message.TripId, reason));
    }

    private static bool IsCurrency(string text) => text is { Length: 3 } && text.All(char.IsAsciiLetterUpper);
}
