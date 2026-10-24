using System.Globalization;
using System.Text.Json;
using System.Text.Json.Nodes;
using Viatica.Kernel;
using Viatica.Search.Application.Projection;

namespace Viatica.Search.Infrastructure.Querying;

/// <summary>The few JSON fragments both searches build the same way.</summary>
internal static class SearchDsl
{
    /// <summary>
    /// Every day of <c>[first, first + days)</c> is inside one run of days with at least <paramref name="units"/>
    /// left. A range query with <c>contains</c> asks the range index exactly that.
    /// </summary>
    public static JsonObject OpenForAllDays(string path, int units, int firstDay, int days) => new()
    {
        ["range"] = new JsonObject
        {
            [$"{path}.open.{units.ToString(CultureInfo.InvariantCulture)}"] = new JsonObject
            {
                ["gte"] = firstDay,
                ["lte"] = firstDay + days - 1,
                ["relation"] = "contains",
            },
        },
    };

    public static JsonObject Term(string field, JsonNode value) => new() { ["term"] = new JsonObject { [field] = value } };

    public static JsonObject Bool(IEnumerable<JsonNode> filters) => new()
    {
        ["bool"] = new JsonObject { ["filter"] = new JsonArray([.. filters]) },
    };

    /// <summary>Sorts parents by the cheapest of their nested documents that pass <paramref name="unitFilter"/>.</summary>
    public static JsonObject CheapestFirst(string path, string ratesField, JsonNode unitFilter, int firstDay, int nights) => new()
    {
        ["_script"] = new JsonObject
        {
            ["type"] = "number",
            ["order"] = "asc",
            ["script"] = Scripts.Sum(ratesField, firstDay, nights),
            ["nested"] = new JsonObject { ["path"] = path, ["filter"] = unitFilter.DeepClone() },
        },
    };

    /// <summary>The same ordering inside <c>inner_hits</c>, where the nested documents are sorted among themselves.</summary>
    public static JsonObject CheapestFirstInner(string ratesField, int firstDay, int nights) => new()
    {
        ["_script"] = new JsonObject
        {
            ["type"] = "number",
            ["order"] = "asc",
            ["script"] = Scripts.Sum(ratesField, firstDay, nights),
        },
    };

    public static JsonObject GeoCircle(GeoLocation center, double radiusKm) => new()
    {
        ["geo_distance"] = new JsonObject
        {
            ["distance"] = $"{radiusKm.ToString("0.###", CultureInfo.InvariantCulture)}km",
            ["location"] = Point(center),
        },
    };

    public static JsonObject Point(GeoLocation point) => new() { ["lat"] = point.Lat, ["lon"] = point.Lon };

    public static JsonObject Source(params string[] fields) => new() { ["includes"] = new JsonArray([.. fields.Select(static f => (JsonNode?)f)]) };

    public static long CentsOf(JsonElement hit) =>
        hit.GetProperty("fields").GetProperty("sum")[0].GetInt64();

    public static string[] PositionOf(JsonElement hit) =>
        [.. hit.GetProperty("sort").EnumerateArray().Select(static e => e.ValueKind == JsonValueKind.String ? e.GetString()! : e.GetRawText())];

    /// <summary>
    /// Turns the position out of a skiptoken back into <c>search_after</c> values. The token is signed, so its
    /// content is ours; the shape is checked anyway, because a token from an older release must be refused
    /// rather than sent to the index in the wrong form.
    /// </summary>
    public static JsonArray SearchAfter(IReadOnlyList<string> position, string kinds)
    {
        if (position.Count != kinds.Length)
        {
            throw InvalidToken();
        }

        var values = new JsonArray();
        for (int i = 0; i < kinds.Length; i++)
        {
            if (kinds[i] == 'n')
            {
                values.Add(double.TryParse(position[i], NumberStyles.Float, CultureInfo.InvariantCulture, out double number)
                    ? JsonValue.Create(number)
                    : throw InvalidToken());
            }
            else
            {
                values.Add(JsonValue.Create(position[i]));
            }
        }

        return values;
    }

    public static double DistanceKm(GeoLocation from, GeoLocation to)
    {
        const double EarthRadiusKm = 6371;
        double lat1 = double.DegreesToRadians(from.Lat);
        double lat2 = double.DegreesToRadians(to.Lat);
        double dLat = lat2 - lat1;
        double dLon = double.DegreesToRadians(to.Lon - from.Lon);
        double h = (Math.Sin(dLat / 2) * Math.Sin(dLat / 2)) +
                   (Math.Cos(lat1) * Math.Cos(lat2) * Math.Sin(dLon / 2) * Math.Sin(dLon / 2));
        return Math.Round(2 * EarthRadiusKm * Math.Asin(Math.Sqrt(h)), 1);
    }

    public static GeoLocation LocationOf(JsonElement source)
    {
        JsonElement location = source.GetProperty("location");
        return new GeoLocation(location.GetProperty("lat").GetDouble(), location.GetProperty("lon").GetDouble());
    }

    public static string? OptionalString(JsonElement element, string name) =>
        element.TryGetProperty(name, out JsonElement value) ? value.GetString() : null;

    private static ApiErrorException InvalidToken() =>
        ApiErrorException.Invalid(ApiErrorCode.InvalidSkipToken, "The $skiptoken does not belong to this query.", "$skiptoken");
}
