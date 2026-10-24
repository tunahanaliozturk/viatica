using System.Text.Json;
using Viatica.Contracts;

namespace Viatica.Search.IntegrationTests;

public sealed class CarSearchTests(SearchApp app) : IClassFixture<SearchApp>, IAsyncLifetime
{
    private static readonly CarSpec Economy = new("ECMR", "Economy", 4, Transmission.Manual, 3, 30m);
    private static readonly CarSpec Compact = new("CCAR", "Compact Automatic", 5, Transmission.Automatic, 3, 45m);
    private static readonly CarSpec Suv = new("IFAR", "SUV", 7, Transmission.Automatic, 1, 90m);

    public async ValueTask InitializeAsync() => await app.ResetAsync();

    public ValueTask DisposeAsync() => ValueTask.CompletedTask;

    // Pick up at 10:00 on day 5 and return at 10:00 on day 8: three rental days, 5, 6 and 7.
    private static string Period(int pickUp = 5, int days = 3) =>
        $"pickUpAt={SearchApp.Base.AddDays(pickUp):yyyy-MM-dd}T10:00:00%2B01:00&dropOffAt={SearchApp.Base.AddDays(pickUp + days):yyyy-MM-dd}T10:00:00%2B01:00";

    private static string[] Names(JsonElement body) => [.. body.GetProperty("value").EnumerateArray().Select(static i => i.GetProperty("name").GetString()!)];

    private static string[] Offers(JsonElement location) =>
        [.. location.GetProperty("offers").EnumerateArray().Select(static o => o.GetProperty("name").GetString()!)];

    private async Task<Guid> AddAsync(string name, Guid partner, double lat, double lon, params CarSpec[] categories)
    {
        var id = Guid.NewGuid();
        await app.SeedAsync(Seed.Location(id, name, "Lisbon", lat, lon, partner, null, categories));
        return id;
    }

    [Fact]
    public async Task A_location_is_found_with_the_cars_free_on_every_rental_day_cheapest_first()
    {
        Guid id = await AddAsync("Lisbon Airport", Seed.CarPartner, 38.77, -9.13, Suv, Economy, Compact);

        (int status, JsonElement body) = await app.GetAsync($"/cars/search?pickUpLocationId={id}&{Period()}");

        status.ShouldBe(200);
        JsonElement location = body.GetProperty("value").EnumerateArray().Single();
        location.GetProperty("locationId").GetGuid().ShouldBe(id);
        location.GetProperty("days").GetInt32().ShouldBe(3);
        location.GetProperty("oneWay").GetBoolean().ShouldBeFalse();
        Offers(location).ShouldBe(["Economy", "Compact Automatic", "SUV"]);
        JsonElement economy = location.GetProperty("offers")[0];
        economy.GetProperty("acriss").GetString().ShouldBe("ECMR");
        economy.GetProperty("pricePerDay").GetProperty("amount").GetString().ShouldBe("30.00");
        economy.GetProperty("total").GetProperty("amount").GetString().ShouldBe("90.00");
        economy.GetProperty("transmission").GetString().ShouldBe("Manual");
    }

    [Fact]
    public async Task One_sold_out_day_removes_that_category_and_a_location_with_none_left_disappears()
    {
        var onlyEconomy = new CarSpec("ECMR", "Economy", 4, Transmission.Manual, 2, 30m);
        var suv = new CarSpec("IFAR", "SUV", 7, Transmission.Automatic, 2, 90m);
        Guid both = await AddAsync("Two Categories", Seed.CarPartner, 38.77, -9.13, onlyEconomy, suv);
        var lone = new CarSpec("ECMR", "Economy", 4, Transmission.Manual, 2, 31m);
        Guid single = await AddAsync("One Category", Seed.CarPartner, 38.80, -9.20, lone);

        // The economy car at the first location and the only car at the second are gone on the middle day.
        await app.SeedAsync(
            new CarAvailabilityChanged(both, onlyEconomy.Id, 2, [new DateAvailability(SearchApp.Base.AddDays(6), 0, new Price(30m, "EUR"))]),
            new CarAvailabilityChanged(single, lone.Id, 2, [new DateAvailability(SearchApp.Base.AddDays(6), 0, new Price(31m, "EUR"))]));

        (_, JsonElement body) = await app.GetAsync($"/cars/search?lat=38.78&lon=-9.15&radiusKm=30&{Period()}");

        JsonElement location = body.GetProperty("value").EnumerateArray().Single();
        location.GetProperty("name").GetString().ShouldBe("Two Categories");
        Offers(location).ShouldBe(["SUV"]);

        // A rental that avoids the sold out day finds both again.
        (_, JsonElement shorter) = await app.GetAsync($"/cars/search?lat=38.78&lon=-9.15&radiusKm=30&{Period(pickUp: 3, days: 2)}");
        Names(shorter).Length.ShouldBe(2);
    }

