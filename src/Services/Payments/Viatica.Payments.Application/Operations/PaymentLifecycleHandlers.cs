using System.Diagnostics;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Viatica.Contracts;
using Viatica.Kernel;
using Viatica.Payments.Application.Provider;
using Viatica.Payments.Domain;

namespace Viatica.Payments.Application.Operations;

// Capture, void and refund (PAY-2). Each ends in exactly one outcome event per operation id. Each is idempotent twice
// over: the consumer's inbox drops the same event id, and the operation record answers a request under a new event id,
// from a redelivery or a Booking that asked twice, by doing nothing. Every money rule is asked of the Payment before the
// provider hears about the request, so a request a rule refuses never costs a provider call.
//
// A request the provider cannot settle throws (see ProviderSettlement) instead of failing fast. Unlike an authorisation,
// these cannot be declined: Booking has already ticketed, committed or cancelled something on the strength of the
// money, and "not captured" would be a statement about the provider that nobody checked. They are tried again.

internal static class PaymentLookup
{
    public static Task<Payment?> FindAsync(this IPaymentsDb db, Guid paymentId, Guid tripId, CancellationToken cancellationToken) =>
        db.Payments.FirstOrDefaultAsync(p => p.Id == paymentId && p.TripId == tripId, cancellationToken);
}

/// <summary>Takes the authorised money, all of it or less. Less releases the rest, and the payment cannot be captured again.</summary>
public sealed class PaymentCaptureRequestedHandler(
    IPaymentsDb db,
    IPaymentProvider provider,
    OperationDesk desk,
    TimeProvider time,
    ILogger<PaymentCaptureRequestedHandler> logger) : IIntegrationEventHandler<PaymentCaptureRequested>
{
    public async Task HandleAsync(PaymentCaptureRequested message, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(message);
        using Activity? activity = PaymentsTelemetry.Source.StartActivity("payments.capture");

        if (await desk.HasSeenAsync(message.TripId, OperationKind.Capture, message.PaymentId, cancellationToken))
        {
            PaymentLog.AlreadyDone(logger, nameof(OperationKind.Capture), message.TripId, message.PaymentId);
            return;
        }

        Payment? payment = await db.FindAsync(message.PaymentId, message.TripId, cancellationToken);
        if (payment is null)
        {
            PaymentLog.UnknownPayment(logger, nameof(OperationKind.Capture), message.TripId, message.PaymentId, message.PaymentId);
            Refuse(message, null, PaymentRefusals.PaymentNotFound);
            return;
        }

        if (PaymentEvents.ToMoney(message.Amount) is not { } amount)
        {
            Refuse(message, null, PaymentRefusals.InvalidAmount);
            return;
        }

        if (payment.WhyNotCapture(amount) is { } refusal)
        {
            Refuse(message, amount, refusal);
            return;
        }

        string key = OperationRecord.KeyFor(message.TripId, OperationKind.Capture, message.PaymentId);
        Resolution resolution = await desk.ResolveAsync(
            OperationKind.Capture,
            message.TripId,
            key,
            token => provider.CaptureAsync(payment.ProviderIntentId!, amount, key, token),
            cancellationToken);

        switch (resolution)
        {
            case Resolved { Answer: var answer } resolved:
                switch (answer)
                {
                    case ProviderSucceeded succeeded:
                        payment.Capture(amount, time.GetUtcNow());
                        desk.Conclude(
                            message.TripId, payment.Id, OperationKind.Capture, message.PaymentId, amount,
                            OperationResult.Succeeded, null, succeeded.Reference, resolved.ByLookup, PaymentEvents.Captured(payment, amount));
                        break;

                    case ProviderRefused refused:
                        Refuse(message, amount, refused.Code, resolved.ByLookup);
                        break;

                    default:
                        throw new UnreachableException("A provider answer was never set.");
                }

                break;

            case NothingSent:
                throw new ProviderUnavailableException(nameof(OperationKind.Capture));

            default:
                throw new UnreachableException("A resolution was never set.");
        }
    }

    private void Refuse(PaymentCaptureRequested message, Money? amount, string reason, bool byLookup = false) =>
        desk.Conclude(
            message.TripId, message.PaymentId, OperationKind.Capture, message.PaymentId, amount,
            OperationResult.Refused, reason, null, byLookup,
            PaymentEvents.Refused(message.PaymentId, message.TripId, Contracts.PaymentOperation.Capture, message.PaymentId, reason));
}

