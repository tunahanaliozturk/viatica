using System.Text.Json.Serialization;

namespace Viatica.Contracts;

/// <summary>
/// An amount of money on the wire. The amount travels as a string ("312.00") so no consumer in any language
/// parses it into a float. Services map it to the kernel's Money type at the edge.
/// </summary>
public sealed record Price(
    [property: JsonNumberHandling(JsonNumberHandling.WriteAsString | JsonNumberHandling.AllowReadingFromString)]
    decimal Amount,
    string Currency);

public sealed record GeoPoint(double Latitude, double Longitude);

public sealed record Address(string Line1, string City, string? Region, string PostalCode, string CountryCode);

/// <summary>Units left and the price for one night (stays) or one rental day (cars).</summary>
public sealed record DateAvailability(DateOnly Date, int Available, Price Rate);

/// <summary>
/// Free cancellation until a number of hours before the stay or rental starts; after that the penalty applies.
/// Flights do not use this: their refund rules come from the fare.
/// </summary>
public sealed record CancellationPolicy(int FreeUntilHoursBeforeStart, CancellationPenalty PenaltyAfter);

public enum CancellationPenalty
{
    /// <summary>The first night of a stay or the first day of a rental is kept.</summary>
    FirstUnit,

    /// <summary>Nothing is refunded.</summary>
    FullAmount,
}

/// <summary>Why a stay or car hold was refused.</summary>
public enum HoldRejectionReason
{
    SoldOut,
    NotBookable,
    QuoteExpired,
    QuoteInvalid,
}

public enum TripItemKind
{
    Stay,
    Car,
    Flight,
}
