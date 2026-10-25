using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Viatica.Contracts;
using Viatica.Kernel;
using Viatica.Payments.Application.Provider;
using Viatica.Payments.Domain;

namespace Viatica.Payments.Application.Operations;

/// <summary>
/// What the four handlers share: whether a request was carried out already, how a provider call whose answer is in
/// doubt is settled (<see cref="ProviderSettlement"/>), and how an outcome is recorded together with its event.
/// </summary>
public sealed class OperationDesk(IPaymentsDb db, ProviderSettlement settlement, TimeProvider time, ILogger<OperationDesk> logger)
{
    /// <summary>True when this operation was concluded before: any later copy of the request changes nothing.</summary>
    public Task<bool> HasSeenAsync(Guid tripId, OperationKind kind, Guid operationId, CancellationToken cancellationToken) =>
        db.Operations.AsNoTracking().AnyAsync(o => o.TripId == tripId && o.Kind == kind && o.OperationId == operationId, cancellationToken);

    /// <inheritdoc cref="ProviderSettlement.ResolveAsync"/>
    public Task<Resolution> ResolveAsync(
        OperationKind kind, Guid tripId, string idempotencyKey, Func<CancellationToken, Task<ProviderResult>> call, CancellationToken cancellationToken) =>
        settlement.ResolveAsync(kind, tripId, idempotencyKey, call, cancellationToken);

    /// <summary>
    /// Records that the operation is concluded and queues its outcome event, to be saved together with whatever state
    /// change the caller made. The record is what makes every later copy of the request a no-op.
    /// </summary>
    public void Conclude(
        Guid tripId,
        Guid paymentId,
        OperationKind kind,
        Guid operationId,
        Money? amount,
        OperationResult result,
        string? reason,
        string? providerReference,
        bool resolvedByLookup,
        IIntegrationEvent outcome)
    {
        ArgumentNullException.ThrowIfNull(outcome);

        db.Operations.Add(OperationRecord.Record(
            tripId, paymentId, kind, operationId, amount, result, reason, providerReference, resolvedByLookup, time.GetUtcNow()));
        db.Publish(outcome);

        string operationName = kind.ToString();
        string resultName = result.ToString();
        string because = reason is null ? "" : $" ({reason})";
        PaymentsTelemetry.Operations.Add(
            1,
            new KeyValuePair<string, object?>("operation", operationName),
            new KeyValuePair<string, object?>("result", resultName));
        PaymentLog.Concluded(logger, operationName, tripId, operationId, resultName, because);
    }
}
