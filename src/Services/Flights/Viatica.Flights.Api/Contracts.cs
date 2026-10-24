using System.Text.Json;
using System.Text.Json.Serialization;
using Viatica.Flights.Application.Features.Orders;
using Viatica.Flights.Application.Features.Search;
using Viatica.Flights.Application.Offers;
using Viatica.Flights.Domain;
using Viatica.Kernel;
using Viatica.Kernel.Web;

namespace Viatica.Flights.Api;

/// <summary>
/// The search request. Flat on purpose: the validator names the fields <c>adults</c>, <c>children</c> and
/// <c>infants</c>, and the error's <c>target</c> should be the field the client sent.
/// </summary>
public sealed record SearchFlightsRequest(
    string Origin,
    string Destination,
    DateOnly DepartureDate,
    DateOnly? ReturnDate = null,
    int Adults = 1,
    int Children = 0,
    int Infants = 0,
    Cabin Cabin = Cabin.Economy)
{
    internal SearchFlightsCommand ToCommand() =>
        new(Origin, Destination, DepartureDate, ReturnDate, Adults, Children, Infants, Cabin);
}

/// <summary>
/// A page of a search: the offers, and what the whole search is. <see cref="IncompleteSuppliers"/> is present on
/// every page, empty when every airline answered.
/// </summary>
public sealed record FlightSearchResponse(
    Guid SearchId,
    DateTimeOffset ExpiresAt,
    IReadOnlyList<IncompleteSupplier> IncompleteSuppliers,
    IReadOnlyList<FlightOffer> Value,
    string? NextLink)
{
    internal static FlightSearchResponse From(FlightSearchPage page, string? nextLink) =>
        new(page.SearchId, page.ExpiresAt, page.IncompleteSuppliers, page.Offers.Value, nextLink);
}

/// <summary>
/// The answer to a re-price. <c>Status</c> is <c>Unchanged</c> or <c>PriceChanged</c>; either way <c>Offer</c> is the
/// offer to book, with a new id and expiry. When the price changed, <c>PreviousTotal</c> is what the traveller saw.
/// </summary>
public sealed record RepriceResponse(string Status, FlightOffer Offer, Money? PreviousTotal);

[JsonSourceGenerationOptions(
    JsonSerializerDefaults.Web,
    UseStringEnumConverter = true,
    DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    RespectNullableAnnotations = true,
    RespectRequiredConstructorParameters = true)]
[JsonSerializable(typeof(SearchFlightsRequest))]
[JsonSerializable(typeof(FlightSearchResponse))]
[JsonSerializable(typeof(RepriceResponse))]
[JsonSerializable(typeof(FlightOrderView))]
internal sealed partial class FlightsJsonContext : JsonSerializerContext;
