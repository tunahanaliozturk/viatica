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

/// <summary>
/// What Booking needs to know about a hold once it is placed, and cannot get from the hold alone: whose inventory it
/// is (so a partner can list their bookings), when the stay or rental starts, and the cancellation policy that was
/// in force when the hold was placed (so a policy changed later does not change what an earlier booking refunds).
/// </summary>
/// <param name="PartnerId">The partner who owns the property or the rental location.</param>
/// <param name="StartsAt">
/// Check-in (the date at the property's check-in time, read as UTC until properties have a time zone) or pick-up.
/// </param>
/// <param name="CancellationPolicy">Copied from the property or the location at the moment of the hold.</param>
public sealed record HoldTerms(Guid PartnerId, DateTimeOffset StartsAt, CancellationPolicy CancellationPolicy);

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
