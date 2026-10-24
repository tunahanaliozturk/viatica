using System.Collections.Frozen;
using System.Globalization;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.HttpResults;
using Microsoft.AspNetCore.Routing;
using Relevox;
using Viatica.Kernel.Web;
using Viatica.Search.Application.Projection;
using Viatica.Search.Application.Stays;

namespace Viatica.Search.Api.Endpoints;

/// <summary>A page of stays. Facet counts and the total come with the first page only.</summary>
public sealed record StaySearchResponse(
    IReadOnlyList<StaySearchItem> Value,
    string? NextLink,
    long? TotalCount,
    StayFacets? Facets);

internal static class StaysEndpoint
{
    private static readonly FrozenSet<string> Parameters = FrozenSet.ToFrozenSet(
    [
        "destination", "bbox", "lat", "lon", "checkIn", "checkOut", "guests", "rooms",
        "minPrice", "maxPrice", "stars", "amenities", "sort",
        "api-version", Paging.Top, Paging.SkipToken,
    ], StringComparer.Ordinal);

    public static void MapStays(this IEndpointRouteBuilder app) =>
        app.MapGet("/stays/search", async Task<Ok<StaySearchResponse>> (HttpContext http, ISender sender, CancellationToken cancellationToken) =>
        {
            StaySearchPage page = await sender.Send(Bind(http), cancellationToken);
            return TypedResults.Ok(new StaySearchResponse(
                page.Items,
                page.NextPosition is null ? null : http.NextLink(page.NextPosition),
                page.TotalCount,
                page.Facets));
        })
        .WithName("SearchStays")
        .WithSummary("Stays free on every night of the stay, cheapest room type per property.");

    internal static SearchStays Bind(HttpContext http)
    {
        var q = new QueryReader(http.Request.Query, Parameters);
        PageRequest page = http.BindPageRequest();

        string? box = q.Text("bbox");
        BoundingBox? boundingBox = box is null ? null : ParseBox(box, q);
        double? lat = q.Double("lat");
        double? lon = q.Double("lon");
        if (lat.HasValue != lon.HasValue)
        {
            q.Problem("lat", "InvalidPoint", "Give lat and lon together.");
        }

        decimal? minPrice = q.Decimal("minPrice");
        decimal? maxPrice = q.Decimal("maxPrice");
        StaySort sort = q.Enum<StaySort>("sort") ?? StaySort.Price;
        DateOnly checkIn = q.Required(q.Date("checkIn"), "checkIn");
        DateOnly checkOut = q.Required(q.Date("checkOut"), "checkOut");
        string? destination = q.Text("destination");
        int guests = q.Int("guests") ?? 2;
        int rooms = q.Int("rooms") ?? 1;
        IReadOnlyList<int> stars = q.Ints("stars");
        IReadOnlyList<string> amenities = q.List("amenities");
        q.ThrowIfInvalid();

        return new SearchStays(
            destination,
            boundingBox,
            lat is { } la && lon is { } lo ? new GeoLocation(la, lo) : null,
            checkIn,
            checkOut,
            guests,
            rooms,
            minPrice,
            maxPrice,
            stars,
            amenities,
            sort,
            page.Top,
            page.After);
    }

    // west,south,east,north: the order GeoJSON and most map libraries use.
    private static BoundingBox? ParseBox(string text, QueryReader q)
    {
        string[] parts = text.Split(',', StringSplitOptions.TrimEntries);
        if (parts.Length == 4 && parts.All(static p => double.TryParse(p, NumberStyles.Float, CultureInfo.InvariantCulture, out double d) && double.IsFinite(d)))
        {
            double[] v = [.. parts.Select(static p => double.Parse(p, CultureInfo.InvariantCulture))];
            return new BoundingBox(v[0], v[1], v[2], v[3]);
        }

        q.Problem("bbox", "InvalidBoundingBox", "bbox must be four numbers: west,south,east,north.");
        return null;
    }
}
