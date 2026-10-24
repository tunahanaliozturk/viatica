using System.Text.Json;
using System.Text.Json.Nodes;
using Viatica.Contracts;
using Viatica.Kernel;
using Viatica.Search.Application;
using Viatica.Search.Application.Cars;
using Viatica.Search.Application.Projection;
using Viatica.Search.Infrastructure.Indexing;
using HttpMethod = OpenSearch.Net.HttpMethod;

namespace Viatica.Search.Infrastructure.Querying;

/// <summary>
/// Car search: a location is a hit when one of its categories passing the filters has a unit left on every rental
/// day. Same document shape and same range-contains query as stays, with one unit per rental instead of N rooms.
/// </summary>
public sealed class CarSearchIndex(OpenSearchRest rest) : ICarSearchIndex
{
    private const string Path = "categories";
    private const string Rates = "categories.rates";

    // A location has a handful of categories; this is the most it lists, cheapest first.
    private const int MaxOffersPerLocation = 12;

    public async Task<CarSearchPage> SearchAsync(CarSearchCriteria criteria, CancellationToken cancellationToken)
    {
        using JsonDocument answer = await rest.SendAsync(
            HttpMethod.POST, $"{IndexDefinitions.Cars}/_search", Build(criteria).ToJsonString(), cancellationToken);
        return Read(answer.RootElement, criteria);
    }

    public async Task<CarLocationRef?> FindLocationAsync(Guid locationId, CancellationToken cancellationToken)
    {
        // A search by id rather than a GET, so only the two fields needed come back and not the nights of every
        // category. It sees what the last refresh made searchable, which is the same view the search itself has.
        var body = new JsonObject
        {
            ["size"] = 1,
            ["_source"] = SearchDsl.Source("partnerId"),
            ["query"] = SearchDsl.Bool([SearchDsl.Term("published", true), SearchDsl.Term("locationId", locationId.ToString())]),
        };

        using JsonDocument answer = await rest.SendAsync(
            HttpMethod.POST, $"{IndexDefinitions.Cars}/_search", body.ToJsonString(), cancellationToken);
        JsonElement[] hits = [.. answer.RootElement.GetProperty("hits").GetProperty("hits").EnumerateArray()];
        return hits.Length == 0
            ? null
            : new CarLocationRef(locationId, Guid.Parse(hits[0].GetProperty("_source").GetProperty("partnerId").GetString()!));
    }

    internal static JsonObject Build(CarSearchCriteria c)
    {
        JsonObject unit = Unit(c);

        var filters = new List<JsonNode>
        {
            SearchDsl.Term("published", true),
            new JsonObject
            {
                ["nested"] = new JsonObject
                {
                    ["path"] = Path,
                    ["query"] = unit.DeepClone(),
                    ["inner_hits"] = new JsonObject
                    {
                        ["size"] = MaxOffersPerLocation,
                        ["_source"] = SearchDsl.Source(
                            "categories.id", "categories.acriss", "categories.name", "categories.seats", "categories.transmission",
                            "categories.fuel", "categories.bags", "categories.minimumDriverAge", "categories.imageUrl"),
                        ["sort"] = new JsonArray(SearchDsl.CheapestFirstInner(Rates, c.FirstDay, c.Days)),
                        ["script_fields"] = new JsonObject { ["sum"] = new JsonObject { ["script"] = Scripts.Sum(Rates, c.FirstDay, c.Days) } },
                    },
                },
            },
        };

        if (c.PickUpLocationId is { } id)
        {
            filters.Add(SearchDsl.Term("locationId", id.ToString()));
        }

        if (c.Origin is { } origin && c.PickUpLocationId is null)
        {
            filters.Add(SearchDsl.GeoCircle(origin, c.RadiusKm));
        }

        if (c.RequiredPartnerId is { } partner)
        {
            // A one way rental stays inside one partner's network, so only that partner's locations can start it.
            filters.Add(SearchDsl.Term("partnerId", partner.ToString()));
        }

        var body = new JsonObject
        {
            ["size"] = c.Top + 1,
            ["track_total_hits"] = false,
            ["_source"] = SearchDsl.Source("locationId", "name", "city", "region", "countryCode", "airportCode", "location"),
            ["query"] = SearchDsl.Bool(filters),
            ["sort"] = new JsonArray(
                SearchDsl.CheapestFirst(Path, Rates, unit, c.FirstDay, c.Days),
                new JsonObject { ["locationId"] = "asc" }),
        };

        if (c.After is not null)
        {
            body["search_after"] = SearchDsl.SearchAfter(c.After, "ns");
        }

        return body;
    }

