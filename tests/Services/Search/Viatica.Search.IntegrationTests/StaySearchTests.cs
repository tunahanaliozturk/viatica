using System.Globalization;
using System.Text.Json;
using Viatica.Contracts;

namespace Viatica.Search.IntegrationTests;

/// <summary>
/// What a stay search answers, against a real OpenSearch. Properties go straight into the projection (the Kafka
/// path has its own tests), so each test says exactly what is for sale and checks exactly what comes back.
/// </summary>
public sealed class StaySearchTests(SearchApp app) : IClassFixture<SearchApp>, IAsyncLifetime
{
    private static string Date(int offset) => SearchApp.Base.AddDays(offset).ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);

    private static string Stay(int from, int nights) => $"checkIn={Date(from)}&checkOut={Date(from + nights)}";

    private static string[] Names(JsonElement body) => [.. body.GetProperty("value").EnumerateArray().Select(static i => i.GetProperty("name").GetString()!)];

    public async ValueTask InitializeAsync()
    {
        await app.ResetAsync();
    }

    public ValueTask DisposeAsync() => ValueTask.CompletedTask;

    [Fact]
    public async Task A_property_with_a_room_free_on_every_night_is_found_with_the_cheapest_room_and_the_total()
    {
        var cheap = new RoomSpec("Standard", 2, 5, 90m);
        var dear = new RoomSpec("Suite", 4, 2, 250m);
        var id = Guid.NewGuid();
        await app.SeedAsync(Seed.Property(id, "Hotel Lisboa", "Lisbon", 4, 38.72, -9.14, ["wifi", "pool"], dear, cheap));

        (int status, JsonElement body) = await app.GetAsync($"/stays/search?destination=lisbon&{Stay(5, 3)}&guests=2");

        status.ShouldBe(200);
        JsonElement hit = body.GetProperty("value").EnumerateArray().Single();
        hit.GetProperty("propertyId").GetGuid().ShouldBe(id);
        hit.GetProperty("name").GetString().ShouldBe("Hotel Lisboa");
        hit.GetProperty("starRating").GetInt32().ShouldBe(4);
        hit.GetProperty("room").GetProperty("name").GetString().ShouldBe("Standard");
        hit.GetProperty("room").GetProperty("roomTypeId").GetGuid().ShouldBe(cheap.Id);
        hit.GetProperty("nights").GetInt32().ShouldBe(3);
        hit.GetProperty("pricePerNight").GetProperty("amount").GetString().ShouldBe("90.00");
        hit.GetProperty("pricePerNight").GetProperty("currency").GetString().ShouldBe("EUR");
        hit.GetProperty("total").GetProperty("amount").GetString().ShouldBe("270.00");
        body.TryGetProperty("nextLink", out _).ShouldBeFalse();
    }

    [Fact]
    public async Task One_sold_out_night_in_the_middle_of_the_stay_excludes_the_property()
    {
        var room = new RoomSpec("Standard", 2, 3, 100m);
        var open = Guid.NewGuid();
        var broken = Guid.NewGuid();
        await app.SeedAsync(Seed.Property(open, "Open House", "Lisbon", 3, 38.7, -9.1, [], room));
        var other = new RoomSpec("Standard", 2, 3, 100m);
        await app.SeedAsync(
            [
                .. Seed.Property(broken, "Sold Out Night", "Lisbon", 3, 38.7, -9.1, [], other),
                new StayAvailabilityChanged(broken, other.Id, 2, [new DateAvailability(SearchApp.Base.AddDays(6), 0, new Price(100m, "EUR"))]),
            ]);

        (_, JsonElement body) = await app.GetAsync($"/stays/search?destination=lisbon&{Stay(5, 3)}");

        Names(body).ShouldBe(["Open House"]);

        // The same property is found when the stay avoids the night that sold out.
        (_, JsonElement before) = await app.GetAsync($"/stays/search?destination=lisbon&{Stay(3, 3)}");
        Names(before).ShouldBe(["Open House", "Sold Out Night"], ignoreOrder: true);
    }

    [Fact]
    public async Task A_night_nobody_has_reported_is_not_for_sale()
    {
        var room = new RoomSpec("Standard", 2, 3, 100m);
        var id = Guid.NewGuid();
        await app.SeedAsync(
            [
                Seed.Listing(id, 1, "Gap Hotel", "Lisbon", 3, 38.7, -9.1, [], room),
                Seed.Nights(id, room, 1, from: 0, count: 6),
                Seed.Nights(id, room, 1, from: 7, count: 10),
            ]);

        (_, JsonElement across) = await app.GetAsync($"/stays/search?destination=lisbon&{Stay(4, 4)}");
        (_, JsonElement before) = await app.GetAsync($"/stays/search?destination=lisbon&{Stay(2, 4)}");
        (_, JsonElement after) = await app.GetAsync($"/stays/search?destination=lisbon&{Stay(7, 4)}");

        Names(across).ShouldBeEmpty();
        Names(before).ShouldBe(["Gap Hotel"]);
        Names(after).ShouldBe(["Gap Hotel"]);
    }

    [Fact]
    public async Task The_rooms_wanted_decide_which_room_types_have_enough_units_on_every_night()
    {
        var plenty = new RoomSpec("Family", 4, 3, 150m);
        var scarce = new RoomSpec("Tiny", 2, 1, 60m);
        var id = Guid.NewGuid();
        await app.SeedAsync(Seed.Property(id, "Mixed", "Lisbon", 3, 38.7, -9.1, [], plenty, scarce));

        (_, JsonElement one) = await app.GetAsync($"/stays/search?destination=lisbon&{Stay(2, 2)}&guests=2&rooms=1");
        (_, JsonElement two) = await app.GetAsync($"/stays/search?destination=lisbon&{Stay(2, 2)}&guests=4&rooms=2");
        (_, JsonElement four) = await app.GetAsync($"/stays/search?destination=lisbon&{Stay(2, 2)}&guests=4&rooms=4");

        JsonElement oneHit = one.GetProperty("value")[0];
        oneHit.GetProperty("room").GetProperty("name").GetString().ShouldBe("Tiny");
        oneHit.GetProperty("total").GetProperty("amount").GetString().ShouldBe("120.00");

        // Two rooms: only Family has two units a night, and two rooms cost two nights twice.
        JsonElement twoHit = two.GetProperty("value")[0];
        twoHit.GetProperty("room").GetProperty("name").GetString().ShouldBe("Family");
        twoHit.GetProperty("total").GetProperty("amount").GetString().ShouldBe("600.00");
        twoHit.GetProperty("pricePerNight").GetProperty("amount").GetString().ShouldBe("150.00");

        Names(four).ShouldBeEmpty();
    }

    [Fact]
    public async Task The_guests_have_to_fit_in_the_rooms()
    {
        var small = new RoomSpec("Double", 2, 5, 80m);
        var id = Guid.NewGuid();
        await app.SeedAsync(Seed.Property(id, "Small Rooms", "Lisbon", 3, 38.7, -9.1, [], small));

        (_, JsonElement fits) = await app.GetAsync($"/stays/search?destination=lisbon&{Stay(1, 2)}&guests=4&rooms=2");
        (_, JsonElement tooMany) = await app.GetAsync($"/stays/search?destination=lisbon&{Stay(1, 2)}&guests=5&rooms=2");

        Names(fits).ShouldBe(["Small Rooms"]);
        Names(tooMany).ShouldBeEmpty();
    }

    [Fact]
    public async Task A_thirty_night_stay_works_and_thirty_one_is_refused()
    {
        var room = new RoomSpec("Standard", 2, 2, 50m);
        var id = Guid.NewGuid();
        await app.SeedAsync(Seed.Property(id, "Long Stay", "Lisbon", 3, 38.7, -9.1, [], room));

        (int ok, JsonElement thirty) = await app.GetAsync($"/stays/search?destination=lisbon&{Stay(1, 30)}");
        (int refused, JsonElement thirtyOne) = await app.GetAsync($"/stays/search?destination=lisbon&{Stay(1, 31)}");

        ok.ShouldBe(200);
        thirty.GetProperty("value")[0].GetProperty("total").GetProperty("amount").GetString().ShouldBe("1500.00");
        refused.ShouldBe(400);
        thirtyOne.GetProperty("error").GetProperty("details")[0].GetProperty("code").GetString().ShouldBe("StayTooLong");
    }

    [Fact]
    public async Task A_property_that_is_unpublished_or_whose_room_is_no_longer_listed_is_not_offered()
    {
        var room = new RoomSpec("Standard", 2, 2, 50m);
        var gone = Guid.NewGuid();
        var kept = Guid.NewGuid();
        await app.SeedAsync(Seed.Property(gone, "Closed", "Lisbon", 3, 38.7, -9.1, [], room));
        var other = new RoomSpec("Standard", 2, 2, 55m);
        await app.SeedAsync(Seed.Property(kept, "Open", "Lisbon", 3, 38.7, -9.1, [], other));
        await app.SeedAsync(new StayUnpublished(gone, 2));

        (_, JsonElement body) = await app.GetAsync($"/stays/search?destination=lisbon&{Stay(1, 2)}");
        Names(body).ShouldBe(["Open"]);

        // The listing now names no room type: nothing to sell, though the nights are still stored.
        await app.SeedAsync(Seed.Listing(kept, 2, "Open", "Lisbon", 3, 38.7, -9.1, []));
        (_, JsonElement after) = await app.GetAsync($"/stays/search?destination=lisbon&{Stay(1, 2)}");
        Names(after).ShouldBeEmpty();
    }

    [Fact]
    public async Task Price_stars_and_amenities_filter_the_result()
    {
        await SeedFourAsync();

        (_, JsonElement priced) = await app.GetAsync($"/stays/search?destination=lisbon&{Stay(1, 2)}&minPrice=100&maxPrice=200&sort=price");
        (_, JsonElement stars) = await app.GetAsync($"/stays/search?destination=lisbon&{Stay(1, 2)}&stars=4,5&sort=price");
        (_, JsonElement amenities) = await app.GetAsync($"/stays/search?destination=lisbon&{Stay(1, 2)}&amenities=pool,spa&sort=price");
        (_, JsonElement combined) = await app.GetAsync($"/stays/search?destination=lisbon&{Stay(1, 2)}&amenities=pool&stars=3,4&maxPrice=150&sort=price");

        Names(priced).ShouldBe(["Boutique", "Grand"]);
        Names(stars).ShouldBe(["Grand", "Palace"]);
        Names(amenities).ShouldBe(["Grand", "Palace"]);
        Names(combined).ShouldBe(["Boutique"]);
    }

    [Fact]
    public async Task Results_sort_by_price_by_rating_and_by_distance()
    {
        await SeedFourAsync();

        (_, JsonElement price) = await app.GetAsync($"/stays/search?destination=lisbon&{Stay(1, 2)}&sort=price");
        (_, JsonElement rating) = await app.GetAsync($"/stays/search?destination=lisbon&{Stay(1, 2)}&sort=rating");
        (_, JsonElement distance) = await app.GetAsync($"/stays/search?destination=lisbon&{Stay(1, 2)}&sort=distance&lat=38.80&lon=-9.20");

        Names(price).ShouldBe(["Hostel", "Boutique", "Grand", "Palace"]);
        Names(rating).ShouldBe(["Palace", "Grand", "Boutique", "Hostel"]);

        // Palace sits at the point asked for, Hostel is the farthest.
        Names(distance).ShouldBe(["Palace", "Grand", "Boutique", "Hostel"]);
        distance.GetProperty("value")[0].GetProperty("distanceKm").GetDouble().ShouldBeLessThan(1);
        distance.GetProperty("value")[3].GetProperty("distanceKm").GetDouble().ShouldBeGreaterThan(5);
    }

    [Fact]
    public async Task Sorting_by_distance_without_a_point_is_a_400_and_a_box_supplies_its_centre()
    {
        await SeedFourAsync();

        (int status, JsonElement error) = await app.GetAsync($"/stays/search?destination=lisbon&{Stay(1, 2)}&sort=distance");
        (int boxStatus, JsonElement box) = await app.GetAsync($"/stays/search?bbox=-9.5,38.5,-8.9,39.1&{Stay(1, 2)}&sort=distance");

        status.ShouldBe(400);
        error.GetProperty("error").GetProperty("details")[0].GetProperty("code").GetString().ShouldBe("DistanceNeedsPoint");
        boxStatus.ShouldBe(200);
        box.GetProperty("value").GetArrayLength().ShouldBe(4);
    }

    [Fact]
    public async Task A_destination_matches_name_city_and_region_ignoring_case_and_accents_and_small_typos()
    {
        var room = new RoomSpec("Standard", 2, 2, 80m);
        await app.SeedAsync(Seed.Property(Guid.NewGuid(), "Zürich Central", "Zürich", 4, 47.37, 8.54, [], room));
        await app.SeedAsync(Seed.Property(Guid.NewGuid(), "Hotel Lisboa", "Lisbon", 4, 38.72, -9.14, [], new RoomSpec("Standard", 2, 2, 80m)));

        (_, JsonElement accents) = await app.GetAsync($"/stays/search?destination=ZURICH&{Stay(1, 2)}");
        (_, JsonElement typo) = await app.GetAsync($"/stays/search?destination=lisbonn&{Stay(1, 2)}");
        (_, JsonElement byName) = await app.GetAsync($"/stays/search?destination=central&{Stay(1, 2)}");
        (_, JsonElement none) = await app.GetAsync($"/stays/search?destination=tokyo&{Stay(1, 2)}");

        Names(accents).ShouldBe(["Zürich Central"]);
        Names(typo).ShouldBe(["Hotel Lisboa"]);
        Names(byName).ShouldBe(["Zürich Central"]);
        Names(none).ShouldBeEmpty();
    }

    [Fact]
    public async Task A_bounding_box_selects_by_location_including_one_that_crosses_the_antimeridian()
    {
        await app.SeedAsync(Seed.Property(Guid.NewGuid(), "Lisbon Inn", "Lisbon", 3, 38.72, -9.14, [], new RoomSpec("Standard", 2, 2, 80m)));
        await app.SeedAsync(Seed.Property(Guid.NewGuid(), "Porto Inn", "Porto", 3, 41.15, -8.61, [], new RoomSpec("Standard", 2, 2, 70m)));
        await app.SeedAsync(Seed.Property(Guid.NewGuid(), "Fiji Inn", "Suva", 3, -18.14, 178.44, [], new RoomSpec("Standard", 2, 2, 60m)));
        await app.SeedAsync(Seed.Property(Guid.NewGuid(), "Samoa Inn", "Apia", 3, -13.83, -171.76, [], new RoomSpec("Standard", 2, 2, 65m)));

        (_, JsonElement south) = await app.GetAsync($"/stays/search?bbox=-10,38,-8,39.5&{Stay(1, 2)}");
        (_, JsonElement both) = await app.GetAsync($"/stays/search?bbox=-10,38,-8,42&{Stay(1, 2)}&sort=price");
        (_, JsonElement pacific) = await app.GetAsync($"/stays/search?bbox=170,-20,-170,-10&{Stay(1, 2)}&sort=price");

        Names(south).ShouldBe(["Lisbon Inn"]);
        Names(both).ShouldBe(["Porto Inn", "Lisbon Inn"]);
        Names(pacific).ShouldBe(["Fiji Inn", "Samoa Inn"]);
    }

    [Fact]
    public async Task Facets_count_the_properties_that_matched_availability_and_filters_and_nothing_else()
    {
        await SeedFourAsync();

        // A fifth property that would add to every count, if sold out nights did not exclude it.
        var soldOut = new RoomSpec("Standard", 2, 1, 100m);
        var id = Guid.NewGuid();
        await app.SeedAsync(
            [
                .. Seed.Property(id, "Full House", "Lisbon", 5, 38.8, -9.2, ["pool", "spa", "wifi"], soldOut),
                new StayAvailabilityChanged(id, soldOut.Id, 2, [new DateAvailability(SearchApp.Base.AddDays(2), 0, new Price(100m, "EUR"))]),
            ]);

        (_, JsonElement all) = await app.GetAsync($"/stays/search?destination=lisbon&{Stay(1, 3)}");
        (_, JsonElement filtered) = await app.GetAsync($"/stays/search?destination=lisbon&{Stay(1, 3)}&amenities=pool");

        all.GetProperty("totalCount").GetInt64().ShouldBe(4);
        Counts(all, "stars").ShouldBe(["1=1", "3=1", "4=1", "5=1"]);
        Counts(all, "amenities").ShouldBe(["gym=1", "pool=3", "spa=2", "wifi=3"]);

        filtered.GetProperty("totalCount").GetInt64().ShouldBe(3);
        Counts(filtered, "stars").ShouldBe(["3=1", "4=1", "5=1"]);
        Counts(filtered, "amenities").ShouldBe(["gym=1", "pool=3", "spa=2", "wifi=2"]);
    }

    [Fact]
    public async Task Unknown_parameters_and_malformed_values_are_refused_with_every_problem_listed()
    {
        (int status, JsonElement body) = await app.GetAsync("/stays/search?destination=lisbon&checkIn=tomorrow&colour=blue&guests=two&sort=cheapest");

        status.ShouldBe(400);
        string[] targets = [.. body.GetProperty("error").GetProperty("details").EnumerateArray().Select(static d => d.GetProperty("target").GetString()!)];
        targets.ShouldBe(["colour", "checkIn", "guests", "sort", "checkOut"], ignoreOrder: true);
        body.GetProperty("error").GetProperty("code").GetString().ShouldBe("InvalidRequest");
    }

    // Sorted "value=count" pairs, so a test compares what the facet says and not the order the buckets came in.
    private static string[] Counts(JsonElement body, string facet) =>
        [.. body.GetProperty("facets").GetProperty(facet).EnumerateArray()
            .Select(static f => $"{(f.GetProperty("value").ValueKind == JsonValueKind.String ? f.GetProperty("value").GetString() : f.GetProperty("value").GetInt32().ToString(CultureInfo.InvariantCulture))}={f.GetProperty("count").GetInt64()}")
            .Order(StringComparer.Ordinal)];

    /// <summary>
    /// Four properties near Lisbon. Prices per night: Hostel 40, Boutique 120, Grand 180, Palace 400. Palace is at
    /// 38.80 N 9.20 W and the others are further from it in that order.
    /// </summary>
    private async Task SeedFourAsync()
    {
        await app.SeedAsync(Seed.Property(Guid.NewGuid(), "Hostel", "Lisbon", 1, 38.72, -9.14, ["wifi"], new RoomSpec("Dorm", 2, 4, 40m)));
        await app.SeedAsync(Seed.Property(Guid.NewGuid(), "Boutique", "Lisbon", 3, 38.74, -9.15, ["wifi", "pool"], new RoomSpec("Double", 2, 4, 120m)));
        await app.SeedAsync(Seed.Property(Guid.NewGuid(), "Grand", "Lisbon", 4, 38.78, -9.18, ["wifi", "pool", "spa", "gym"], new RoomSpec("Deluxe", 3, 4, 180m)));
        await app.SeedAsync(Seed.Property(Guid.NewGuid(), "Palace", "Lisbon", 5, 38.80, -9.20, ["pool", "spa"], new RoomSpec("Suite", 4, 4, 400m)));
    }
}
