namespace Viatica.Payments.Application;

/// <summary>The codes this service adds to the shared vocabulary. They are listed in docs/api/errors.md.</summary>
public static class PaymentErrorCodes
{
    public const string PaymentMethodInvalid = nameof(PaymentMethodInvalid);
    public const string PaymentNotFound = nameof(PaymentNotFound);
    public const string ProviderUnavailable = nameof(ProviderUnavailable);
}
