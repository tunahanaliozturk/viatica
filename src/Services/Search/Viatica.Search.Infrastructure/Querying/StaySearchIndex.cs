using System.Text.Json;
using System.Text.Json.Nodes;
using Viatica.Kernel;
using Viatica.Search.Application;
using Viatica.Search.Application.Projection;
using Viatica.Search.Application.Stays;
using Viatica.Search.Infrastructure.Indexing;
using HttpMethod = OpenSearch.Net.HttpMethod;

namespace Viatica.Search.Infrastructure.Querying;

/// <summary>
/// "Which properties have a room type free on every night of the stay, and what does the cheapest cost" as one
/// OpenSearch request. The shape of the query, and why, is in ADR 0005.
/// </summary>
public sealed class StaySearchIndex(OpenSearchRest rest) : IStaySearchIndex
{
    private const string Path = "rooms";
    private const string Rates = "rooms.rates";

    public async Task<StaySearchPage> SearchAsync(StaySearchCriteria criteria, CancellationToken cancellationToken)
    {
        JsonObject body = Build(criteria);
        using JsonDocument answer = await rest.SendAsync(HttpMethod.POST, $"{IndexDefinitions.Stays}/_search", body.ToJsonString(), cancellationToken);
        return Read(answer.RootElement, criteria);
    }