    [Fact]
    public async Task Category_transmission_seats_and_price_filter_the_offers()
    {
        Guid id = await AddAsync("Lisbon Airport", Seed.CarPartner, 38.77, -9.13, Economy, Compact, Suv);
        string place = $"pickUpLocationId={id}&{Period()}";

        (_, JsonElement acriss) = await app.GetAsync($"/cars/search?{place}&category=C");
        (_, JsonElement exact) = await app.GetAsync($"/cars/search?{place}&category=ecmr");
        (_, JsonElement automatic) = await app.GetAsync($"/cars/search?{place}&transmission=Automatic");
        (_, JsonElement seats) = await app.GetAsync($"/cars/search?{place}&minSeats=7");
        (_, JsonElement cheap) = await app.GetAsync($"/cars/search?{place}&maxPrice=40");
        (_, JsonElement range) = await app.GetAsync($"/cars/search?{place}&minPrice=40&maxPrice=50");
        (_, JsonElement none) = await app.GetAsync($"/cars/search?{place}&category=C&transmission=Manual");

        Offers(acriss.GetProperty("value")[0]).ShouldBe(["Compact Automatic"]);
        Offers(exact.GetProperty("value")[0]).ShouldBe(["Economy"]);
        Offers(automatic.GetProperty("value")[0]).ShouldBe(["Compact Automatic", "SUV"]);
        Offers(seats.GetProperty("value")[0]).ShouldBe(["SUV"]);
        Offers(cheap.GetProperty("value")[0]).ShouldBe(["Economy"]);
        Offers(range.GetProperty("value")[0]).ShouldBe(["Compact Automatic"]);
        none.GetProperty("value").GetArrayLength().ShouldBe(0);
    }

    [Fact]
    public async Task A_point_and_a_radius_find_the_locations_within_it_and_report_the_distance()
    {
        await AddAsync("Near", Seed.CarPartner, 38.72, -9.14, Economy);
        await AddAsync("Edge", Seed.CarPartner, 38.90, -9.14, Economy);
        await AddAsync("Far", Seed.CarPartner, 41.15, -8.61, Economy);

        (_, JsonElement small) = await app.GetAsync($"/cars/search?lat=38.72&lon=-9.14&radiusKm=5&{Period()}");
        (_, JsonElement wide) = await app.GetAsync($"/cars/search?lat=38.72&lon=-9.14&radiusKm=50&{Period()}");
        (_, JsonElement defaulted) = await app.GetAsync($"/cars/search?lat=38.72&lon=-9.14&{Period()}");

        Names(small).ShouldBe(["Near"]);
        Names(wide).ShouldBe(["Near", "Edge"], ignoreOrder: true);
        Names(defaulted).ShouldBe(["Near", "Edge"], ignoreOrder: true);
        small.GetProperty("value")[0].GetProperty("distanceKm").GetDouble().ShouldBeLessThan(0.1);
    }

    [Fact]
    public async Task A_one_way_rental_is_offered_between_locations_of_one_partner_only()
    {
        Guid pickUp = await AddAsync("Lisbon Airport", Seed.CarPartner, 38.77, -9.13, Economy);
        Guid sameDrop = await AddAsync("Lisbon Downtown", Seed.CarPartner, 38.72, -9.14, new CarSpec("ECMR", "Economy", 4, Transmission.Manual, 3, 33m));
        Guid rivalDrop = await AddAsync("Rival Downtown", Seed.OtherCarPartner, 38.73, -9.15, new CarSpec("ECMR", "Economy", 4, Transmission.Manual, 3, 28m));

        (int sameStatus, JsonElement same) = await app.GetAsync($"/cars/search?pickUpLocationId={pickUp}&dropOffLocationId={sameDrop}&{Period()}");
        (int rivalStatus, JsonElement rival) = await app.GetAsync($"/cars/search?pickUpLocationId={pickUp}&dropOffLocationId={rivalDrop}&{Period()}");
        (int missingStatus, JsonElement missing) = await app.GetAsync($"/cars/search?pickUpLocationId={pickUp}&dropOffLocationId={Guid.NewGuid()}&{Period()}");
        (int backStatus, JsonElement back) = await app.GetAsync($"/cars/search?pickUpLocationId={pickUp}&dropOffLocationId={pickUp}&{Period()}");

        sameStatus.ShouldBe(200);
        same.GetProperty("value")[0].GetProperty("oneWay").GetBoolean().ShouldBeTrue();
        rivalStatus.ShouldBe(400);
        rival.GetProperty("error").GetProperty("code").GetString().ShouldBe("OneWayNotAllowed");
        rival.GetProperty("error").GetProperty("target").GetString().ShouldBe("dropOffLocationId");
        missingStatus.ShouldBe(404);
        missing.GetProperty("error").GetProperty("code").GetString().ShouldBe("LocationNotFound");
        backStatus.ShouldBe(200);
        back.GetProperty("value")[0].GetProperty("oneWay").GetBoolean().ShouldBeFalse();
    }

