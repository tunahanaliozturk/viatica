using Viatica.Contracts;
using Viatica.Kernel;
using Viatica.Payments.Domain;

namespace Viatica.Payments.Application.Operations;

/// <summary>
/// The outcome events, built in one place so that every path to an outcome produces the same event. Booking waits for
/// exactly one of these per request.
/// </summary>
internal static class PaymentEvents
{
    public static Price ToPrice(Money money) => new(money.Amount, money.Currency);

    public static PaymentAuthorized Authorized(Payment payment) => new(payment.Id, payment.TripId, ToPrice(payment.Authorized));

    public static PaymentDeclined Declined(Guid paymentId, Guid tripId, string reason) => new(paymentId, tripId, reason);

    public static PaymentCaptured Captured(Payment payment, Money amount) => new(payment.Id, payment.TripId, ToPrice(amount));

    public static PaymentVoided Voided(Payment payment) => new(payment.Id, payment.TripId);

    public static PaymentRefunded Refunded(Payment payment, Guid refundId, Money amount) =>
        new(payment.Id, payment.TripId, refundId, ToPrice(amount));

    public static PaymentOperationRefused Refused(Guid paymentId, Guid tripId, Contracts.PaymentOperation operation, Guid operationId, string reason) =>
        new(paymentId, tripId, operation, operationId, reason);

    /// <summary>Null when the wire price is not money: a currency that is not three letters, or too many decimals for it.</summary>
    public static Money? ToMoney(Price price)
    {
        try
        {
            return new Money(price.Amount, price.Currency);
        }
        catch (ArgumentException)
        {
            return null;
        }
    }
}
