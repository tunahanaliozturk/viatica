using System.Buffers.Text;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using Viatica.Kernel;

namespace Viatica.Stays.Domain;

/// <summary>
/// What a quote token pins: the room type, the nights, the number of rooms and the total. The payload is readable on
/// purpose (Booking shows the item and the expected price from it without calling Stays); the MAC is what stops
/// anyone from writing one.
/// </summary>
public sealed record QuotePayload(
    int V,
    string Kind,
    Guid PropertyId,
    Guid RoomTypeId,
    DateOnly CheckIn,
    DateOnly CheckOut,
    int Quantity,
    Money Total,
    DateTimeOffset ExpiresAt)
{
    public const int CurrentVersion = 1;
    public const string StayKind = "stay";

    [JsonIgnore]
    public StayDates Dates => new(CheckIn, CheckOut);
}

public enum QuoteFailure
{
    /// <summary>Not a token this service issued: malformed, wrong MAC, or a payload it does not understand.</summary>
    Invalid,

    /// <summary>A genuine token past its expiry. Only reported after the MAC checked out.</summary>
    Expired,
}

/// <summary>
/// Issues and verifies quote tokens: <c>base64url(json) + "." + base64url(HMAC-SHA256)</c>, as
/// <c>docs/design.md</c> specifies. The key belongs to Stays alone; Cars has its own, so a token minted by one
/// service is worthless to the other.
/// </summary>
public sealed class QuoteTokens
{
    /// <summary>A quote is good for fifteen minutes, the same as the hold it leads to.</summary>
    public static readonly TimeSpan Lifetime = TimeSpan.FromMinutes(15);

    // Tokens are about 400 characters. Anything much longer is not ours, and is refused before any work is done.
    private const int MaxTokenLength = 2048;
    private const int MacLength = 32;

    private readonly byte[] _key;

    public QuoteTokens(ReadOnlySpan<byte> key)
    {
        if (key.Length < MacLength)
        {
            throw new ArgumentException($"The quote key must be at least {MacLength} bytes.", nameof(key));
        }

        _key = key.ToArray();
    }

    public string Issue(QuotePayload payload)
    {
        ArgumentNullException.ThrowIfNull(payload);

        string body = Base64Url.EncodeToString(JsonSerializer.SerializeToUtf8Bytes(payload, QuoteJsonContext.Default.QuotePayload));
        return $"{body}.{Base64Url.EncodeToString(Mac(body))}";
    }

    /// <summary>
    /// Checks the MAC first, in constant time, and only then looks inside: a forged token is never parsed, and an
    /// expired one can only be told apart from an invalid one by someone who held a genuine token.
    /// </summary>
    public bool TryRead(string? token, DateTimeOffset now, out QuotePayload? payload, out QuoteFailure failure)
    {
        payload = null;
        failure = QuoteFailure.Invalid;

        if (string.IsNullOrEmpty(token) || token.Length > MaxTokenLength)
        {
            return false;
        }

        int dot = token.IndexOf('.', StringComparison.Ordinal);
        if (dot <= 0 || dot != token.LastIndexOf('.'))
        {
            return false;
        }

        string body = token[..dot];
        Span<byte> received = stackalloc byte[MacLength];
        // TryDecode still throws on a character outside the alphabet, so the alphabet is checked first.
        if (!Base64Url.IsValid(token.AsSpan(dot + 1)) || !Base64Url.TryDecodeFromChars(token.AsSpan(dot + 1), received, out int written) || written != MacLength)
        {
            return false;
        }

        // Base64 lets the last character carry bits that mean nothing, so two spellings decode to one MAC. Insisting on
        // the canonical spelling keeps "any changed character is refused" true without a footnote.
        ReadOnlySpan<char> macText = token.AsSpan(dot + 1);
        if (!macText.SequenceEqual(Base64Url.EncodeToString(received)) || !CryptographicOperations.FixedTimeEquals(received, Mac(body)))
        {
            return false;
        }

        QuotePayload? read;
        try
        {
            read = JsonSerializer.Deserialize(Base64Url.DecodeFromChars(body), QuoteJsonContext.Default.QuotePayload);
        }
        catch (Exception ex) when (ex is JsonException or FormatException)
        {
            return false;
        }

        if (read is not { V: QuotePayload.CurrentVersion, Kind: QuotePayload.StayKind })
        {
            return false;
        }

        if (now >= read.ExpiresAt)
        {
            failure = QuoteFailure.Expired;
            return false;
        }

        payload = read;
        return true;
    }

    private byte[] Mac(string body) => HMACSHA256.HashData(_key, Encoding.ASCII.GetBytes(body));
}

[JsonSourceGenerationOptions(JsonSerializerDefaults.Web, UseStringEnumConverter = true)]
[JsonSerializable(typeof(QuotePayload))]
internal sealed partial class QuoteJsonContext : JsonSerializerContext;
