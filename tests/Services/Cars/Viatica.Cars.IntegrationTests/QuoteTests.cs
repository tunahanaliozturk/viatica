using System.Net;
using System.Text.Json.Nodes;
using Viatica.Testing;

namespace Viatica.Cars.IntegrationTests;

public sealed class QuoteTests(PostgresFixture postgres, KafkaFixture kafka, ValkeyFixture valkey) : IAsyncLifetime
{
    private CarsHost host = null!;

    public async ValueTask InitializeAsync() => host = await CarsHost.StartAsync(postgres, kafka, valkey);

    public ValueTask DisposeAsync()
    {
        host.Dispose();
        return ValueTask.CompletedTask;
    }

    private static DateTimeOffset Monday => Scenario.Monday;

    private Task<HttpResponseMessage> QuoteAsync(
        Marketplace m, DateTimeOffset pickUp, DateTimeOffset dropOff, int driverAge = 35, Guid? dropOffLocation = null, Guid? location = null) =>
        host.Anonymous().PostAsync("/cars/quotes", Scenario.Quote(location ?? m.LocationId, m.CategoryId, pickUp, dropOff, driverAge, dropOffLocation));

    private static async Task<decimal> TotalAsync(HttpResponseMessage response) =>
        decimal.Parse((string)(await response.JsonAsync())["total"]!["amount"]!, System.Globalization.CultureInfo.InvariantCulture);

    [Fact]
    public async Task A_quote_prices_each_day_and_pins_the_total_in_a_signed_token()
    {
        Marketplace m = await Scenario.ArrangeAsync(host, rate: 40m);

        HttpResponseMessage response = await QuoteAsync(m, Monday, Monday.AddDays(2));

        await response.AssertStatusAsync(HttpStatusCode.OK);
        JsonNode quote = await response.JsonAsync();
        ((int)quote["rentalDays"]!).ShouldBe(2);
        quote["days"]!.AsArray().Count.ShouldBe(2);
        ((string)quote["days"]![0]!["date"]!).ShouldBe("2026-12-07");
        ((string)quote["days"]![0]!["rate"]!["amount"]!).ShouldBe("40.00");
        ((string)quote["rental"]!["amount"]!).ShouldBe("80.00");
        ((string)quote["oneWayFee"]!["amount"]!).ShouldBe("0.00");
        ((string)quote["youngDriverSurcharge"]!["amount"]!).ShouldBe("0.00");
        ((string)quote["total"]!["amount"]!).ShouldBe("80.00");
        ((string)quote["total"]!["currency"]!).ShouldBe("EUR");
        ((string)quote["quoteToken"]!).ShouldContain(".");
        DateTimeOffset.Parse((string)quote["expiresAt"]!, System.Globalization.CultureInfo.InvariantCulture)
            .ShouldBe(host.Clock.GetUtcNow() + TimeSpan.FromMinutes(15));
    }

    [Fact]
    public async Task The_day_rates_of_a_rental_may_differ_and_the_total_is_their_sum()
    {
        Marketplace m = await Scenario.ArrangeAsync(host, rate: 40m);
        await Scenario.Ok(m.Client.PostAsync(
            $"/cars/locations/{m.LocationId}/categories/{m.CategoryId}/inventory/bulk", Scenario.Inventory("2026-12-08", "2026-12-08", 5, 55m)));

        HttpResponseMessage response = await QuoteAsync(m, Monday, Monday.AddHours(72));

        (await response.JsonAsync())["days"]!.AsArray().Select(static d => (string)d!["rate"]!["amount"]!).ShouldBe(["40.00", "55.00", "40.00"]);
        (await TotalAsync(response)).ShouldBe(135m);
    }

    [Theory]
    [InlineData(24, 0, 1)]
    [InlineData(24, 59, 1)]   // the 59 minute grace
    [InlineData(25, 0, 2)]
    [InlineData(47, 0, 2)]
    [InlineData(49, 0, 3)]
    public async Task The_number_of_rental_days_follows_the_grace_rule_over_http(int hours, int minutes, int days)
    {
        Marketplace m = await Scenario.ArrangeAsync(host, rate: 40m);
        // Out on a Monday at 09:00; the return time is chosen to land inside opening hours or on the grace edge.
        DateTimeOffset pickUp = new(2026, 12, 7, 9, 0, 0, TimeSpan.Zero);
        DateTimeOffset dropOff = pickUp.AddHours(hours).AddMinutes(minutes);

        HttpResponseMessage response = await QuoteAsync(m, pickUp, dropOff);

        // 24 h 59 min after 09:00 is 09:59 the next day: open. 49 h after is 10:00 on Wednesday: open.
        await response.AssertStatusAsync(HttpStatusCode.OK);
        ((int)(await response.JsonAsync())["rentalDays"]!).ShouldBe(days);
    }

