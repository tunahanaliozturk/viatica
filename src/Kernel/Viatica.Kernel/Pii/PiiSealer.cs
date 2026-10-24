using System.Buffers.Text;
using System.Security.Cryptography;
using System.Text;

namespace Viatica.Kernel.Pii;

/// <summary>
/// Seals personal data for the broker and for storage: AES-256-GCM with a random 12-byte nonce per message and
/// caller-chosen associated data, written as <c>base64url(nonce || ciphertext || tag)</c>.
/// </summary>
/// <remarks>
/// <para>
/// The associated data is not secret and is not stored in the output. It binds the ciphertext to the thing it
/// belongs to, so a sealed value copied onto another trip's event (or another row) fails authentication instead of
/// being read as that trip's data. Callers pass it on both ends; see <see cref="PassengerSealing.AssociatedData"/>.
/// </para>
/// <para>
/// GCM with a random nonce is safe for about 2^32 messages under one key. A trip's passenger list is sealed once
/// per flight item, so a key would need billions of bookings before that matters; rotate the key long before.
/// </para>
/// </remarks>
public sealed class PiiSealer
{
    public const int KeyLength = 32;
    private const int NonceLength = 12;
    private const int TagLength = 16;

    private readonly byte[] key;

    public PiiSealer(ReadOnlySpan<byte> key)
    {
        if (key.Length != KeyLength)
        {
            // The length only, never the key: a wrong-sized key in a log line is still a key.
            throw new ArgumentException($"The key must be {KeyLength} bytes (AES-256); got {key.Length}.", nameof(key));
        }

        this.key = key.ToArray();
    }

    /// <summary>Reads a key from configuration, where it is held as base64 (<c>Viatica:Pii:PassengerKey</c>).</summary>
    public static PiiSealer FromBase64(string base64Key)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(base64Key);

        byte[] bytes;
        try
        {
            bytes = Convert.FromBase64String(base64Key);
        }
        catch (FormatException)
        {
            throw new ArgumentException("The key is not valid base64.", nameof(base64Key));
        }

        try
        {
            return new PiiSealer(bytes);
        }
        finally
        {
            CryptographicOperations.ZeroMemory(bytes);
        }
    }

    public string Seal(ReadOnlySpan<byte> plaintext, string associatedData)
    {
        ArgumentNullException.ThrowIfNull(associatedData);

        byte[] buffer = new byte[NonceLength + plaintext.Length + TagLength];
        Span<byte> nonce = buffer.AsSpan(0, NonceLength);
        RandomNumberGenerator.Fill(nonce);

        using var aes = new AesGcm(key, TagLength);
        aes.Encrypt(
            nonce,
            plaintext,
            buffer.AsSpan(NonceLength, plaintext.Length),
            buffer.AsSpan(NonceLength + plaintext.Length, TagLength),
            Encoding.UTF8.GetBytes(associatedData));

        return Base64Url.EncodeToString(buffer);
    }

    /// <summary>
    /// Opens a sealed value, or says it cannot. Wrong key, wrong associated data, a changed bit and a value that is
    /// not ours at all all end the same way, so the caller cannot tell an attacker which one it was.
    /// </summary>
    public bool TryOpen(string sealedValue, string associatedData, out byte[] plaintext)
    {
        ArgumentNullException.ThrowIfNull(associatedData);
        plaintext = [];

        if (string.IsNullOrEmpty(sealedValue) || !Base64Url.IsValid(sealedValue, out int length)
            || length < NonceLength + TagLength)
        {
            return false;
        }

        byte[] buffer = Base64Url.DecodeFromChars(sealedValue);
        int cipherLength = buffer.Length - NonceLength - TagLength;
        byte[] opened = new byte[cipherLength];

        try
        {
            using var aes = new AesGcm(key, TagLength);
            aes.Decrypt(
                buffer.AsSpan(0, NonceLength),
                buffer.AsSpan(NonceLength, cipherLength),
                buffer.AsSpan(NonceLength + cipherLength, TagLength),
                opened,
                Encoding.UTF8.GetBytes(associatedData));
        }
        catch (CryptographicException)
        {
            CryptographicOperations.ZeroMemory(opened);
            return false;
        }

        plaintext = opened;
        return true;
    }

    /// <summary>Like <see cref="TryOpen"/>, for callers that treat a failure as a bug.</summary>
    /// <exception cref="CryptographicException">The value was not sealed with this key and these associated data.</exception>
    public byte[] Open(string sealedValue, string associatedData) =>
        TryOpen(sealedValue, associatedData, out byte[] plaintext)
            ? plaintext
            : throw new CryptographicException("The sealed value does not authenticate.");
}
