using System.Diagnostics;
using Microsoft.Extensions.Logging;
using Viatica.Payments.Application.Provider;
using Viatica.Payments.Domain;

namespace Viatica.Payments.Application.Operations;

/// <summary>The provider answered, now or when the key was looked up. <see cref="ByLookup"/> says which.</summary>
public sealed record Resolved(ProviderAnswer Answer, bool ByLookup);

/// <summary>The provider was certainly not reached by the first call: the circuit was open, or it said it was unavailable.</summary>
public sealed record NothingSent;

public union Resolution(Resolved, NothingSent);

/// <summary>
/// The provider could not settle an operation. Thrown so the consumer retries with backoff and, after its attempt
/// limit, parks the message: an operation that moves money is never answered with a guess.
/// </summary>
public sealed class ProviderUnavailableException(string operation) : Exception($"The payment provider could not settle {operation}.");

/// <summary>
/// How a provider call whose answer is in doubt is settled. This is PAY-3.
/// </summary>
/// <remarks>
/// <para>
/// A call that failed in a way that may have reached the provider is never repeated blindly. The provider is asked
/// what it recorded under the operation's idempotency key. If it recorded an answer, that answer is the outcome. If it
/// recorded nothing, the call is sent once more under the same key, which the provider treats as the same operation
/// even if the first copy is still on its way. If the lookup itself cannot be answered, or the second call fails
/// again, this throws and the request is tried again later, with the same key. It never guesses.
/// </para>
/// <para>
/// A crash between the provider's answer and our commit needs no extra code: the redelivered request finds no record
/// here, calls the provider under the same key, and gets the answer the provider already recorded.
/// </para>
/// </remarks>
public sealed class ProviderSettlement(IPaymentProvider provider, ILogger<ProviderSettlement> logger)
{
    /// <summary>Makes the provider call and settles it if its answer is in doubt (see the remarks on this class).</summary>
    /// <exception cref="ProviderUnavailableException">The doubt could not be settled. Nothing was decided.</exception>
    public async Task<Resolution> ResolveAsync(
        OperationKind kind, Guid tripId, string idempotencyKey, Func<CancellationToken, Task<ProviderResult>> call, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(call);
        string operation = kind.ToString();

        switch (await call(cancellationToken))
        {
            case ProviderSucceeded succeeded:
                return new Resolved(succeeded, ByLookup: false);

            case ProviderRefused refused:
                return new Resolved(refused, ByLookup: false);

            case ProviderFailure { MayHaveReachedProvider: false }:
                PaymentLog.NothingSent(logger, operation, tripId);
                return new NothingSent();

            case ProviderFailure:
                break;

            default:
                throw new UnreachableException("A provider result was never set.");
        }

        PaymentLog.LookingUp(logger, operation, tripId);
        switch (await provider.LookUpAsync(idempotencyKey, cancellationToken))
        {
            case OperationFound found:
                PaymentsTelemetry.Lookups.Add(1, new KeyValuePair<string, object?>("result", "found"));
                PaymentLog.FoundByLookup(logger, operation, tripId);
                return new Resolved(found.Answer, ByLookup: true);

            case NoSuchOperation:
                PaymentsTelemetry.Lookups.Add(1, new KeyValuePair<string, object?>("result", "none"));
                break;

            case ProviderFailure:
                PaymentsTelemetry.Lookups.Add(1, new KeyValuePair<string, object?>("result", "failed"));
                PaymentLog.Unresolved(logger, operation, tripId, "the lookup failed");
                throw new ProviderUnavailableException(operation);

            default:
                throw new UnreachableException("A lookup result was never set.");
        }

        PaymentLog.Resending(logger, operation, tripId);
        PaymentsTelemetry.Resends.Add(1, new KeyValuePair<string, object?>("operation", operation));
        switch (await call(cancellationToken))
        {
            case ProviderSucceeded succeeded:
                return new Resolved(succeeded, ByLookup: false);

            case ProviderRefused refused:
                return new Resolved(refused, ByLookup: false);

            case ProviderFailure:
                // The history is uncertain now, so even a certain "not reached" is not a reason to decide: the first
                // copy may still land. The same key on the next attempt settles it.
                PaymentLog.Unresolved(logger, operation, tripId, "the second call failed too");
                throw new ProviderUnavailableException(operation);

            default:
                throw new UnreachableException("A provider result was never set.");
        }
    }

}