    [Fact]
    public async Task A_point_search_with_a_drop_off_location_lists_only_that_partners_locations()
    {
        Guid mine = await AddAsync("Mine A", Seed.CarPartner, 38.72, -9.14, Economy);
        Guid dropOff = await AddAsync("Mine B", Seed.CarPartner, 38.74, -9.15, new CarSpec("ECMR", "Economy", 4, Transmission.Manual, 3, 33m));
        await AddAsync("Rival", Seed.OtherCarPartner, 38.73, -9.14, new CarSpec("ECMR", "Economy", 4, Transmission.Manual, 3, 20m));

        (_, JsonElement body) = await app.GetAsync($"/cars/search?lat=38.73&lon=-9.14&radiusKm=10&dropOffLocationId={dropOff}&{Period()}");

        JsonElement[] items = [.. body.GetProperty("value").EnumerateArray()];
        items.Select(static i => i.GetProperty("name").GetString()).Order().ShouldBe(["Mine A", "Mine B"]);
        items.Single(i => i.GetProperty("locationId").GetGuid() == mine).GetProperty("oneWay").GetBoolean().ShouldBeTrue();
        items.Single(i => i.GetProperty("locationId").GetGuid() == dropOff).GetProperty("oneWay").GetBoolean().ShouldBeFalse();
    }

    [Fact]
    public async Task An_unpublished_location_is_neither_offered_nor_a_valid_pick_up()
    {
        Guid id = await AddAsync("Closing", Seed.CarPartner, 38.77, -9.13, Economy);
        await app.SeedAsync(new CarLocationUnpublished(id, 2));

        (int status, JsonElement body) = await app.GetAsync($"/cars/search?pickUpLocationId={id}&{Period()}");
        (_, JsonElement byPoint) = await app.GetAsync($"/cars/search?lat=38.77&lon=-9.13&{Period()}");

        status.ShouldBe(404);
        body.GetProperty("error").GetProperty("code").GetString().ShouldBe("LocationNotFound");
        byPoint.GetProperty("value").GetArrayLength().ShouldBe(0);
    }

    [Fact]
    public async Task Cars_page_by_keyset_and_a_request_must_name_a_place_and_a_sensible_period()
    {
        for (int i = 0; i < 7; i++)
        {
            await AddAsync($"Location {i}", Seed.CarPartner, 38.72 + (i * 0.001), -9.14, new CarSpec("ECMR", "Economy", 4, Transmission.Manual, 3, 30m + i));
        }

        List<string> names = [];
        (_, JsonElement page) = await app.GetAsync($"/cars/search?lat=38.72&lon=-9.14&{Period()}&$top=3");
        while (true)
        {
            names.AddRange(Names(page));
            if (!page.TryGetProperty("nextLink", out JsonElement next))
            {
                break;
            }

            (_, page) = await app.FollowAsync(next.GetString()!);
        }

        names.ShouldBe([.. Enumerable.Range(0, 7).Select(static i => $"Location {i}")]);

        (int noPlace, JsonElement missing) = await app.GetAsync($"/cars/search?{Period()}");
        (int both, JsonElement ambiguous) = await app.GetAsync($"/cars/search?pickUpLocationId={Guid.NewGuid()}&lat=1&lon=1&{Period()}");
        (int backwards, JsonElement reversed) = await app.GetAsync(
            $"/cars/search?lat=1&lon=1&pickUpAt={SearchApp.Base.AddDays(9):yyyy-MM-dd}T10:00:00Z&dropOffAt={SearchApp.Base.AddDays(8):yyyy-MM-dd}T10:00:00Z");

        noPlace.ShouldBe(400);
        missing.GetProperty("error").GetProperty("details")[0].GetProperty("code").GetString().ShouldBe("LocationRequired");
        both.ShouldBe(400);
        ambiguous.GetProperty("error").GetProperty("details")[0].GetProperty("code").GetString().ShouldBe("AmbiguousLocation");
        backwards.ShouldBe(400);
        reversed.GetProperty("error").GetProperty("details")[0].GetProperty("code").GetString().ShouldBe("DropOffBeforePickUp");
    }
}