    [Fact]
    public async Task A_rental_longer_than_thirty_days_is_refused()
    {
        Marketplace m = await Scenario.ArrangeAsync(host);

        HttpResponseMessage response = await QuoteAsync(m, Monday, Monday.AddDays(31));

        await response.AssertStatusAsync(HttpStatusCode.BadRequest, "RentalTooLong");
    }

    [Fact]
    public async Task A_drop_off_before_the_pick_up_and_a_pick_up_in_the_past_are_refused()
    {
        Marketplace m = await Scenario.ArrangeAsync(host);

        await (await QuoteAsync(m, Monday, Monday.AddHours(-2))).AssertStatusAsync(HttpStatusCode.BadRequest, "DropOffBeforePickUp");
        DateTimeOffset yesterday = host.Clock.GetUtcNow().AddDays(-1);
        await (await QuoteAsync(m, yesterday, yesterday.AddDays(1))).AssertStatusAsync(HttpStatusCode.BadRequest, "PickUpInThePast");
    }

    [Theory]
    [InlineData("2026-12-07T07:59:00+00:00", "2026-12-08T10:00:00+00:00", "pickUpAt")]    // a minute before the desk opens
    [InlineData("2026-12-07T18:01:00+00:00", "2026-12-08T10:00:00+00:00", "pickUpAt")]    // a minute after it closes
    [InlineData("2026-12-13T10:00:00+00:00", "2026-12-14T10:00:00+00:00", "pickUpAt")]    // Sunday: no hours at all
    [InlineData("2026-12-07T10:00:00+00:00", "2026-12-09T18:01:00+00:00", "dropOffAt")]
    [InlineData("2026-12-07T10:00:00+00:00", "2026-12-12T13:30:00+00:00", "dropOffAt")]    // Saturday closes at 13:00
    [InlineData("2026-12-07T10:00:00+00:00", "2026-12-13T10:00:00+00:00", "dropOffAt")]    // back on a Sunday
    public async Task Pick_up_and_drop_off_must_fall_inside_opening_hours(string pickUp, string dropOff, string target)
    {
        Marketplace m = await Scenario.ArrangeAsync(host);

        HttpResponseMessage response = await QuoteAsync(m, DateTimeOffset.Parse(pickUp, System.Globalization.CultureInfo.InvariantCulture), DateTimeOffset.Parse(dropOff, System.Globalization.CultureInfo.InvariantCulture));

        await response.AssertStatusAsync(HttpStatusCode.BadRequest, "LocationClosed");
        ((string)(await response.JsonAsync())["error"]!["target"]!).ShouldBe(target);
    }

    [Theory]
    [InlineData("2026-12-07T08:00:00+00:00", "2026-12-08T08:00:00+00:00")]   // the opening minute, both ends
    [InlineData("2026-12-07T18:00:00+00:00", "2026-12-08T18:00:00+00:00")]   // the closing minute, both ends
    [InlineData("2026-12-11T10:00:00+00:00", "2026-12-12T13:00:00+00:00")]   // back on Saturday at closing time
    public async Task The_opening_and_closing_minutes_are_open(string pickUp, string dropOff)
    {
        Marketplace m = await Scenario.ArrangeAsync(host);

        HttpResponseMessage response = await QuoteAsync(m, DateTimeOffset.Parse(pickUp, System.Globalization.CultureInfo.InvariantCulture), DateTimeOffset.Parse(dropOff, System.Globalization.CultureInfo.InvariantCulture));

        await response.AssertStatusAsync(HttpStatusCode.OK);
    }

    [Fact]
    public async Task Opening_hours_are_the_wall_clock_in_the_offset_the_client_sends()
    {
        Marketplace m = await Scenario.ArrangeAsync(host);
        var localEight = new DateTimeOffset(2026, 12, 7, 8, 0, 0, TimeSpan.FromHours(1));   // 07:00 UTC, 08:00 in Lisbon summer time

        HttpResponseMessage response = await QuoteAsync(m, localEight, localEight.AddHours(24));

        await response.AssertStatusAsync(HttpStatusCode.OK);
        (await QuoteAsync(m, localEight.ToOffset(TimeSpan.Zero), localEight.AddHours(24))).StatusCode.ShouldBe(HttpStatusCode.BadRequest);
    }