/// <summary>Releases an authorisation that will not be captured: the trip failed or was unwound. A captured payment is refunded instead.</summary>
public sealed class PaymentVoidRequestedHandler(
    IPaymentsDb db,
    IPaymentProvider provider,
    OperationDesk desk,
    TimeProvider time,
    ILogger<PaymentVoidRequestedHandler> logger) : IIntegrationEventHandler<PaymentVoidRequested>
{
    public async Task HandleAsync(PaymentVoidRequested message, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(message);
        using Activity? activity = PaymentsTelemetry.Source.StartActivity("payments.void");

        if (await desk.HasSeenAsync(message.TripId, OperationKind.Void, message.PaymentId, cancellationToken))
        {
            PaymentLog.AlreadyDone(logger, nameof(OperationKind.Void), message.TripId, message.PaymentId);
            return;
        }

        Payment? payment = await db.FindAsync(message.PaymentId, message.TripId, cancellationToken);
        if (payment is null)
        {
            PaymentLog.UnknownPayment(logger, nameof(OperationKind.Void), message.TripId, message.PaymentId, message.PaymentId);
            Refuse(message, PaymentRefusals.PaymentNotFound);
            return;
        }

        if (payment.WhyNotVoid() is { } refusal)
        {
            Refuse(message, refusal);
            return;
        }

        string key = OperationRecord.KeyFor(message.TripId, OperationKind.Void, message.PaymentId);
        Resolution resolution = await desk.ResolveAsync(
            OperationKind.Void,
            message.TripId,
            key,
            token => provider.VoidAsync(payment.ProviderIntentId!, key, token),
            cancellationToken);

        switch (resolution)
        {
            case Resolved { Answer: var answer } resolved:
                switch (answer)
                {
                    case ProviderSucceeded succeeded:
                        payment.Void(time.GetUtcNow());
                        desk.Conclude(
                            message.TripId, payment.Id, OperationKind.Void, message.PaymentId, null,
                            OperationResult.Succeeded, null, succeeded.Reference, resolved.ByLookup, PaymentEvents.Voided(payment));
                        break;

                    case ProviderRefused refused:
                        Refuse(message, refused.Code, resolved.ByLookup);
                        break;

                    default:
                        throw new UnreachableException("A provider answer was never set.");
                }

                break;

            case NothingSent:
                throw new ProviderUnavailableException(nameof(OperationKind.Void));

            default:
                throw new UnreachableException("A resolution was never set.");
        }
    }

    private void Refuse(PaymentVoidRequested message, string reason, bool byLookup = false) =>
        desk.Conclude(
            message.TripId, message.PaymentId, OperationKind.Void, message.PaymentId, null,
            OperationResult.Refused, reason, null, byLookup,
            PaymentEvents.Refused(message.PaymentId, message.TripId, Contracts.PaymentOperation.Void, message.PaymentId, reason));
}

/// <summary>Gives money back, in parts if need be, up to what was captured. The operation id is the refund id.</summary>
public sealed class PaymentRefundRequestedHandler(
    IPaymentsDb db,
    IPaymentProvider provider,
    OperationDesk desk,
    TimeProvider time,
    ILogger<PaymentRefundRequestedHandler> logger) : IIntegrationEventHandler<PaymentRefundRequested>
{
    public async Task HandleAsync(PaymentRefundRequested message, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(message);
        using Activity? activity = PaymentsTelemetry.Source.StartActivity("payments.refund");

        if (await desk.HasSeenAsync(message.TripId, OperationKind.Refund, message.RefundId, cancellationToken))
        {
            PaymentLog.AlreadyDone(logger, nameof(OperationKind.Refund), message.TripId, message.RefundId);
            return;
        }

        Payment? payment = await db.FindAsync(message.PaymentId, message.TripId, cancellationToken);
        if (payment is null)
        {
            PaymentLog.UnknownPayment(logger, nameof(OperationKind.Refund), message.TripId, message.RefundId, message.PaymentId);
            Refuse(message, null, PaymentRefusals.PaymentNotFound);
            return;
        }

        if (PaymentEvents.ToMoney(message.Amount) is not { } amount)
        {
            Refuse(message, null, PaymentRefusals.InvalidAmount);
            return;
        }

        if (payment.WhyNotRefund(amount) is { } refusal)
        {
            Refuse(message, amount, refusal);
            return;
        }

        string key = OperationRecord.KeyFor(message.TripId, OperationKind.Refund, message.RefundId);
        Resolution resolution = await desk.ResolveAsync(
            OperationKind.Refund,
            message.TripId,
            key,
            token => provider.RefundAsync(payment.ProviderIntentId!, amount, message.TripId, message.RefundId, key, token),
            cancellationToken);

        switch (resolution)
        {
            case Resolved { Answer: var answer } resolved:
                switch (answer)
                {
                    case ProviderSucceeded succeeded:
                        payment.Refund(amount, time.GetUtcNow());
                        desk.Conclude(
                            message.TripId, payment.Id, OperationKind.Refund, message.RefundId, amount,
                            OperationResult.Succeeded, null, succeeded.Reference, resolved.ByLookup,
                            PaymentEvents.Refunded(payment, message.RefundId, amount));
                        break;

                    case ProviderRefused refused:
                        Refuse(message, amount, refused.Code, resolved.ByLookup);
                        break;

                    default:
                        throw new UnreachableException("A provider answer was never set.");
                }

                break;

            case NothingSent:
                throw new ProviderUnavailableException(nameof(OperationKind.Refund));

            default:
                throw new UnreachableException("A resolution was never set.");
        }
    }

    private void Refuse(PaymentRefundRequested message, Money? amount, string reason, bool byLookup = false) =>
        desk.Conclude(
            message.TripId, message.PaymentId, OperationKind.Refund, message.RefundId, amount,
            OperationResult.Refused, reason, null, byLookup,
            PaymentEvents.Refused(message.PaymentId, message.TripId, Contracts.PaymentOperation.Refund, message.RefundId, reason));
}
