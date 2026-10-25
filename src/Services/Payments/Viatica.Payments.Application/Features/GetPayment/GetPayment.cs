using Microsoft.EntityFrameworkCore;
using Relevox;
using Viatica.Kernel;
using Viatica.Payments.Domain;

namespace Viatica.Payments.Application.Features.GetPayment;

/// <summary>One concluded request, as an operator sees it: what was asked, what came of it, and how it was settled.</summary>
public sealed record OperationView(
    OperationKind Kind,
    Guid OperationId,
    OperationResult Result,
    Money? Amount,
    string? Reason,
    bool ResolvedByLookup,
    DateTimeOffset At);

/// <summary>A payment as operators see it. A card is never here, not even its last digits.</summary>
public sealed record PaymentView(
    Guid Id,
    Guid TripId,
    PaymentStatus Status,
    Money Authorized,
    Money Captured,
    Money Refunded,
    string? DeclineReason,
    IReadOnlyList<OperationView> Operations,
    DateTimeOffset CreatedAt,
    DateTimeOffset UpdatedAt,
    uint Version);

public sealed record GetPaymentQuery(Guid PaymentId) : IRequest<PaymentView>;

public sealed class GetPaymentHandler(IPaymentsDb db) : IRequestHandler<GetPaymentQuery, PaymentView>
{
    public async Task<PaymentView> Handle(GetPaymentQuery request, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);

        Payment payment = await db.Payments.AsNoTracking().FirstOrDefaultAsync(p => p.Id == request.PaymentId, cancellationToken)
            ?? throw ApiErrorException.Domain(ErrorCategory.NotFound, PaymentErrorCodes.PaymentNotFound, "There is no such payment.");

        List<OperationRecord> records = await db.Operations.AsNoTracking()
            .Where(o => o.PaymentId == payment.Id)
            .OrderBy(o => o.CreatedAt)
            .ThenBy(o => o.Id)
            .ToListAsync(cancellationToken);

        return new PaymentView(
            payment.Id,
            payment.TripId,
            payment.Status,
            payment.Authorized,
            payment.Captured,
            payment.Refunded,
            payment.DeclineReason,
            [.. records.Select(static o => new OperationView(
                o.Kind,
                o.OperationId,
                o.Result,
                o.Amount is { } amount && o.Currency is { } currency ? new Money(amount, currency) : null,
                o.Reason,
                o.ResolvedByLookup,
                o.CreatedAt))],
            payment.CreatedAt,
            payment.UpdatedAt,
            payment.Version);
    }
}