    [Fact]
    public async Task A_one_way_rental_to_another_location_of_the_same_partner_adds_the_partners_fee()
    {
        Marketplace m = await Scenario.ArrangeAsync(host, rate: 40m);
        await Scenario.Ok(m.Client.PutAsync("/cars/partner/settings", new { oneWayFee = Scenario.Money(25m), youngDriverSurchargePerDay = Scenario.Money(0m) }));

        HttpResponseMessage oneWay = await QuoteAsync(m, Monday, Monday.AddHours(24), dropOffLocation: m.OtherLocationId);
        HttpResponseMessage sameLocation = await QuoteAsync(m, Monday, Monday.AddHours(24), dropOffLocation: m.LocationId);

        JsonNode quote = await oneWay.JsonAsync();
        ((string)quote["oneWayFee"]!["amount"]!).ShouldBe("25.00");
        ((string)quote["total"]!["amount"]!).ShouldBe("65.00");
        (await TotalAsync(sameLocation)).ShouldBe(40m);
    }

    [Fact]
    public async Task A_one_way_rental_checks_the_opening_hours_of_the_drop_off_location()
    {
        Marketplace m = await Scenario.ArrangeAsync(host);
        // Porto is the second location of the arrangement, with the same hours; close it on Tuesdays.
        object[] noTuesday = [.. Scenario.BusinessHours.Where(static h => !h.ToString()!.Contains("Tuesday", StringComparison.Ordinal))];
        await Scenario.Ok(m.Client.PutAsync($"/cars/locations/{m.OtherLocationId}", Scenario.Location("Porto Airport", noTuesday, "OPO")));

        HttpResponseMessage response = await QuoteAsync(m, Monday, Monday.AddHours(24), dropOffLocation: m.OtherLocationId);

        await response.AssertStatusAsync(HttpStatusCode.BadRequest, "LocationClosed");
        ((string)(await response.JsonAsync())["error"]!["target"]!).ShouldBe("dropOffAt");
    }

    [Fact]
    public async Task One_way_to_a_location_of_another_partner_is_not_allowed()
    {
        Marketplace m = await Scenario.ArrangeAsync(host);
        Marketplace competitor = await Scenario.ArrangeAsync(host);

        HttpResponseMessage response = await QuoteAsync(m, Monday, Monday.AddHours(24), dropOffLocation: competitor.LocationId);

        await response.AssertStatusAsync(HttpStatusCode.BadRequest, "OneWayNotAllowed");
    }

    [Fact]
    public async Task One_way_to_an_unpublished_location_is_a_missing_location()
    {
        Marketplace m = await Scenario.ArrangeAsync(host);
        await Scenario.Ok(m.Client.PostAsync($"/cars/locations/{m.OtherLocationId}/unpublish"));

        HttpResponseMessage response = await QuoteAsync(m, Monday, Monday.AddHours(24), dropOffLocation: m.OtherLocationId);

        await response.AssertStatusAsync(HttpStatusCode.NotFound, "LocationNotFound");
    }

    [Theory]
    [InlineData(20, "DriverTooYoung")]
    [InlineData(18, "DriverTooYoung")]
    public async Task A_driver_under_the_category_minimum_is_too_young(int age, string code)
    {
        Marketplace m = await Scenario.ArrangeAsync(host, minimumDriverAge: 21);

        HttpResponseMessage response = await QuoteAsync(m, Monday, Monday.AddHours(24), age);

        await response.AssertStatusAsync(HttpStatusCode.BadRequest, code);
        ((string)(await response.JsonAsync())["error"]!["target"]!).ShouldBe("driverAge");
    }

    [Theory]
    [InlineData(21, "36.00")]   // at the minimum: surcharge for each of the three days
    [InlineData(24, "36.00")]
    [InlineData(25, "0.00")]
    [InlineData(70, "0.00")]
    public async Task Drivers_between_the_minimum_age_and_25_pay_the_partners_daily_surcharge(int age, string surcharge)
    {
        Marketplace m = await Scenario.ArrangeAsync(host, rate: 40m, minimumDriverAge: 21);
        await Scenario.Ok(m.Client.PutAsync("/cars/partner/settings", new { oneWayFee = Scenario.Money(0m), youngDriverSurchargePerDay = Scenario.Money(12m) }));

        HttpResponseMessage response = await QuoteAsync(m, Monday, Monday.AddHours(72), age);

        JsonNode quote = await response.JsonAsync();
        ((string)quote["youngDriverSurcharge"]!["amount"]!).ShouldBe(surcharge);
        decimal expected = 120m + decimal.Parse(surcharge, System.Globalization.CultureInfo.InvariantCulture);
        decimal.Parse((string)quote["total"]!["amount"]!, System.Globalization.CultureInfo.InvariantCulture).ShouldBe(expected);
    }

