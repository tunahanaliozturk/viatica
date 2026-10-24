using System.Buffers;
using System.Buffers.Binary;
using System.Buffers.Text;
using System.Security.Cryptography;
using System.Text;
using Microsoft.Extensions.Options;
using Microsoft.Extensions.Primitives;

namespace Viatica.Kernel.Web;

/// <summary>
/// Turns a keyset position into an opaque <c>$skiptoken</c> and back. The token is
/// <c>base64url(version, position, HMAC-SHA256(key, SHA-256(normalised query) ‖ version ‖ position))</c>.
/// </summary>
/// <remarks>
/// <para>
/// The position is readable to anyone who decodes the base64, and that is fine: it holds values the caller has
/// already seen on the previous page. What the MAC stops is a caller writing their own position (to start
/// mid-collection) or taking a token from one query to another, because the query's hash is inside the MAC.
/// The hash is not sent; the server recomputes it from the request, so a replayed token simply fails to verify.
/// </para>
/// <para>
/// The normalised query is the path plus every parameter except <c>$skiptoken</c> and <c>$top</c>, keys lower
/// cased and sorted. <c>$top</c> stays out so a client can change page size mid-way; everything else, including
/// <c>api-version</c>, <c>$filter</c> and <c>$orderby</c>, is bound.
/// </para>
/// </remarks>
public sealed class SkipTokenCodec
{
    public const int MaxTokenLength = 2048;

    private const byte FormatVersion = 1;
    private const int HashLength = SHA256.HashSizeInBytes;
    private const int MacLength = HMACSHA256.HashSizeInBytes;

    private static readonly SearchValues<char> Base64UrlAlphabet =
        SearchValues.Create("ABCDEFGHIJKLMNOPQRSTUVWXYZabcdefghijklmnopqrstuvwxyz0123456789-_");

    private readonly byte[] key;

    public SkipTokenCodec(IOptions<ViaticaApiOptions> options)
        : this(options?.Value.SkipTokenKeyBytes() ?? throw new ArgumentNullException(nameof(options)))
    {
    }

    public SkipTokenCodec(byte[] key)
    {
        ArgumentNullException.ThrowIfNull(key);
        ArgumentOutOfRangeException.ThrowIfLessThan(key.Length, 32, nameof(key));
        this.key = key;
    }

    public string Encode(IReadOnlyList<string> position, string normalizedQuery)
    {
        ArgumentNullException.ThrowIfNull(position);
        ArgumentNullException.ThrowIfNull(normalizedQuery);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(position.Count, byte.MaxValue, nameof(position));

        int payloadLength = 2;
        foreach (string value in position)
        {
            payloadLength += 2 + Encoding.UTF8.GetByteCount(value);
        }

        // Layout in one rented buffer: [query hash][payload][mac]. The MAC covers hash and payload; the token is
        // payload and mac, which sit next to each other, so nothing is copied twice.
        byte[] buffer = ArrayPool<byte>.Shared.Rent(HashLength + payloadLength + MacLength);
        try
        {
            Span<byte> span = buffer.AsSpan(0, HashLength + payloadLength + MacLength);
            HashQuery(normalizedQuery, span[..HashLength]);

            Span<byte> payload = span.Slice(HashLength, payloadLength);
            payload[0] = FormatVersion;
            payload[1] = (byte)position.Count;
            int offset = 2;
            foreach (string value in position)
            {
                int written = Encoding.UTF8.GetBytes(value, payload[(offset + 2)..]);
                if (written > ushort.MaxValue)
                {
                    throw new ArgumentException("A keyset value is too long to page on.", nameof(position));
                }

                BinaryPrimitives.WriteUInt16BigEndian(payload[offset..], (ushort)written);
                offset += 2 + written;
            }

            HMACSHA256.HashData(key, span[..(HashLength + payloadLength)], span[(HashLength + payloadLength)..]);
            return Base64Url.EncodeToString(span[HashLength..]);
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(buffer);
        }
    }

