using System.Buffers.Text;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Serialization;
using Viatica.Kernel;

namespace Viatica.Cars.Application.Quotes;

/// <summary>
/// What a quote token says. The payload is readable on purpose (Booking shows the item and the expected price from
/// it without calling Cars) and is never trusted: Cars verifies the MAC and the expiry when it places the hold.
/// </summary>
public sealed record CarQuote(
    int V,
    string Kind,
    Guid LocationId,
    Guid CategoryId,
    Guid DropOffLocationId,
    DateTimeOffset PickUpAt,
    DateTimeOffset DropOffAt,
    int DriverAge,
    Money Total,
    DateTimeOffset ExpiresAt)
{
    public const string CarKind = "car";
}

public enum QuoteTokenStatus
{
    Valid,
    Invalid,
    Expired,
}

/// <summary>
/// Signs and checks quote tokens: <c>base64url(json payload) + "." + base64url(HMAC-SHA256)</c>, as
/// <c>docs/design.md</c> describes. The key belongs to Cars alone; no other service can mint or accept a car
/// quote, and Cars cannot accept a stay quote.
/// </summary>
public sealed class QuoteTokenCodec
{
    public const int MinimumKeyLength = 32;

    public static readonly TimeSpan Lifetime = TimeSpan.FromMinutes(15);

    // A real token is a few hundred characters. Refusing long ones first means nobody makes us base64-decode and
    // HMAC a megabyte of junk.
    private const int MaxTokenLength = 2048;

    private readonly byte[] _key;
    private readonly TimeProvider _clock;

    public QuoteTokenCodec(byte[] key, TimeProvider clock)
    {
        ArgumentNullException.ThrowIfNull(key);
        ArgumentNullException.ThrowIfNull(clock);
        if (key.Length < MinimumKeyLength)
        {
            throw new ArgumentException($"The quote signing key must be at least {MinimumKeyLength} bytes.", nameof(key));
        }

        _key = [.. key];
        _clock = clock;
    }

    public (string Token, CarQuote Quote) Issue(
        Guid locationId, Guid categoryId, Guid dropOffLocationId, DateTimeOffset pickUpAt, DateTimeOffset dropOffAt,
        int driverAge, Money total)
    {
        var quote = new CarQuote(
            1, CarQuote.CarKind, locationId, categoryId, dropOffLocationId, pickUpAt, dropOffAt, driverAge, total,
            _clock.GetUtcNow() + Lifetime);

        byte[] payload = JsonSerializer.SerializeToUtf8Bytes(quote, QuoteJson.Default.CarQuote);
        byte[] mac = HMACSHA256.HashData(_key, payload);
        return (Base64Url.EncodeToString(payload) + "." + Base64Url.EncodeToString(mac), quote);
    }

    public QuoteTokenStatus Read(string? token, out CarQuote? quote)
    {
        quote = null;
        if (string.IsNullOrEmpty(token) || token.Length > MaxTokenLength || token.Split('.') is not [var body, var signature])
        {
            return QuoteTokenStatus.Invalid;
        }

        byte[] payload;
        byte[] mac;
        try
        {
            payload = Base64Url.DecodeFromChars(body);
            mac = Base64Url.DecodeFromChars(signature);
        }
        catch (FormatException)
        {
            return QuoteTokenStatus.Invalid;
        }

        // Constant time, so the time this takes does not tell a forger how many leading bytes were right.
        if (!CryptographicOperations.FixedTimeEquals(HMACSHA256.HashData(_key, payload), mac))
        {
            return QuoteTokenStatus.Invalid;
        }

        try
        {
            quote = JsonSerializer.Deserialize(payload, QuoteJson.Default.CarQuote);
        }
        catch (JsonException)
        {
            return QuoteTokenStatus.Invalid;
        }

        if (quote is not { V: 1, Kind: CarQuote.CarKind })
        {
            quote = null;
            return QuoteTokenStatus.Invalid;
        }

        return _clock.GetUtcNow() >= quote.ExpiresAt ? QuoteTokenStatus.Expired : QuoteTokenStatus.Valid;
    }
}

[JsonSourceGenerationOptions(
    JsonSerializerDefaults.Web,
    DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    RespectNullableAnnotations = true,
    RespectRequiredConstructorParameters = true)]
[JsonSerializable(typeof(CarQuote))]
internal sealed partial class QuoteJson : JsonSerializerContext;
