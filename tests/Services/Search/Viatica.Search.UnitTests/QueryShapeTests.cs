using System.Text.Json;
using System.Text.Json.Nodes;
using Viatica.Kernel;
using Viatica.Search.Application.Cars;
using Viatica.Search.Application.Projection;
using Viatica.Search.Application.Stays;
using Viatica.Search.Infrastructure.Indexing;
using Viatica.Search.Infrastructure.Querying;

namespace Viatica.Search.UnitTests;

/// <summary>
/// What the query builders send, without an index to send it to. The answers those queries give are checked
/// against a real OpenSearch in the integration tests; these pin the parts that are easy to get wrong silently.
/// </summary>
public sealed class QueryShapeTests
{
    private static readonly int FirstNight = Events.Today.AddDays(10).DayNumber;

    private static StaySearchCriteria Criteria(Func<StaySearchCriteria, StaySearchCriteria>? change = null)
    {
        var criteria = new StaySearchCriteria(
            "lisbon", null, null, FirstNight, 3, 2, 1, null, null, [], [], StaySort.Price, 20, null);
        return change?.Invoke(criteria) ?? criteria;
    }

    [Fact]
    public void Every_night_of_the_stay_is_asked_for_in_one_range_query_on_the_threshold_for_the_rooms_wanted()
    {
        JsonObject body = StaySearchIndex.Build(Criteria(static c => c with { Rooms = 2 }));

        string json = body.ToJsonString();
        JsonNode range = body["query"]!["bool"]!["filter"]![1]!["nested"]!["query"]!["bool"]!["filter"]![1]!["range"]!;
        range["rooms.open.2"]!["gte"]!.GetValue<int>().ShouldBe(FirstNight);
        range["rooms.open.2"]!["lte"]!.GetValue<int>().ShouldBe(FirstNight + 2);
        range["rooms.open.2"]!["relation"]!.GetValue<string>().ShouldBe("contains");
        json.ShouldContain("\"size\":21");
    }

    [Fact]
    public void The_first_page_asks_for_the_total_and_the_facets_and_later_pages_do_not()
    {
        JsonObject first = StaySearchIndex.Build(Criteria());
        JsonObject next = StaySearchIndex.Build(Criteria(static c => c with { After = ["24033.0", "0192f0c4-0000-7000-8000-000000000001"] }));

        first["track_total_hits"]!.GetValue<bool>().ShouldBeTrue();
        first["aggs"]!["stars"].ShouldNotBeNull();
        first["aggs"]!["amenities"].ShouldNotBeNull();
        next["track_total_hits"]!.GetValue<bool>().ShouldBeFalse();
        next["aggs"].ShouldBeNull();
        next["search_after"]!.AsArray().Count.ShouldBe(2);
        next["search_after"]![0]!.GetValue<double>().ShouldBe(24033d);
    }

    [Fact]
    public void Each_sort_has_a_unique_last_key_so_paging_never_repeats_or_skips()
    {
        foreach (StaySort sort in Enum.GetValues<StaySort>())
        {
            JsonArray order = StaySearchIndex.Build(Criteria(c => c with { Sort = sort, DistanceOrigin = new GeoLocation(1, 1) }))["sort"]!.AsArray();

            order[^1]!["propertyId"]!.GetValue<string>().ShouldBe("asc", sort.ToString());
        }
    }

    [Fact]
    public void A_skiptoken_position_of_the_wrong_shape_is_refused()
    {
        ApiErrorException error = Should.Throw<ApiErrorException>(() =>
            StaySearchIndex.Build(Criteria(static c => c with { After = ["only-one"] })));

        error.Error.Code.ShouldBe("InvalidSkipToken");
        Should.Throw<ApiErrorException>(() =>
            StaySearchIndex.Build(Criteria(static c => c with { After = ["not-a-number", "id"] }))).Error.Code.ShouldBe("InvalidSkipToken");
    }

    [Fact]
    public void A_price_range_becomes_a_whole_stay_comparison_in_cents()
    {
        JsonObject body = StaySearchIndex.Build(Criteria(static c => c with { MinNightlyCents = 5000, MaxNightlyCents = 12000 }));

        JsonNode script = body["query"]!["bool"]!["filter"]![1]!["nested"]!["query"]!["bool"]!["filter"]![3]!["script"]!["script"]!;
        script["params"]!["min"]!.GetValue<long>().ShouldBe(15000);
        script["params"]!["max"]!.GetValue<long>().ShouldBe(36000);
    }

    [Fact]
    public void Car_search_limits_a_one_way_rental_to_the_partner_of_the_drop_off()
    {
        var partner = Guid.NewGuid();
        var criteria = new CarSearchCriteria(null, new GeoLocation(38.7, -9.1), 25, partner, FirstNight, 3, null, null, null, null, null, 20, null);

        string json = CarSearchIndex.Build(criteria).ToJsonString();

        json.ShouldContain($"\"partnerId\":\"{partner}\"");
        json.ShouldContain("\"geo_distance\"");
        json.ShouldContain("categories.open.1");
    }

    [Fact]
    public void A_stay_document_writes_its_search_fields_derived_from_the_ledger_and_reads_back_the_same()
    {
        var room = Guid.NewGuid();
        StayDocument document = new StayProjection().Apply(null, Events.Listing(Guid.NewGuid(), 1, roomTypes: [room]), Events.FirstDay, out _)!;
        document = new StayProjection().Apply(document, Events.Nights(document.PropertyId, room, 1, Events.Today.AddDays(10), 3, 2, 99.5m), Events.FirstDay, out _)!;
        var info = (System.Text.Json.Serialization.Metadata.JsonTypeInfo<StayDocument>)IndexJsonContext.Default.GetTypeInfo(typeof(StayDocument))!;

        string json = JsonSerializer.Serialize(document, info);
        StayDocument read = JsonSerializer.Deserialize(json, info)!;

        using JsonDocument parsed = JsonDocument.Parse(json);
        JsonElement stored = parsed.RootElement.GetProperty("rooms")[0];
        stored.GetProperty("open").GetProperty("2")[0].GetProperty("gte").GetInt32().ShouldBe(FirstNight);
        stored.GetProperty("open").GetProperty("3").GetArrayLength().ShouldBe(0);
        stored.GetProperty("rates").GetArrayLength().ShouldBe(3);
        read.Rooms.Single().State.RateCents.ShouldBe([9950L, 9950L, 9950L]);
        parsed.RootElement.GetProperty("location").GetProperty("lat").GetDouble().ShouldBe(38.72);
    }
}
