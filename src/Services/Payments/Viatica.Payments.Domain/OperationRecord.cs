using System.Globalization;
using Viatica.Kernel;

namespace Viatica.Payments.Domain;

public enum OperationKind
{
    Authorize,
    Capture,
    Void,
    Refund,
}

public enum OperationResult
{
    /// <summary>The provider did it.</summary>
    Succeeded,

    /// <summary>An authorisation the provider, or a rule of ours, declined.</summary>
    Declined,

    /// <summary>A capture, void or refund that a rule or the provider refused. Nothing moved.</summary>
    Refused,
}

/// <summary>
/// What was done about one request, recorded in the same transaction as the outcome event. It is the memory that makes
/// a request idempotent on trip id plus operation id: a repeat finds its row and does nothing, however it arrived.
/// </summary>
/// <remarks>
/// <see cref="OperationId"/> is the payment id for an authorisation, a capture and a void (a payment has one of each),
/// and the refund id for a refund. The kind is part of the key because the payment id alone names three operations.
/// </remarks>
public sealed class OperationRecord
{
    private OperationRecord()
    {
    }

    public Guid Id { get; private set; }

    public Guid TripId { get; private set; }

    public Guid PaymentId { get; private set; }

    public OperationKind Kind { get; private set; }

    public Guid OperationId { get; private set; }

    /// <summary>The key the provider knows this operation by. It is derived from the three fields above and nothing else.</summary>
    public string IdempotencyKey { get; private set; } = "";

    public decimal? Amount { get; private set; }

    public string? Currency { get; private set; }

    public OperationResult Result { get; private set; }

    /// <summary>A decline code or a refusal code. Null when the operation succeeded.</summary>
    public string? Reason { get; private set; }

    /// <summary>The provider's id for what it made: the payment intent for an authorisation, the refund for a refund.</summary>
    public string? ProviderReference { get; private set; }

    /// <summary>True when the provider's answer was lost and the outcome was read back by looking the key up.</summary>
    public bool ResolvedByLookup { get; private set; }

    public DateTimeOffset CreatedAt { get; private set; }

    /// <summary>
    /// The one key a request has, at the provider and here. The same trip, kind and id always give the same key, so a
    /// retry after a timeout, a redelivery and a crash between the provider call and the commit all reach the same
    /// operation and never a second one.
    /// </summary>
    public static string KeyFor(Guid tripId, OperationKind kind, Guid operationId) =>
        string.Create(CultureInfo.InvariantCulture, $"{tripId:N}:{kind.ToString().ToLowerInvariant()}:{operationId:N}");

    public static OperationRecord Record(
        Guid tripId,
        Guid paymentId,
        OperationKind kind,
        Guid operationId,
        Money? amount,
        OperationResult result,
        string? reason,
        string? providerReference,
        bool resolvedByLookup,
        DateTimeOffset now) => new()
        {
            Id = Guid.CreateVersion7(),
            TripId = tripId,
            PaymentId = paymentId,
            Kind = kind,
            OperationId = operationId,
            IdempotencyKey = KeyFor(tripId, kind, operationId),
            Amount = amount?.Amount,
            Currency = amount?.Currency,
            Result = result,
            Reason = reason,
            ProviderReference = providerReference,
            ResolvedByLookup = resolvedByLookup,
            CreatedAt = now,
        };
}
