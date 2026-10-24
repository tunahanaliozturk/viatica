using System.Text.Json.Serialization;

namespace Viatica.Flights.Infrastructure.Suppliers;

// The airlines' wire contract, as the simulator speaks it: PascalCase, NDC flavoured names, its own error shape.
// Written here from docs/airline-sim.md and not shared with the simulator's project, the way an adapter is written
// against a supplier's published API. Enums stay strings so an airline that adds a value does not break parsing.

internal sealed record PassengerMixDto(int Adults, int Children, int Infants);

internal sealed record SearchRequestDto(
    string Origin,
    string Destination,
    DateOnly DepartureDate,
    DateOnly? ReturnDate,
    PassengerMixDto Passengers,
    string Cabin);

internal sealed record PriceDto(decimal Base, decimal Taxes, decimal Total, string Currency);

internal sealed record SegmentDto(
    string FlightNumber,
    string Origin,
    string Destination,
    DateTime DepartureLocal,
    DateTime ArrivalLocal,
    int DurationMinutes,
    string Aircraft);

internal sealed record OfferDto(
    string OfferId,
    DateTimeOffset ExpiresAt,
    string FareClass,
    string Cabin,
    bool Refundable,
    PriceDto Price,
    int SeatsLeft,
    List<SegmentDto> Segments);

internal sealed record SearchResponseDto(string Airline, List<OfferDto> Offers);

internal sealed record RepriceResponseDto(string Status, decimal PreviousTotal, OfferDto Offer);

internal sealed record PassengerDto(string Type, string GivenName, string Surname, DateOnly BirthDate);

internal sealed record OrderCreateDto(string OfferId, string IdempotencyKey, List<PassengerDto> Passengers);

internal sealed record OrderDto(
    string BookingReference,
    string IdempotencyKey,
    string Status,
    string FareClass,
    PassengerMixDto Passengers,
    PriceDto Price,
    DateTimeOffset CreatedAt,
    DateTimeOffset TicketingTimeLimit,
    DateTimeOffset? TicketedAt,
    List<string> TicketNumbers,
    DateTimeOffset? VoidedAt,
    DateTimeOffset? CancelledAt,
    decimal? RefundAmount,
    List<SegmentDto> Segments);

internal sealed record ErrorItemDto(string Code, string DescText);

internal sealed record ErrorResponseDto(List<ErrorItemDto> Errors);

[JsonSourceGenerationOptions(PropertyNamingPolicy = JsonKnownNamingPolicy.Unspecified, PropertyNameCaseInsensitive = true)]
[JsonSerializable(typeof(SearchRequestDto))]
[JsonSerializable(typeof(SearchResponseDto))]
[JsonSerializable(typeof(RepriceResponseDto))]
[JsonSerializable(typeof(OrderCreateDto))]
[JsonSerializable(typeof(OrderDto))]
[JsonSerializable(typeof(ErrorResponseDto))]
internal sealed partial class AirlineJson : JsonSerializerContext;
