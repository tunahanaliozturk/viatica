using System.Text.Json.Serialization;
using Viatica.AirlineSim.Behaviour;
using Viatica.AirlineSim.Network;
using Viatica.AirlineSim.Orders;

namespace Viatica.AirlineSim.Api;

// The wire shapes of the simulated airlines. PascalCase and NDC flavoured names on purpose: they should read like
// somebody else's API, so the Flights adapter is written against a foreign contract, as it will be in production.

internal sealed record SearchRequest(
    string Origin,
    string Destination,
    DateOnly DepartureDate,
    DateOnly? ReturnDate,
    PassengerMix Passengers,
    Cabin Cabin);

internal sealed record SearchResponse(string Airline, IReadOnlyList<OfferView> Offers);

internal sealed record OfferView(
    string OfferId,
    DateTimeOffset ExpiresAt,
    string FareClass,
    Cabin Cabin,
    bool Refundable,
    PriceView Price,
    int SeatsLeft,
    IReadOnlyList<SegmentView> Segments);

internal sealed record SegmentView(
    string FlightNumber,
    string Origin,
    string Destination,
    DateTime DepartureLocal,
    DateTime ArrivalLocal,
    int DurationMinutes,
    string Aircraft);

internal sealed record PriceView(decimal Base, decimal Taxes, decimal Total, string Currency)
{
    public static PriceView From(Price price) => new(price.Base, price.Taxes, price.Total, "EUR");
}

[JsonConverter(typeof(JsonStringEnumConverter<RepriceStatus>))]
internal enum RepriceStatus
{
    Unchanged,
    Changed,
}

internal sealed record RepriceResponse(RepriceStatus Status, decimal PreviousTotal, OfferView Offer);

internal sealed record PassengerInput(string Type, string GivenName, string Surname, DateOnly BirthDate);

internal sealed record OrderCreateRequest(string OfferId, string IdempotencyKey, IReadOnlyList<PassengerInput> Passengers);

internal sealed record OrderView(
    string BookingReference,
    string IdempotencyKey,
    OrderStatus Status,
    string FareClass,
    PassengerMix Passengers,
    PriceView Price,
    DateTimeOffset CreatedAt,
    DateTimeOffset TicketingTimeLimit,
    DateTimeOffset? TicketedAt,
    IReadOnlyList<string> TicketNumbers,
    DateTimeOffset? VoidedAt,
    DateTimeOffset? CancelledAt,
    decimal? RefundAmount,
    IReadOnlyList<SegmentView> Segments);

internal sealed record ErrorItem(string Code, string DescText);

internal sealed record ErrorResponse(IReadOnlyList<ErrorItem> Errors);

internal sealed record AirlineView(string Code, string Name, string Hub, string Behaviour, Knobs Knobs, Knobs Defaults);

[JsonSerializable(typeof(SearchRequest))]
[JsonSerializable(typeof(SearchResponse))]
[JsonSerializable(typeof(RepriceResponse))]
[JsonSerializable(typeof(OrderCreateRequest))]
[JsonSerializable(typeof(OrderView))]
[JsonSerializable(typeof(ErrorResponse))]
[JsonSerializable(typeof(KnobsPatch))]
[JsonSerializable(typeof(AirlineView))]
[JsonSerializable(typeof(IReadOnlyList<AirlineView>))]
internal sealed partial class SimJson : JsonSerializerContext;
