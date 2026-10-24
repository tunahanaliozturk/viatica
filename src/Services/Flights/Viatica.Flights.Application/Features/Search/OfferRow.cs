using Viatica.Flights.Application.Offers;
using Viatica.Kernel.Querying;

namespace Viatica.Flights.Application.Features.Search;

/// <summary>
/// A cached offer flattened to the fields a client may filter and sort on. Paging runs over the cached result set
/// in memory with the same keyset machinery the database-backed endpoints use, so a client sees one paging contract
/// whatever sits behind it.
/// </summary>
public sealed class OfferRow
{
    public const int DefaultPageSize = 20;

    public required string Id { get; init; }

    public required string Airline { get; init; }

    public required string FareClass { get; init; }

    public required string Cabin { get; init; }

    public bool Refundable { get; init; }

    public decimal TotalAmount { get; init; }

    public DateTimeOffset Departure { get; init; }

    public int DurationMinutes { get; init; }

    public required FlightOffer Offer { get; init; }

    /// <summary>
    /// Price, then departure, then id: the order FLT-1 asks for, made total by the key so paging never skips or
    /// repeats an offer. Sortable fields are the ones keyset paging can compare: no nullable, no boolean.
    /// </summary>
    public static QueryFields<OfferRow> Fields { get; } = new QueryFields<OfferRow>()
        .Key("id", static row => row.Id)
        .Field("airline", static row => row.Airline)
        .Field("fareClass", static row => row.FareClass)
        .Field("cabin", static row => row.Cabin)
        .Field("refundable", static row => row.Refundable)
        .Field("totalAmount", static row => row.TotalAmount, sortable: true)
        .Field("departure", static row => row.Departure, sortable: true)
        .Field("duration", static row => row.DurationMinutes, sortable: true)
        .DefaultOrderBy("totalAmount, departure");

    public static CollectionQuery<OfferRow> FirstPage(int top = DefaultPageSize) => Fields.Parse(null, null, top);

    public static OfferRow From(FlightOffer offer) => new()
    {
        Id = offer.Id,
        Airline = offer.Airline,
        FareClass = offer.FareClass,
        Cabin = offer.Cabin.ToString(),
        Refundable = offer.Refundable,
        TotalAmount = offer.Total.Amount,
        Departure = offer.Departure,
        DurationMinutes = offer.DurationMinutes,
        Offer = offer,
    };
}

/// <summary>One page of a search plus what the whole search is: its id, when it ends, and who is missing.</summary>
public sealed record FlightSearchPage(
    Guid SearchId,
    DateTimeOffset ExpiresAt,
    IReadOnlyList<IncompleteSupplier> IncompleteSuppliers,
    KeysetPage<FlightOffer> Offers);

public static class OfferPaging
{
    public static FlightSearchPage Page(StoredSearch search, CollectionQuery<OfferRow> query)
    {
        IQueryable<OfferRow> rows = search.Offers.Select(OfferRow.From).AsQueryable();
        KeysetPage<FlightOffer> page = query.ToPage([.. query.ApplyTo(rows)], static row => row.Offer);
        return new FlightSearchPage(search.Id, search.ExpiresAt, search.IncompleteSuppliers, page);
    }
}
