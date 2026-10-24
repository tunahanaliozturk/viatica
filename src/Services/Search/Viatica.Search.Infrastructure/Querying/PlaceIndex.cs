using System.Text.Json;
using System.Text.Json.Nodes;
using Viatica.Search.Application.Places;
using Viatica.Search.Application.Projection;
using Viatica.Search.Infrastructure.Indexing;
using HttpMethod = OpenSearch.Net.HttpMethod;

namespace Viatica.Search.Infrastructure.Querying;

/// <summary>
/// Cities and property names for autocomplete. Both fields are analysed with accents and case folded and matched
/// with <c>match_bool_prefix</c>: every word typed must be present, and the last one may be unfinished, so "new yo"
/// finds New York and "zur" finds Zurich.
/// </summary>
public sealed class PlaceIndex(OpenSearchRest rest) : IPlaceIndex
{
    public async Task<IReadOnlyList<Place>> CitiesAsync(string text, int limit, CancellationToken cancellationToken)
    {
        // A city is a value on stays and on car locations, so one request over both aliases lists every city that
        // has something to book, once, ordered by how much there is.
        var body = new JsonObject
        {
            ["size"] = 0,
            ["query"] = SearchDsl.Bool([SearchDsl.Term("published", true), Prefix("city", text)]),
            ["aggs"] = new JsonObject
            {
                ["cities"] = new JsonObject
                {
                    ["terms"] = new JsonObject { ["field"] = "city.key", ["size"] = limit },
                    ["aggs"] = new JsonObject
                    {
                        ["sample"] = new JsonObject
                        {
                            ["top_hits"] = new JsonObject
                            {
                                ["size"] = 1,
                                ["_source"] = SearchDsl.Source("city", "region", "countryCode", "location"),
                            },
                        },
                    },
                },
            },
        };

        using JsonDocument answer = await rest.SendAsync(
            HttpMethod.POST, $"{IndexDefinitions.Stays},{IndexDefinitions.Cars}/_search", body.ToJsonString(), cancellationToken);

        return
        [
            .. answer.RootElement.GetProperty("aggregations").GetProperty("cities").GetProperty("buckets").EnumerateArray()
                .Select(static bucket =>
                {
                    JsonElement source = bucket.GetProperty("sample").GetProperty("hits").GetProperty("hits")[0].GetProperty("_source");
                    string? region = SearchDsl.OptionalString(source, "region");
                    string country = source.GetProperty("countryCode").GetString()!;
                    return new Place(
                        PlaceKind.City,
                        source.GetProperty("city").GetString()!,
                        region is null ? country : $"{region}, {country}",
                        null,
                        null,
                        SearchDsl.LocationOf(source));
                }),
        ];
    }

    public async Task<IReadOnlyList<Place>> PropertiesAsync(string text, int limit, CancellationToken cancellationToken)
    {
        var body = new JsonObject
        {
            ["size"] = limit,
            ["_source"] = SearchDsl.Source("propertyId", "name", "city", "countryCode", "location"),
            ["query"] = SearchDsl.Bool([SearchDsl.Term("published", true), Prefix("name", text)]),
            ["sort"] = new JsonArray(
                new JsonObject { ["_score"] = "desc" },
                new JsonObject { ["starRating"] = "desc" },
                new JsonObject { ["propertyId"] = "asc" }),
        };

        using JsonDocument answer = await rest.SendAsync(
            HttpMethod.POST, $"{IndexDefinitions.Stays}/_search", body.ToJsonString(), cancellationToken);

        return
        [
            .. answer.RootElement.GetProperty("hits").GetProperty("hits").EnumerateArray()
                .Select(static hit =>
                {
                    JsonElement source = hit.GetProperty("_source");
                    return new Place(
                        PlaceKind.Property,
                        source.GetProperty("name").GetString()!,
                        $"{source.GetProperty("city").GetString()}, {source.GetProperty("countryCode").GetString()}",
                        null,
                        Guid.Parse(source.GetProperty("propertyId").GetString()!),
                        SearchDsl.LocationOf(source));
                }),
        ];
    }

    private static JsonObject Prefix(string field, string text) => new()
    {
        ["match_bool_prefix"] = new JsonObject
        {
            [field] = new JsonObject { ["query"] = text, ["operator"] = "and" },
        },
    };
}