    /// <summary>Verifies a token against the current query and returns its position, or false for any defect.</summary>
    public bool TryDecode(string token, string normalizedQuery, out string[] position)
    {
        ArgumentNullException.ThrowIfNull(token);
        ArgumentNullException.ThrowIfNull(normalizedQuery);
        position = [];

        // Base64Url throws rather than returning false on a character outside its alphabet; checking first keeps
        // a garbage token on the cheap path instead of raising an exception per bad request.
        if (token.Length is 0 or > MaxTokenLength || token.AsSpan().ContainsAnyExcept(Base64UrlAlphabet))
        {
            return false;
        }

        byte[] buffer = ArrayPool<byte>.Shared.Rent(HashLength + Base64Url.GetMaxDecodedLength(token.Length));
        try
        {
            if (!Base64Url.TryDecodeFromChars(token, buffer.AsSpan(HashLength), out int decoded)
                || decoded < 2 + MacLength)
            {
                return false;
            }

            Span<byte> span = buffer.AsSpan(0, HashLength + decoded);
            int signedLength = span.Length - MacLength;
            HashQuery(normalizedQuery, span[..HashLength]);

            Span<byte> expected = stackalloc byte[MacLength];
            HMACSHA256.HashData(key, span[..signedLength], expected);
            if (!CryptographicOperations.FixedTimeEquals(expected, span[signedLength..]))
            {
                return false;
            }

            // Past the MAC the payload is our own, but it is still parsed defensively: a key leak should not turn
            // into an index out of range.
            return TryReadPosition(span[HashLength..signedLength], out position);
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(buffer);
        }
    }

    /// <summary>The path and every query parameter except <c>$skiptoken</c> and <c>$top</c>, in a canonical order.</summary>
    public static string NormalizeQuery(string path, IEnumerable<KeyValuePair<string, StringValues>> query)
    {
        ArgumentNullException.ThrowIfNull(path);
        ArgumentNullException.ThrowIfNull(query);

        // Length prefixes keep the encoding unambiguous: "a=b&c" and "a=b%26c" can never normalise alike.
        var builder = new StringBuilder();
        Append(builder, path.ToLowerInvariant());
        foreach (var (name, values) in query
            .Where(static pair => !IsPagingParameter(pair.Key))
            .Select(static pair => (Name: pair.Key.ToLowerInvariant(), pair.Value))
            .OrderBy(static pair => pair.Name, StringComparer.Ordinal))
        {
            Append(builder, name);
            foreach (string? value in values)
            {
                Append(builder, value ?? string.Empty);
            }
        }

        return builder.ToString();
    }

    private static bool IsPagingParameter(string name) =>
        name.Equals(Paging.SkipToken, StringComparison.OrdinalIgnoreCase)
        || name.Equals(Paging.Top, StringComparison.OrdinalIgnoreCase);

    private static void Append(StringBuilder builder, string value) =>
        builder.Append(value.Length).Append(':').Append(value);

    private static void HashQuery(string normalizedQuery, Span<byte> destination)
    {
        int length = Encoding.UTF8.GetMaxByteCount(normalizedQuery.Length);
        byte[] rented = ArrayPool<byte>.Shared.Rent(length);
        try
        {
            int written = Encoding.UTF8.GetBytes(normalizedQuery, rented);
            SHA256.HashData(rented.AsSpan(0, written), destination);
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(rented);
        }
    }

    private static bool TryReadPosition(ReadOnlySpan<byte> payload, out string[] position)
    {
        position = [];
        if (payload.Length < 2 || payload[0] != FormatVersion)
        {
            return false;
        }

        var values = new string[payload[1]];
        int offset = 2;
        for (int i = 0; i < values.Length; i++)
        {
            if (offset + 2 > payload.Length)
            {
                return false;
            }

            int length = BinaryPrimitives.ReadUInt16BigEndian(payload[offset..]);
            offset += 2;
            if (offset + length > payload.Length)
            {
                return false;
            }

            values[i] = Encoding.UTF8.GetString(payload.Slice(offset, length));
            offset += length;
        }

        if (offset != payload.Length)
        {
            return false;
        }

        position = values;
        return true;
    }
}
