using System.Text.Json.Serialization;
using Viatica.Flights.Domain;
using Viatica.Kernel;

namespace Viatica.Flights.Application.Offers;

/// <summary>
/// One bookable fare in the platform's own words, whichever airline it came from: the itinerary, the fare class and
/// cabin, what it costs and when the price stops being good. This is what clients see.
/// </summary>
/// <param name="Id">Ours, never the airline's. An offer id is only meaningful while the offer is cached.</param>
/// <param name="Total">What the passenger mix pays in all, taxes included. Binding only after a re-price.</param>
/// <param name="ExpiresAt">When the airline stops honouring the price. The cache entry ends at the same moment.</param>
public sealed record FlightOffer(
    string Id,
    string Airline,
    string FareClass,
    Cabin Cabin,
    bool Refundable,
    Money Total,
    int SeatsLeft,
    DateTimeOffset ExpiresAt,
    IReadOnlyList<FlightSegment> Segments)
{
    /// <summary>
    /// When the first flight leaves, on the origin airport's clock. The airline gives local times without an offset
    /// and the platform has no time zone table, so this is a "floating" instant. It sorts correctly among offers
    /// from one origin, which is the only comparison a search makes.
    /// </summary>
    [JsonIgnore]
    public DateTimeOffset Departure => new(Segments[0].DepartureLocal, TimeSpan.Zero);

    [JsonIgnore]
    public int DurationMinutes => Segments.Sum(static s => s.DurationMinutes);
}

/// <summary>One flight. Times are local to the airport, as printed on the ticket.</summary>
public sealed record FlightSegment(
    string FlightNumber,
    string Origin,
    string Destination,
    DateTime DepartureLocal,
    DateTime ArrivalLocal,
    int DurationMinutes,
    string Aircraft);

/// <summary>An airline that did not take part in a search, and why.</summary>
public sealed record IncompleteSupplier(string Airline, IncompleteReason Reason);

public enum IncompleteReason
{
    /// <summary>It was still working when the deadline came, or its own timeout fired first.</summary>
    Timeout,

    /// <summary>It answered with a failure, or could not be reached.</summary>
    Error,

    /// <summary>It has been failing, so the circuit breaker did not even ask.</summary>
    CircuitOpen,
}

/// <summary>
/// What the cache keeps per offer: the offer clients saw, the airline's own id for it (never sent to a client), and
/// the passenger mix it was priced for, which an order must match.
/// </summary>
public sealed record StoredOffer(FlightOffer Offer, string SupplierOfferId, PassengerMix Passengers);

/// <summary>The whole result set of one search, in the order clients page through it by default.</summary>
public sealed record StoredSearch(
    Guid Id,
    DateTimeOffset ExpiresAt,
    IReadOnlyList<IncompleteSupplier> IncompleteSuppliers,
    IReadOnlyList<FlightOffer> Offers);