    internal static JsonObject Build(StaySearchCriteria c)
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
                        ["size"] = 1,
                        ["_source"] = SearchDsl.Source("rooms.id", "rooms.name", "rooms.maxOccupancy", "rooms.beds"),
                        ["sort"] = new JsonArray(SearchDsl.CheapestFirstInner(Rates, c.FirstNight, c.Nights)),
                        ["script_fields"] = new JsonObject { ["sum"] = new JsonObject { ["script"] = Scripts.Sum(Rates, c.FirstNight, c.Nights) } },
                    },
                },
            },
        };

        if (c.Destination is not null)
        {
            filters.Add(new JsonObject
            {
                ["multi_match"] = new JsonObject
                {
                    ["query"] = c.Destination,
                    ["fields"] = new JsonArray("name^2", "city^3", "region"),
                    ["type"] = "best_fields",
                    ["operator"] = "and",
                    ["fuzziness"] = "AUTO",
                },
            });
        }

        if (c.Box is { } box)
        {
            filters.Add(new JsonObject
            {
                ["geo_bounding_box"] = new JsonObject
                {
                    ["location"] = new JsonObject
                    {
                        ["top_left"] = new JsonObject { ["lat"] = box.North, ["lon"] = box.West },
                        ["bottom_right"] = new JsonObject { ["lat"] = box.South, ["lon"] = box.East },
                    },
                },
            });
        }

        if (c.Stars.Count > 0)
        {
            filters.Add(new JsonObject { ["terms"] = new JsonObject { ["starRating"] = new JsonArray([.. c.Stars.Select(static s => (JsonNode?)s)]) } });
        }

        foreach (string amenity in c.Amenities)
        {
            filters.Add(SearchDsl.Term("amenities", amenity));
        }

        (JsonArray sort, string kinds) = Sort(c, unit);
        bool firstPage = c.After is null;
        var body = new JsonObject
        {
            ["size"] = c.Top + 1,
            ["track_total_hits"] = firstPage,
            ["_source"] = SearchDsl.Source("propertyId", "name", "starRating", "city", "region", "countryCode", "location", "amenities"),
            ["query"] = SearchDsl.Bool(filters),
            ["sort"] = sort,
        };

        if (c.After is not null)
        {
            body["search_after"] = SearchDsl.SearchAfter(c.After, kinds);
        }

        if (firstPage)
        {
            // Over the properties that matched everything, availability included, so a facet never counts a
            // property the list below it would not show.
            body["aggs"] = new JsonObject
            {
                ["stars"] = new JsonObject { ["terms"] = new JsonObject { ["field"] = "starRating", ["size"] = 5 } },
                ["amenities"] = new JsonObject { ["terms"] = new JsonObject { ["field"] = "amenities", ["size"] = 50 } },
            };
        }

        return body;
    }

    /// <summary>What a room type must satisfy to be offered: listed, free on every night, big enough, within the price range.</summary>
    private static JsonObject Unit(StaySearchCriteria c)
    {
        var filters = new List<JsonNode>
        {
            SearchDsl.Term("rooms.listed", true),
            SearchDsl.OpenForAllDays(Path, c.Rooms, c.FirstNight, c.Nights),
            new JsonObject { ["range"] = new JsonObject { ["rooms.maxOccupancy"] = new JsonObject { ["gte"] = c.GuestsPerRoom } } },
        };

        if (c.MinNightlyCents is not null || c.MaxNightlyCents is not null)
        {
            // Compared as whole-stay sums so no division, and so no rounding, happens per document.
            long min = (c.MinNightlyCents ?? 0) * c.Nights;
            long max = c.MaxNightlyCents is { } cap ? cap * c.Nights : long.MaxValue;
            filters.Add(new JsonObject { ["script"] = new JsonObject { ["script"] = Scripts.SumWithin(Rates, c.FirstNight, c.Nights, min, max) } });
        }

        return SearchDsl.Bool(filters);
    }

    private static (JsonArray Sort, string Kinds) Sort(StaySearchCriteria c, JsonObject unit)
    {
        JsonObject id = new() { ["propertyId"] = "asc" };
        JsonObject price = SearchDsl.CheapestFirst(Path, Rates, unit, c.FirstNight, c.Nights);

        switch (c.Sort)
        {
            case StaySort.Rating:
                return ([new JsonObject { ["starRating"] = "desc" }, price, id], "nns");
            case StaySort.Distance:
                GeoLocation origin = c.DistanceOrigin!;
                return (
                [
                    new JsonObject
                    {
                        ["_geo_distance"] = new JsonObject
                        {
                            ["location"] = SearchDsl.Point(origin),
                            ["order"] = "asc",
                            ["unit"] = "m",
                            ["distance_type"] = "arc",
                        },
                    },
                    id,
                ], "ns");
            default:
                return ([price, id], "ns");
        }
    }

    private static StaySearchPage Read(JsonElement root, StaySearchCriteria c)
    {
        JsonElement hits = root.GetProperty("hits");
        List<StaySearchItem> items = [];
        string[]? next = null;

        JsonElement[] found = [.. hits.GetProperty("hits").EnumerateArray()];
        foreach (JsonElement hit in found.Take(c.Top))
        {
            JsonElement source = hit.GetProperty("_source");
            JsonElement room = hit.GetProperty("inner_hits").GetProperty(Path).GetProperty("hits").GetProperty("hits")[0];
            JsonElement roomSource = room.GetProperty("_source");
            GeoLocation location = SearchDsl.LocationOf(source);

            (Money perNight, Money total) = StayPricing.Offer(SearchDsl.CentsOf(room), c.Nights, c.Rooms);
            items.Add(new StaySearchItem(
                Guid.Parse(source.GetProperty("propertyId").GetString()!),
                source.GetProperty("name").GetString()!,
                source.GetProperty("starRating").GetInt32(),
                source.GetProperty("city").GetString()!,
                SearchDsl.OptionalString(source, "region"),
                source.GetProperty("countryCode").GetString()!,
                location,
                [.. source.GetProperty("amenities").EnumerateArray().Select(static a => a.GetString()!)],
                c.DistanceOrigin is { } origin ? SearchDsl.DistanceKm(origin, location) : null,
                new StayRoomOffer(
                    Guid.Parse(roomSource.GetProperty("id").GetString()!),
                    roomSource.GetProperty("name").GetString()!,
                    roomSource.GetProperty("maxOccupancy").GetInt32(),
                    roomSource.GetProperty("beds").GetString()!),
                c.Nights,
                c.Rooms,
                perNight,
                total));
        }

        // One more row than the page holds was asked for. Its presence is the whole answer to "is there a next page".
        if (found.Length > c.Top)
        {
            next = SearchDsl.PositionOf(found[c.Top - 1]);
        }

        if (c.After is not null)
        {
            return new StaySearchPage(items, next, null, null);
        }

        JsonElement aggregations = root.GetProperty("aggregations");
        return new StaySearchPage(
            items,
            next,
            hits.GetProperty("total").GetProperty("value").GetInt64(),
            new StayFacets(
                [.. aggregations.GetProperty("stars").GetProperty("buckets").EnumerateArray()
                    .Select(static b => new FacetCount<int>(b.GetProperty("key").GetInt32(), b.GetProperty("doc_count").GetInt64()))
                    .OrderBy(static f => f.Value)],
                [.. aggregations.GetProperty("amenities").GetProperty("buckets").EnumerateArray()
                    .Select(static b => new FacetCount<string>(b.GetProperty("key").GetString()!, b.GetProperty("doc_count").GetInt64()))]));
    }
}
