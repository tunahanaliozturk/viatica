using Viatica.Kernel;

namespace Viatica.Payments.Domain;

public enum PaymentStatus
{
    /// <summary>The provider holds the amount on the card. It can be captured or voided.</summary>
    Authorized,

    /// <summary>The provider said no, or could not be asked. Nothing is held, and nothing can follow.</summary>
    Declined,

    /// <summary>Money was taken. It can be refunded, in parts, up to what was captured.</summary>
    Captured,

    /// <summary>The hold was released. Nothing can follow.</summary>
    Voided,
}

/// <summary>Why a money rule said no. They are part of <c>PaymentOperationRefused.Reason</c>, so they are contract.</summary>
public static class PaymentRefusals
{
    public const string PaymentNotFound = nameof(PaymentNotFound);
    public const string CurrencyMismatch = nameof(CurrencyMismatch);
    public const string InvalidAmount = nameof(InvalidAmount);
    public const string NotAuthorized = nameof(NotAuthorized);
    public const string AlreadyCaptured = nameof(AlreadyCaptured);
    public const string PaymentVoided = nameof(PaymentVoided);
    public const string ExceedsAuthorization = nameof(ExceedsAuthorization);
    public const string NotCaptured = nameof(NotCaptured);
    public const string ExceedsCapture = nameof(ExceedsCapture);
}

/// <summary>A money rule was broken by a caller that skipped the check. A bug, not a request to refuse.</summary>
public sealed class PaymentRuleException(string refusal) : InvalidOperationException($"The payment rule {refusal} would be broken.")
{
    public string Refusal { get; } = refusal;
}

/// <summary>
/// The payment of one trip, and the money rules around it: a capture never exceeds the authorisation, a refund total
/// never exceeds the capture, and nothing is captured after a void. The rules live here and nowhere else, so a
/// redelivered request, a retry after a timeout and a request that arrives late all meet the same ones.
/// </summary>
/// <remarks>
/// Each rule has a question (<c>WhyNot...</c>) and an action. The handler asks first, because a refused request must
/// not reach the provider, and acts only after the provider agreed. The action asks again and throws: it is the last
/// line, for the day a caller forgets to ask.
/// </remarks>
public sealed class Payment
{
    private Payment()
    {
    }

    /// <summary>Booking's id for the payment. One trip has one.</summary>
    public Guid Id { get; private set; }

    public Guid TripId { get; private set; }

    public string Currency { get; private set; } = "";

    public decimal AuthorizedAmount { get; private set; }

    public decimal CapturedAmount { get; private set; }

    public decimal RefundedAmount { get; private set; }

    public Money Authorized => new(AuthorizedAmount, Currency);

    public Money Captured => new(CapturedAmount, Currency);

    public Money Refunded => new(RefundedAmount, Currency);

    public PaymentStatus Status { get; private set; }

    /// <summary>The provider's id for the authorisation, which later operations name. Null if it was declined.</summary>
    public string? ProviderIntentId { get; private set; }

    /// <summary>The method token Booking sent. A reference, not a card.</summary>
    public string PaymentMethodToken { get; private set; } = "";

    /// <summary>The provider's decline code, or one of ours. Null unless <see cref="Status"/> is Declined.</summary>
    public string? DeclineReason { get; private set; }

    public DateTimeOffset CreatedAt { get; private set; }

    public DateTimeOffset UpdatedAt { get; private set; }

    /// <summary>Postgres <c>xmin</c>: the row version, and the ETag.</summary>
    public uint Version { get; private set; }

    public static Payment Authorize(Guid id, Guid tripId, Money amount, string paymentMethodToken, string providerIntentId, DateTimeOffset now)
    {
        ArgumentNullException.ThrowIfNull(amount);
        ArgumentException.ThrowIfNullOrWhiteSpace(providerIntentId);

        if (amount.Amount <= 0)
        {
            throw new PaymentRuleException(PaymentRefusals.InvalidAmount);
        }

        Payment payment = New(id, tripId, amount, paymentMethodToken, now);
        payment.Status = PaymentStatus.Authorized;
        payment.AuthorizedAmount = amount.Amount;
        payment.ProviderIntentId = providerIntentId;
        return payment;
    }