    [Fact]
    public async Task A_day_with_no_car_left_makes_the_whole_rental_sold_out()
    {
        Marketplace m = await Scenario.ArrangeAsync(host, allotment: 5);
        await Scenario.Ok(m.Client.PostAsync(
            $"/cars/locations/{m.LocationId}/categories/{m.CategoryId}/inventory/bulk", Scenario.Inventory("2026-12-08", "2026-12-08", 0, 40m)));

        await (await QuoteAsync(m, Monday, Monday.AddHours(72))).AssertStatusAsync(HttpStatusCode.Conflict, "SoldOut");
        (await QuoteAsync(m, Monday, Monday.AddHours(24))).StatusCode.ShouldBe(HttpStatusCode.OK);
    }

    [Fact]
    public async Task Days_without_any_inventory_row_are_sold_out()
    {
        Marketplace m = await Scenario.ArrangeAsync(host);   // inventory covers December only
        var january = new DateTimeOffset(2027, 1, 4, 10, 0, 0, TimeSpan.Zero);

        await (await QuoteAsync(m, january, january.AddHours(24))).AssertStatusAsync(HttpStatusCode.Conflict, "SoldOut");
    }

    [Fact]
    public async Task Cars_held_by_other_checkouts_count_against_the_quote()
    {
        Marketplace m = await Scenario.ArrangeAsync(host, allotment: 1);
        string token = await Scenario.TokenAsync(host.Anonymous(), m, Monday, Monday.AddHours(24));
        (await host.PlaceAsync(token)).Describe().ShouldBe("placed");

        await (await QuoteAsync(m, Monday, Monday.AddHours(24))).AssertStatusAsync(HttpStatusCode.Conflict, "SoldOut");
    }

    [Fact]
    public async Task Listings_that_are_not_published_cannot_be_quoted()
    {
        Marketplace m = await Scenario.ArrangeAsync(host);
        await Scenario.Ok(m.Client.PostAsync($"/cars/categories/{m.CategoryId}/unpublish"));
        await (await QuoteAsync(m, Monday, Monday.AddHours(24))).AssertStatusAsync(HttpStatusCode.NotFound, "CategoryNotFound");

        await Scenario.Ok(m.Client.PostAsync($"/cars/categories/{m.CategoryId}/publish"));
        await Scenario.Ok(m.Client.PostAsync($"/cars/locations/{m.LocationId}/unpublish"));
        await (await QuoteAsync(m, Monday, Monday.AddHours(24))).AssertStatusAsync(HttpStatusCode.NotFound, "LocationNotFound");

        await (await QuoteAsync(m, Monday, Monday.AddHours(24), location: Guid.NewGuid())).AssertStatusAsync(HttpStatusCode.NotFound, "LocationNotFound");
    }

    [Fact]
    public async Task A_category_of_another_partner_is_not_found_at_this_location()
    {
        Marketplace m = await Scenario.ArrangeAsync(host);
        Marketplace other = await Scenario.ArrangeAsync(host);

        HttpResponseMessage response = await host.Anonymous().PostAsync("/cars/quotes", Scenario.Quote(m.LocationId, other.CategoryId, Monday, Monday.AddHours(24)));

        await response.AssertStatusAsync(HttpStatusCode.NotFound, "CategoryNotFound");
    }

    [Fact]
    public async Task A_quote_needs_no_sign_in_but_a_bad_body_is_a_400()
    {
        Marketplace m = await Scenario.ArrangeAsync(host);

        (await QuoteAsync(m, Monday, Monday.AddHours(24))).StatusCode.ShouldBe(HttpStatusCode.OK);
        (await host.Anonymous().PostAsync("/cars/quotes", Scenario.Quote(m.LocationId, m.CategoryId, Monday, Monday.AddHours(24), driverAge: 16)))
            .StatusCode.ShouldBe(HttpStatusCode.BadRequest);
        (await host.Anonymous().PostAsync("/cars/quotes", new { locationId = m.LocationId })).StatusCode.ShouldBe(HttpStatusCode.BadRequest);
    }
}