    private static JsonObject Unit(CarSearchCriteria c)
    {
        var filters = new List<JsonNode>
        {
            SearchDsl.Term("categories.listed", true),
            SearchDsl.OpenForAllDays(Path, 1, c.FirstDay, c.Days),
        };

        if (c.Category is not null)
        {
            filters.Add(new JsonObject { ["prefix"] = new JsonObject { ["categories.acriss"] = c.Category } });
        }

        if (c.Transmission is { } transmission)
        {
            filters.Add(SearchDsl.Term("categories.transmission", transmission.ToString()));
        }

        if (c.MinSeats is { } seats)
        {
            filters.Add(new JsonObject { ["range"] = new JsonObject { ["categories.seats"] = new JsonObject { ["gte"] = seats } } });
        }

        if (c.MinDailyCents is not null || c.MaxDailyCents is not null)
        {
            long min = (c.MinDailyCents ?? 0) * c.Days;
            long max = c.MaxDailyCents is { } cap ? cap * c.Days : long.MaxValue;
            filters.Add(new JsonObject { ["script"] = new JsonObject { ["script"] = Scripts.SumWithin(Rates, c.FirstDay, c.Days, min, max) } });
        }

        return SearchDsl.Bool(filters);
    }

    private static CarSearchPage Read(JsonElement root, CarSearchCriteria c)
    {
        JsonElement[] found = [.. root.GetProperty("hits").GetProperty("hits").EnumerateArray()];
        List<CarSearchItem> items = [];

        foreach (JsonElement hit in found.Take(c.Top))
        {
            JsonElement source = hit.GetProperty("_source");
            GeoLocation location = SearchDsl.LocationOf(source);
            List<CarOffer> offers = [];
            foreach (JsonElement inner in hit.GetProperty("inner_hits").GetProperty(Path).GetProperty("hits").GetProperty("hits").EnumerateArray())
            {
                JsonElement category = inner.GetProperty("_source");
                (Money perDay, Money total) = CarPricing.Offer(SearchDsl.CentsOf(inner), c.Days);
                offers.Add(new CarOffer(
                    Guid.Parse(category.GetProperty("id").GetString()!),
                    category.GetProperty("acriss").GetString()!,
                    category.GetProperty("name").GetString()!,
                    category.GetProperty("seats").GetInt32(),
                    Enum.Parse<Transmission>(category.GetProperty("transmission").GetString()!),
                    Enum.Parse<FuelType>(category.GetProperty("fuel").GetString()!),
                    category.GetProperty("bags").GetInt32(),
                    category.GetProperty("minimumDriverAge").GetInt32(),
                    SearchDsl.OptionalString(category, "imageUrl") is { } url ? new Uri(url) : null,
                    perDay,
                    total));
            }

            items.Add(new CarSearchItem(
                Guid.Parse(source.GetProperty("locationId").GetString()!),
                source.GetProperty("name").GetString()!,
                source.GetProperty("city").GetString()!,
                SearchDsl.OptionalString(source, "region"),
                source.GetProperty("countryCode").GetString()!,
                SearchDsl.OptionalString(source, "airportCode"),
                location,
                c.Origin is { } origin ? SearchDsl.DistanceKm(origin, location) : null,
                OneWay: false,
                c.Days,
                offers));
        }

        return new CarSearchPage(items, found.Length > c.Top ? SearchDsl.PositionOf(found[c.Top - 1]) : null);
    }
}
