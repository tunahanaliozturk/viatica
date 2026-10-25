using Viatica.Kernel;
using Viatica.Payments.Domain.Cards;

namespace Viatica.Payments.Application.Provider;

/// <summary>
/// The payment provider as this service needs it. Nothing outside the adapter that implements it knows the provider's
/// field names, status codes or error texts.
/// </summary>
/// <remarks>
/// Every mutating call takes the operation's idempotency key, and a repeat under the same key is the same operation,
/// not a second one. That is what makes the lookup safe to act on and a resend safe to make.
/// </remarks>
public interface IPaymentProvider
{
    /// <summary>The one place a card number is sent anywhere. It is read from <paramref name="card"/> for the request and kept nowhere.</summary>
    Task<TokeniseOutcome> TokeniseAsync(CardDetails card, string idempotencyKey, CancellationToken cancellationToken);

    /// <summary>Holds <paramref name="amount"/> on the card behind <paramref name="providerToken"/>.</summary>
    Task<ProviderResult> AuthoriseAsync(
        string providerToken, Money amount, Guid tripId, Guid paymentId, string idempotencyKey, CancellationToken cancellationToken);

    /// <summary>Takes <paramref name="amount"/> from the authorisation, and releases any rest of it.</summary>
    Task<ProviderResult> CaptureAsync(string intentId, Money amount, string idempotencyKey, CancellationToken cancellationToken);

    /// <summary>Releases the authorisation without taking anything.</summary>
    Task<ProviderResult> VoidAsync(string intentId, string idempotencyKey, CancellationToken cancellationToken);

    Task<ProviderResult> RefundAsync(
        string intentId, Money amount, Guid tripId, Guid refundId, string idempotencyKey, CancellationToken cancellationToken);

    /// <summary>What the provider recorded under an idempotency key. The way to learn what a call that got no answer did.</summary>
    Task<LookupResult> LookUpAsync(string idempotencyKey, CancellationToken cancellationToken);
}
