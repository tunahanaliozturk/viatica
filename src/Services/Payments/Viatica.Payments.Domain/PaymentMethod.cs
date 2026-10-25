using System.Buffers.Text;
using System.Security.Cryptography;

namespace Viatica.Payments.Domain;

/// <summary>
/// A card the traveller tokenised: the token Booking carries, the provider's own token behind it, and what a receipt
/// shows. There is no card number here and no way to get one back; the number went to the provider and stopped.
/// </summary>
public sealed class PaymentMethod
{
    private PaymentMethod()
    {
    }

    /// <summary>Ours (<c>pm_...</c>), unguessable. It is what the checkout carries to Booking and back.</summary>
    public string Token { get; private set; } = "";

    /// <summary>The provider's token for the same card. It never leaves this service.</summary>
    public string ProviderToken { get; private set; } = "";

    public string Brand { get; private set; } = "";

    public string Last4 { get; private set; } = "";

    public int ExpMonth { get; private set; }

    public int ExpYear { get; private set; }

    public DateTimeOffset CreatedAt { get; private set; }

    public static PaymentMethod Create(string providerToken, string brand, string last4, int expMonth, int expYear, DateTimeOffset now)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(providerToken);
        ArgumentException.ThrowIfNullOrWhiteSpace(brand);
        ArgumentException.ThrowIfNullOrWhiteSpace(last4);

        return new PaymentMethod
        {
            Token = NewToken(),
            ProviderToken = providerToken,
            Brand = brand,
            Last4 = last4,
            ExpMonth = expMonth,
            ExpYear = expYear,
            CreatedAt = now,
        };
    }

    // 192 random bits, so a token cannot be guessed by trying neighbours. A time ordered id would leak when it was made.
    private static string NewToken() => "pm_" + Base64Url.EncodeToString(RandomNumberGenerator.GetBytes(24));
}
