using System.Globalization;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Viatica.Kernel.Pii;

public enum PassengerType
{
    Adult,
    Child,
    Infant,
}

/// <summary>
/// One traveller as the passport has them. Personal data: it exists in plain text only inside a handler, and its
/// <see cref="ToString"/> says nothing, so an interpolated log line or a failed assertion cannot print it.
/// </summary>
public sealed record PassengerDetails(
    PassengerType Type,
    string GivenName,
    string Surname,
    DateOnly BirthDate,
    string? DocumentNumber = null)
{
    public override string ToString() => nameof(PassengerDetails) + " { redacted }";
}

/// <summary>
/// The passenger list of a flight item as it travels from Booking to Flights (docs/design.md, "Personal data on the
/// broker"). Booking seals, Flights opens; both go through here so the format has one definition.
/// </summary>
public static class PassengerSealing
{
    /// <summary>The associated data of a flight item's passengers: <c>{tripId:N}:{itemId:N}</c>.</summary>
    public static string AssociatedData(Guid tripId, Guid itemId) =>
        string.Create(CultureInfo.InvariantCulture, $"{tripId:N}:{itemId:N}");

    public static string SealPassengers(this PiiSealer sealer, IReadOnlyList<PassengerDetails> passengers, string associatedData)
    {
        ArgumentNullException.ThrowIfNull(sealer);
        ArgumentNullException.ThrowIfNull(passengers);

        return sealer.Seal(JsonSerializer.SerializeToUtf8Bytes([.. passengers], PiiJsonContext.Default.PassengerDetailsArray), associatedData);
    }

    /// <summary>
    /// Opens a sealed passenger list. False for anything that does not authenticate, and also for a value that
    /// authenticates but is not a passenger list: that is a producer bug, and the caller treats it the same way.
    /// </summary>
    public static bool TryOpenPassengers(
        this PiiSealer sealer, string sealedValue, string associatedData, out IReadOnlyList<PassengerDetails> passengers)
    {
        ArgumentNullException.ThrowIfNull(sealer);
        passengers = [];

        if (!sealer.TryOpen(sealedValue, associatedData, out byte[] plaintext))
        {
            return false;
        }

        try
        {
            if (JsonSerializer.Deserialize(plaintext, PiiJsonContext.Default.PassengerDetailsArray) is { Length: > 0 } list)
            {
                passengers = list;
                return true;
            }
        }
        catch (JsonException)
        {
            // Falls through to false. The exception text can quote the payload, so it is not kept.
        }

        return false;
    }

    /// <summary>The trip-and-item overload, for the broker hop.</summary>
    public static bool TryOpenPassengers(
        this PiiSealer sealer, string sealedValue, Guid tripId, Guid itemId, out IReadOnlyList<PassengerDetails> passengers) =>
        sealer.TryOpenPassengers(sealedValue, AssociatedData(tripId, itemId), out passengers);

    public static string SealPassengers(
        this PiiSealer sealer, IReadOnlyList<PassengerDetails> passengers, Guid tripId, Guid itemId) =>
        sealer.SealPassengers(passengers, AssociatedData(tripId, itemId));
}

[JsonSourceGenerationOptions(
    JsonSerializerDefaults.Web,
    UseStringEnumConverter = true,
    DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull)]
[JsonSerializable(typeof(PassengerDetails[]))]
internal sealed partial class PiiJsonContext : JsonSerializerContext;
