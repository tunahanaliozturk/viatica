using System.Text.Json;

namespace Viatica.Search.IntegrationTests;

public sealed class AutocompleteTests(SearchApp app) : IClassFixture<SearchApp>, IAsyncLifetime
{
    public async ValueTask InitializeAsync()
    {
        await app.ResetAsync();
        await app.SeedAsync(Seed.Property(Guid.NewGuid(), "Hotel Lisboa Centro", "Lisbon", 4, 38.72, -9.14, [], new RoomSpec("Standard", 2, 2, 90m)));
        await app.SeedAsync(Seed.Property(Guid.NewGuid(), "Lisbon Marina Resort", "Lisbon", 5, 38.70, -9.18, [], new RoomSpec("Standard", 2, 2, 190m)));
        await app.SeedAsync(Seed.Property(Guid.NewGuid(), "Porto Palace", "Porto", 5, 41.15, -8.61, [], new RoomSpec("Standard", 2, 2, 150m)));
        await app.SeedAsync(Seed.Property(Guid.NewGuid(), "Zürich Central", "Zürich", 4, 47.37, 8.54, [], new RoomSpec("Standard", 2, 2, 210m)));
        await app.SeedAsync(Seed.Location(Guid.NewGuid(), "Faro Airport", "Faro", 37.01, -7.97, Seed.CarPartner, "FAO", new CarSpec("ECMR", "Economy", 4, Viatica.Contracts.Transmission.Manual, 2, 30m)));
        await app.SeedAsync(Seed.Location(Guid.NewGuid(), "Lisbon Airport", "Lisbon", 38.77, -9.13, Seed.CarPartner, "LIS", new CarSpec("ECMR", "Economy", 4, Viatica.Contracts.Transmission.Manual, 2, 30m)));
    }

    public ValueTask DisposeAsync() => ValueTask.CompletedTask;

    private static (string Kind, string Name)[] Places(JsonElement body) =>
    [
        .. body.GetProperty("value").EnumerateArray().Select(static p => (p.GetProperty("kind").GetString()!, p.GetProperty("name").GetString()!)),
    ];

    [Fact]
    public async Task A_typed_airport_code_comes_first_then_the_city_once_then_other_airports_and_the_properties()
    {
        (int status, JsonElement body) = await app.GetAsync("/autocomplete?q=lis");

        status.ShouldBe(200);
        (string Kind, string Name)[] places = Places(body);
        places[..2].ShouldBe([("Airport", "Lisbon Humberto Delgado Airport"), ("City", "Lisbon")]);

        // Any word may match: "lis" also begins Liszt, as in Budapest Ferenc Liszt Airport. Other airports follow cities.
        places[2].ShouldBe(("Airport", "Budapest Ferenc Liszt Airport"));
        places[3..].ShouldBe([("Property", "Hotel Lisboa Centro"), ("Property", "Lisbon Marina Resort")], ignoreOrder: true);
        JsonElement airport = body.GetProperty("value")[0];
        airport.GetProperty("code").GetString().ShouldBe("LIS");
        airport.GetProperty("detail").GetString().ShouldBe("Lisbon, PT");
        body.GetProperty("value")[1].GetProperty("detail").GetString().ShouldBe("PT");
        body.GetProperty("value")[3].TryGetProperty("id", out _).ShouldBeTrue();
        body.GetProperty("value")[3].GetProperty("location").GetProperty("lat").GetDouble().ShouldBeInRange(38.7, 38.73);
    }

    [Fact]
    public async Task Cities_come_from_both_stays_and_car_locations_and_ignore_case_and_accents()
    {
        (_, JsonElement faro) = await app.GetAsync("/autocomplete?q=FAR&types=city");
        (_, JsonElement zurich) = await app.GetAsync("/autocomplete?q=zurich&types=city");

        Places(faro).ShouldBe([("City", "Faro")]);
        Places(zurich).ShouldBe([("City", "Zürich")]);
    }

    [Fact]
    public async Task Airports_come_from_the_list_by_code_city_or_name()
    {
        (_, JsonElement byName) = await app.GetAsync("/autocomplete?q=heathrow&types=airport");
        (_, JsonElement byCity) = await app.GetAsync("/autocomplete?q=ist&types=airport");
        (_, JsonElement byCode) = await app.GetAsync("/autocomplete?q=JFK&types=airport");

        Places(byName).ShouldBe([("Airport", "London Heathrow Airport")]);
        Places(byCity)[0].ShouldBe(("Airport", "Istanbul Airport"));
        Places(byCode)[0].ShouldBe(("Airport", "New York John F. Kennedy Airport"));
    }

    [Fact]
    public async Task Property_names_match_on_any_word_and_an_unfinished_last_word()
    {
        (_, JsonElement body) = await app.GetAsync("/autocomplete?q=marina%20res&types=property");
        (_, JsonElement second) = await app.GetAsync("/autocomplete?q=palace&types=property");

        Places(body).ShouldBe([("Property", "Lisbon Marina Resort")]);
        Places(second).ShouldBe([("Property", "Porto Palace")]);
    }

    [Fact]
    public async Task The_kinds_asked_for_limit_the_answer_and_top_limits_its_length()
    {
        (_, JsonElement cityOnly) = await app.GetAsync("/autocomplete?q=lis&types=city");
        (_, JsonElement two) = await app.GetAsync("/autocomplete?q=lis&$top=2");

        Places(cityOnly).ShouldBe([("City", "Lisbon")]);
        two.GetProperty("value").GetArrayLength().ShouldBe(2);
        two.TryGetProperty("nextLink", out _).ShouldBeFalse();
    }

    [Fact]
    public async Task An_unpublished_property_is_not_suggested()
    {
        var room = new RoomSpec("Standard", 2, 2, 90m);
        var id = Guid.NewGuid();
        await app.SeedAsync(Seed.Property(id, "Quiet Corner Inn", "Lisbon", 3, 38.7, -9.1, [], room));
        (_, JsonElement before) = await app.GetAsync("/autocomplete?q=quiet&types=property");
        await app.SeedAsync(new Viatica.Contracts.StayUnpublished(id, 2));
        (_, JsonElement after) = await app.GetAsync("/autocomplete?q=quiet&types=property");

        Places(before).ShouldBe([("Property", "Quiet Corner Inn")]);
        Places(after).ShouldBeEmpty();
    }

    [Fact]
    public async Task Short_text_unknown_kinds_and_unknown_parameters_are_refused()
    {
        (int shortStatus, JsonElement tooShort) = await app.GetAsync("/autocomplete?q=l");
        (int kindStatus, _) = await app.GetAsync("/autocomplete?q=lis&types=street");
        (int paramStatus, _) = await app.GetAsync("/autocomplete?q=lis&colour=red");
        (int missingStatus, _) = await app.GetAsync("/autocomplete");

        shortStatus.ShouldBe(400);
        tooShort.GetProperty("error").GetProperty("details")[0].GetProperty("code").GetString().ShouldBe("QueryTooShort");
        kindStatus.ShouldBe(400);
        paramStatus.ShouldBe(400);
        missingStatus.ShouldBe(400);
    }
}
