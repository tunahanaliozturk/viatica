using Relevox;
using Viatica.Kernel;
using Viatica.Search.Application.Projection;

namespace Viatica.Search.Application.Stays;

public enum StaySort
{
    /// <summary>Cheapest total for the stay first.</summary>
    Price,

    /// <summary>Highest star rating first, then cheapest.</summary>
    Rating,

    /// <summary>Nearest first. Needs a point: the bounding box centre or an explicit latitude and longitude.</summary>
    Distance,
}

/// <summary>West, south, east, north in degrees. West greater than east means the box crosses the antimeridian.</summary>
public sealed record BoundingBox(double West, double South, double East, double North)
{
    public GeoLocation Center
    {
        get
        {
            double east = East < West ? East + 360 : East;
            double lon = (West + east) / 2;
            return new GeoLocation((South + North) / 2, lon > 180 ? lon - 360 : lon);
        }
    }
}

/// <summary>
/// Stays for given nights. A property is returned only when one of its room types has at least <see cref="Rooms"/>
/// units left on every night of the stay and sleeps <see cref="Guests"/> people in that many rooms.
/// </summary>
/// <param name="Destination">Free text matched against property name, city and region. Optional when a box is given.</param>
/// <param name="Origin">Where "near" is measured from when sorting by distance; the box centre when null.</param>
/// <param name="MinPrice">Lowest average price per room and night, in EUR.</param>
/// <param name="After">The position inside a skiptoken; null on the first page.</param>
public sealed record SearchStays(
    string? Destination,
    BoundingBox? Box,
    GeoLocation? Origin,
    DateOnly CheckIn,
    DateOnly CheckOut,
    int Guests,
    int Rooms,
    decimal? MinPrice,
    decimal? MaxPrice,
    IReadOnlyList<int> Stars,
    IReadOnlyList<string> Amenities,
    StaySort Sort,
    int Top,
    IReadOnlyList<string>? After) : IRequest<StaySearchPage>
{
    public const int MaxNights = 30;
    public const int MaxRooms = NightLedger.MaxThreshold;

    public int Nights => CheckOut.DayNumber - CheckIn.DayNumber;
}

/// <summary>What the index needs to answer a stay search, with every figure already in the units it stores.</summary>
public sealed record StaySearchCriteria(
    string? Destination,
    BoundingBox? Box,
    GeoLocation? DistanceOrigin,
    int FirstNight,
    int Nights,
    int Rooms,
    int GuestsPerRoom,
    long? MinNightlyCents,
    long? MaxNightlyCents,
    IReadOnlyList<int> Stars,
    IReadOnlyList<string> Amenities,
    StaySort Sort,
    int Top,
    IReadOnlyList<string>? After)
{
    public static StaySearchCriteria From(SearchStays request)
    {
        ArgumentNullException.ThrowIfNull(request);

        return new StaySearchCriteria(
            string.IsNullOrWhiteSpace(request.Destination) ? null : request.Destination.Trim(),
            request.Box,
            request.Origin ?? request.Box?.Center,
            request.CheckIn.DayNumber,
            request.Nights,
            request.Rooms,
            // Guests are spread over the rooms as evenly as they can be: 5 guests in 2 rooms needs rooms for 3.
            (request.Guests + request.Rooms - 1) / request.Rooms,
            Cents(request.MinPrice),
            Cents(request.MaxPrice),
            [.. request.Stars.Distinct().Order()],
            [.. request.Amenities.Select(static a => a.Trim().ToLowerInvariant()).Where(static a => a.Length > 0).Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal)],
            request.Sort,
            request.Top,
            request.After);
    }

    private static long? Cents(decimal? euros) => euros is { } value ? (long)decimal.Round(value * 100m) : null;
}

public sealed record StayRoomOffer(Guid RoomTypeId, string Name, int MaxOccupancy, string Beds);

/// <summary>One property that can host the stay, with the cheapest room type that does.</summary>
/// <param name="PricePerNight">Average nightly rate of one room, rounded to the cent.</param>
/// <param name="Total">Every night of every room added up. Indicative: the binding price is the quote from Stays.</param>
public sealed record StaySearchItem(
    Guid PropertyId,
    string Name,
    int StarRating,
    string City,
    string? Region,
    string CountryCode,
    GeoLocation Location,
    IReadOnlyList<string> Amenities,
    double? DistanceKm,
    StayRoomOffer Room,
    int Nights,
    int Rooms,
    Money PricePerNight,
    Money Total);

public sealed record FacetCount<TValue>(TValue Value, long Count);

/// <summary>How many of the matching properties have each star rating and each amenity.</summary>
public sealed record StayFacets(IReadOnlyList<FacetCount<int>> Stars, IReadOnlyList<FacetCount<string>> Amenities);

/// <summary>A page of stays. <see cref="TotalCount"/> and <see cref="Facets"/> come with the first page only.</summary>
public sealed record StaySearchPage(
    IReadOnlyList<StaySearchItem> Items,
    IReadOnlyList<string>? NextPosition,
    long? TotalCount,
    StayFacets? Facets);

/// <summary>The stays side of the search index, as the application sees it.</summary>
public interface IStaySearchIndex
{
    Task<StaySearchPage> SearchAsync(StaySearchCriteria criteria, CancellationToken cancellationToken);
}

public sealed class SearchStaysHandler(IStaySearchIndex index) : IRequestHandler<SearchStays, StaySearchPage>
{
    public Task<StaySearchPage> Handle(SearchStays request, CancellationToken cancellationToken) =>
        index.SearchAsync(StaySearchCriteria.From(request), cancellationToken);
}

/// <summary>The price a result shows, worked out from what the index sums up.</summary>
public static class StayPricing
{
    /// <param name="roomNightsCents">One room's rates for every night, added up.</param>
    public static (Money PerNight, Money Total) Offer(long roomNightsCents, int nights, int rooms) =>
    (
        Money.Eur(decimal.Round((decimal)roomNightsCents / nights, MidpointRounding.AwayFromZero) / 100m),
        Money.Eur(roomNightsCents * rooms / 100m)
    );
}
