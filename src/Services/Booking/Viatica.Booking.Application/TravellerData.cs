using System.Globalization;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Serialization;
using Viatica.Kernel.Pii;

namespace Viatica.Booking.Application;

public sealed record ContactDetails(string Email, string? Phone = null)
{
    public override string ToString() => nameof(ContactDetails) + " { redacted }";
}

/// <summary>
/// Everything a checkout carries that is about a person: how to reach them, who is travelling, and the token that
/// pays. It exists in plain text only inside the checkout handler and for as long as a handler needs it, and its
/// <see cref="ToString"/> says nothing, so an interpolated log line or a failed assertion cannot print it.
/// </summary>
public sealed record TravellerDetails(ContactDetails Contact, IReadOnlyList<PassengerDetails> Passengers, string PaymentMethodToken)
{
    public override string ToString() => nameof(TravellerDetails) + " { redacted }";
}

[JsonSourceGenerationOptions(JsonSerializerDefaults.Web, UseStringEnumConverter = true, DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull)]
[JsonSerializable(typeof(TravellerDetails))]
internal sealed partial class TravellerJsonContext : JsonSerializerContext;

/// <summary>
/// Where Booking's personal data is sealed, and the only place that handles it (BKG-3, requirements section 12).
/// </summary>
/// <remarks>
/// <para>
/// <b>At rest</b> the trip keeps one blob, <c>SealedTravellerData</c>: AES-256-GCM under Booking's own storage key
/// with the trip id as associated data, so a blob copied onto another trip's row does not open. It lives only while
/// the saga runs and is erased when the trip is confirmed or failed.
/// </para>
/// <para>
/// <b>On the broker</b> a flight's passenger list goes to Flights under the key the two services share
/// (<c>Viatica:Pii:PassengerKey</c>) with the trip and item ids as associated data (docs/design.md).
/// </para>
/// <para>
/// Two keys, as in Flights, so that rotating or leaking one neither exposes nor strands the other.
/// </para>
/// </remarks>
public sealed class TravellerVault(byte[] passengerKey, byte[] storageKey)
{
    private readonly PiiSealer broker = new(passengerKey);
    private readonly PiiSealer storage = new(storageKey);
    private readonly byte[] fingerprintKey = HMACSHA256.HashData(storageKey, System.Text.Encoding.UTF8.GetBytes("checkout-fingerprint"));

    public string Seal(Guid tripId, TravellerDetails details)
    {
        ArgumentNullException.ThrowIfNull(details);
        return storage.Seal(JsonSerializer.SerializeToUtf8Bytes(details, TravellerJsonContext.Default.TravellerDetails), AssociatedData(tripId));
    }

    /// <summary>False for a blob that does not authenticate: another trip's, another key's, or damaged.</summary>
    public bool TryOpen(Guid tripId, string? sealedData, out TravellerDetails details)
    {
        details = null!;
        if (sealedData is null || !storage.TryOpen(sealedData, AssociatedData(tripId), out byte[] plaintext))
        {
            return false;
        }

        try
        {
            details = JsonSerializer.Deserialize(plaintext, TravellerJsonContext.Default.TravellerDetails)!;
            return details is not null;
        }
        catch (JsonException)
        {
            // The text of the exception can quote the payload, so it is not kept.
            return false;
        }
    }

    public string SealPassengersForFlights(Guid tripId, Guid itemId, TravellerDetails details)
    {
        ArgumentNullException.ThrowIfNull(details);
        return broker.SealPassengers(details.Passengers, tripId, itemId);
    }

    /// <summary>
    /// A keyed hash of the request, so a repeat is told from a different request without keeping the request. Keyed,
    /// because a plain hash of a name and a birth date can be guessed by anyone who holds the database.
    /// </summary>
    public string Fingerprint(Guid tripId, TravellerDetails details)
    {
        ArgumentNullException.ThrowIfNull(details);

        byte[] body = JsonSerializer.SerializeToUtf8Bytes(details, TravellerJsonContext.Default.TravellerDetails);
        byte[] input = [.. tripId.ToByteArray(), .. body];
        return Convert.ToHexString(HMACSHA256.HashData(fingerprintKey, input));
    }

    private static string AssociatedData(Guid tripId) =>
        string.Create(CultureInfo.InvariantCulture, $"trip-travellers:{tripId:N}");
}