    public static Payment Decline(Guid id, Guid tripId, Money amount, string paymentMethodToken, string reason, DateTimeOffset now)
    {
        ArgumentNullException.ThrowIfNull(amount);
        ArgumentException.ThrowIfNullOrWhiteSpace(reason);

        Payment payment = New(id, tripId, amount, paymentMethodToken, now);
        payment.Status = PaymentStatus.Declined;
        payment.DeclineReason = reason;
        return payment;
    }

    /// <summary>Null when <paramref name="amount"/> may be captured now, or the rule that says it may not.</summary>
    public string? WhyNotCapture(Money amount)
    {
        ArgumentNullException.ThrowIfNull(amount);

        if (!string.Equals(amount.Currency, Currency, StringComparison.Ordinal))
        {
            return PaymentRefusals.CurrencyMismatch;
        }

        if (amount.Amount <= 0)
        {
            return PaymentRefusals.InvalidAmount;
        }

        return Status switch
        {
            PaymentStatus.Voided => PaymentRefusals.PaymentVoided,
            PaymentStatus.Declined => PaymentRefusals.NotAuthorized,
            PaymentStatus.Captured => PaymentRefusals.AlreadyCaptured,
            _ when amount.Amount > AuthorizedAmount => PaymentRefusals.ExceedsAuthorization,
            _ => null,
        };
    }

    /// <summary>
    /// Takes the money. Less than the authorisation is fine: the provider releases the rest, so the payment is
    /// captured once and cannot be captured again.
    /// </summary>
    public void Capture(Money amount, DateTimeOffset now)
    {
        if (WhyNotCapture(amount) is { } refusal)
        {
            throw new PaymentRuleException(refusal);
        }

        Status = PaymentStatus.Captured;
        CapturedAmount = amount.Amount;
        UpdatedAt = now;
    }

    /// <summary>Null when the hold may be released now, or the rule that says it may not.</summary>
    public string? WhyNotVoid() => Status switch
    {
        PaymentStatus.Captured => PaymentRefusals.AlreadyCaptured,
        PaymentStatus.Declined => PaymentRefusals.NotAuthorized,
        PaymentStatus.Voided => PaymentRefusals.PaymentVoided,
        _ => null,
    };

    public void Void(DateTimeOffset now)
    {
        if (WhyNotVoid() is { } refusal)
        {
            throw new PaymentRuleException(refusal);
        }

        Status = PaymentStatus.Voided;
        UpdatedAt = now;
    }

    /// <summary>Null when <paramref name="amount"/> may be refunded now, or the rule that says it may not.</summary>
    public string? WhyNotRefund(Money amount)
    {
        ArgumentNullException.ThrowIfNull(amount);

        if (!string.Equals(amount.Currency, Currency, StringComparison.Ordinal))
        {
            return PaymentRefusals.CurrencyMismatch;
        }

        if (amount.Amount <= 0)
        {
            return PaymentRefusals.InvalidAmount;
        }

        if (Status != PaymentStatus.Captured)
        {
            return PaymentRefusals.NotCaptured;
        }

        return RefundedAmount + amount.Amount > CapturedAmount ? PaymentRefusals.ExceedsCapture : null;
    }

    public void Refund(Money amount, DateTimeOffset now)
    {
        if (WhyNotRefund(amount) is { } refusal)
        {
            throw new PaymentRuleException(refusal);
        }

        RefundedAmount += amount.Amount;
        UpdatedAt = now;
    }

    private static Payment New(Guid id, Guid tripId, Money amount, string paymentMethodToken, DateTimeOffset now)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(paymentMethodToken);

        return new Payment
        {
            Id = id,
            TripId = tripId,
            Currency = amount.Currency,
            PaymentMethodToken = paymentMethodToken,
            CreatedAt = now,
            UpdatedAt = now,
        };
    }
}
