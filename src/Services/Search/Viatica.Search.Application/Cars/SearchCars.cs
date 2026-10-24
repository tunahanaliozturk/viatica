using Relevox;
using Viatica.Contracts;
using Viatica.Kernel;
using Viatica.Search.Application.Projection;

namespace Viatica.Search.Application.Cars;

/// <summary>
/// Cars for a rental period. A location is returned when a category passing the filters has a unit left on every
/// rental day. Give either a pick-up location or a point; with a point the radius defaults to
/// <see cref="DefaultRadiusKm"/>.
/// </summary>
/// <param name="DropOffLocationId">
/// Where the car goes back. Null means the pick-up location. A different location is a one way rental, allowed only
/// between locations of one partner.
/// </param>
/// <param name="Category">An ACRISS code or the start of one: "E" is every economy car, "ECMR" exactly that code.</param>
public sealed record SearchCars(
    Guid? PickUpLocationId,
    GeoLocation? Origin,
    double? RadiusKm,
    Guid? DropOffLocationId,
    DateTimeOffset PickUpAt,
    DateTimeOffset DropOffAt,
    string? Category,
    Transmission? Transmission,
    int? MinSeats,
    decimal? MinPrice,
    decimal? MaxPrice,
    int Top,
    IReadOnlyList<string>? After) : IRequest<CarSearchPage>
{
    public const double DefaultRadiusKm = 25;
    public const double MaxRadiusKm = 200;
    public const int MaxRentalDays = 30;

    /// <summary>
    /// Rental days are counted the way rental desks count them: every started 24 hours is a day. The calendar day
    /// of the pick-up time, as the caller wrote it, is the first one.
    /// </summary>
    public int Days => (int)Math.Ceiling((DropOffAt - PickUpAt).TotalHours / 24);

    public int FirstDay => DateOnly.FromDateTime(PickUpAt.DateTime).DayNumber;
}

public sealed record CarSearchCriteria(
    Guid? PickUpLocationId,
    GeoLocation? Origin,
    double RadiusKm,
    Guid? RequiredPartnerId,
    int FirstDay,
    int Days,
    string? Category,
    Transmission? Transmission,
    int? MinSeats,
    long? MinDailyCents,
    long? MaxDailyCents,
    int Top,
    IReadOnlyList<string>? After);

public sealed record CarOffer(
    Guid CategoryId,
    string Acriss,
    string Name,
    int Seats,
    Transmission Transmission,
    FuelType Fuel,
    int Bags,
    int MinimumDriverAge,
    Uri? ImageUrl,
    Money PricePerDay,
    Money Total);

/// <summary>A pick-up location with the cars available there, cheapest first.</summary>
/// <param name="OneWay">True when the car goes back to a different location than the one it leaves from.</param>
public sealed record CarSearchItem(
    Guid LocationId,
    string Name,
    string City,
    string? Region,
    string CountryCode,
    string? AirportCode,
    GeoLocation Location,
    double? DistanceKm,
    bool OneWay,
    int Days,
    IReadOnlyList<CarOffer> Offers);

public sealed record CarSearchPage(IReadOnlyList<CarSearchItem> Items, IReadOnlyList<string>? NextPosition);

public sealed record CarLocationRef(Guid LocationId, Guid PartnerId);

public interface ICarSearchIndex
{
    Task<CarSearchPage> SearchAsync(CarSearchCriteria criteria, CancellationToken cancellationToken);

    /// <summary>A published location, or null when there is none with that id.</summary>
    Task<CarLocationRef?> FindLocationAsync(Guid locationId, CancellationToken cancellationToken);
}

public sealed class SearchCarsHandler(ICarSearchIndex index) : IRequestHandler<SearchCars, CarSearchPage>
{
    public async Task<CarSearchPage> Handle(SearchCars request, CancellationToken cancellationToken)
    {
        CarLocationRef? pickUp = null;
        if (request.PickUpLocationId is { } pickUpId)
        {
            pickUp = await index.FindLocationAsync(pickUpId, cancellationToken)
                ?? throw ApiErrorException.Domain(ErrorCategory.NotFound, "LocationNotFound", "There is no such pick-up location.", "pickUpLocationId");
        }

        CarLocationRef? dropOff = null;
        if (request.DropOffLocationId is { } dropOffId)
        {
            dropOff = pickUp is not null && dropOffId == pickUp.LocationId
                ? pickUp
                : await index.FindLocationAsync(dropOffId, cancellationToken)
                    ?? throw ApiErrorException.Domain(ErrorCategory.NotFound, "LocationNotFound", "There is no such drop-off location.", "dropOffLocationId");
        }

        // One way rentals stay inside one partner's network: a rental company cannot leave its car at a rival.
        if (pickUp is not null && dropOff is not null && pickUp.PartnerId != dropOff.PartnerId)
        {
            throw ApiErrorException.Invalid(
                "OneWayNotAllowed", "One way rentals are only offered between locations of the same partner.", "dropOffLocationId");
        }

        var criteria = new CarSearchCriteria(
            request.PickUpLocationId,
            request.Origin,
            Math.Min(request.RadiusKm ?? SearchCars.DefaultRadiusKm, SearchCars.MaxRadiusKm),
            dropOff?.PartnerId,
            request.FirstDay,
            request.Days,
            string.IsNullOrWhiteSpace(request.Category) ? null : request.Category.Trim().ToUpperInvariant(),
            request.Transmission,
            request.MinSeats,
            Cents(request.MinPrice),
            Cents(request.MaxPrice),
            request.Top,
            request.After);

        CarSearchPage page = await index.SearchAsync(criteria, cancellationToken);
        if (request.DropOffLocationId is not { } returnTo)
        {
            return page;
        }

        return page with { Items = [.. page.Items.Select(item => item with { OneWay = item.LocationId != returnTo })] };
    }

    private static long? Cents(decimal? euros) => euros is { } value ? (long)decimal.Round(value * 100m) : null;
}

public static class CarPricing
{
    /// <param name="daysCents">One car's rates for every rental day, added up.</param>
    public static (Money PerDay, Money Total) Offer(long daysCents, int days) =>
    (
        Money.Eur(decimal.Round((decimal)daysCents / days, MidpointRounding.AwayFromZero) / 100m),
        Money.Eur(daysCents / 100m)
    );
}
