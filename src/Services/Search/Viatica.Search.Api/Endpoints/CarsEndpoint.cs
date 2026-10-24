using System.Collections.Frozen;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.HttpResults;
using Microsoft.AspNetCore.Routing;
using Relevox;
using Viatica.Contracts;
using Viatica.Kernel.Web;
using Viatica.Search.Application.Cars;
using Viatica.Search.Application.Projection;

namespace Viatica.Search.Api.Endpoints;

public sealed record CarSearchResponse(IReadOnlyList<CarSearchItem> Value, string? NextLink);

internal static class CarsEndpoint
{
    private static readonly FrozenSet<string> Parameters = FrozenSet.ToFrozenSet(
    [
        "pickUpLocationId", "lat", "lon", "radiusKm", "dropOffLocationId", "pickUpAt", "dropOffAt",
        "category", "transmission", "minSeats", "minPrice", "maxPrice",
        "api-version", Paging.Top, Paging.SkipToken,
    ], StringComparer.Ordinal);

    public static void MapCars(this IEndpointRouteBuilder app) =>
        app.MapGet("/cars/search", async Task<Ok<CarSearchResponse>> (HttpContext http, ISender sender, CancellationToken cancellationToken) =>
        {
            CarSearchPage page = await sender.Send(Bind(http), cancellationToken);
            return TypedResults.Ok(new CarSearchResponse(page.Items, page.NextPosition is null ? null : http.NextLink(page.NextPosition)));
        })
        .WithName("SearchCars")
        .WithSummary("Pick-up locations with a car of the category free on every rental day.");

    internal static SearchCars Bind(HttpContext http)
    {
        var q = new QueryReader(http.Request.Query, Parameters);
        PageRequest page = http.BindPageRequest();

        double? lat = q.Double("lat");
        double? lon = q.Double("lon");
        if (lat.HasValue != lon.HasValue)
        {
            q.Problem("lat", "InvalidPoint", "Give lat and lon together.");
        }

        Guid? pickUp = q.Id("pickUpLocationId");
        Guid? dropOff = q.Id("dropOffLocationId");
        double? radius = q.Double("radiusKm");
        DateTimeOffset pickUpAt = q.Required(q.Instant("pickUpAt"), "pickUpAt");
        DateTimeOffset dropOffAt = q.Required(q.Instant("dropOffAt"), "dropOffAt");
        string? category = q.Text("category");
        Transmission? transmission = q.Enum<Transmission>("transmission");
        int? minSeats = q.Int("minSeats");
        decimal? minPrice = q.Decimal("minPrice");
        decimal? maxPrice = q.Decimal("maxPrice");
        q.ThrowIfInvalid();

        return new SearchCars(
            pickUp,
            lat is { } la && lon is { } lo ? new GeoLocation(la, lo) : null,
            radius,
            dropOff,
            pickUpAt,
            dropOffAt,
            category,
            transmission,
            minSeats,
            minPrice,
            maxPrice,
            page.Top,
            page.After);
    }
}
