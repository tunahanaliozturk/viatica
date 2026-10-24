using System.Globalization;
using System.Net;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Viatica.Stays.Application.Features.Nights;
using Viatica.Stays.Application.Features.Quotes;
using Viatica.Stays.Domain;
using Viatica.Testing;

namespace Viatica.Stays.IntegrationTests;

/// <summary>The partner's calendar (STY-3) and the traveller's quote (STY-5).</summary>
public sealed class RatesAndQuotesTests(StaysFixture stays) : IClassFixture<StaysFixture>
{
    private StaysHost Host => stays.Host;

    private static DateOnly Today => DateOnly.FromDateTime(StaysHost.Start.UtcDateTime);

    [Fact]
    public async Task A_calendar_edit_a_year_ahead_is_one_request_one_transaction_and_one_event()
    {
        using EventProbe probe = stays.Probe();
        var scenario = await Scenario.CreateAsync(Host, publish: false);
        DateOnly last = Today.AddDays(StayDates.RateWindowDays);

        using HttpResponseMessage response = await scenario.SetNightsAsync(Today, last, allotment: 4, rate: 99m);

        response.StatusCode.ShouldBe(HttpStatusCode.OK);
        NightsSet result = await StaysHost.ReadAsync<NightsSet>(response);
        result.Nights.ShouldBe(366);

        List<Night> nights = await scenario.NightsAsync();
        nights.Count(n => n.Date >= Today).ShouldBe(366);
        nights.Where(n => n.Date >= Today).ShouldAllBe(n => n.Allotment == 4 && n.Rate == 99m && n.Sold == 0 && n.Held == 0);

        // One request is one announcement, with every night in it and the next version.
        IReadOnlyList<ProbedMessage> events = await scenario.EventsAsync(probe, "StayAvailabilityChanged", 2);
        Scenario.Long(events[1], "availabilityVersion").ShouldBe(result.AvailabilityVersion);
        Scenario.Nights(events[1]).GetArrayLength().ShouldBe(366);
        Scenario.Nights(events[1])[0].GetProperty("rate").GetProperty("amount").GetString().ShouldBe("99.00");
        Scenario.Nights(events[1])[0].GetProperty("available").GetInt32().ShouldBe(4);
    }

    [Fact]
    public async Task The_rate_defaults_to_the_room_types_base_rate()
    {
        var scenario = await Scenario.CreateAsync(Host, rate: 120m);

        (await scenario.SetNightsAsync(Today.AddDays(40), Today.AddDays(42), allotment: 3)).StatusCode.ShouldBe(HttpStatusCode.OK);

        (await scenario.NightsAsync()).Where(n => n.Date >= Today.AddDays(40)).ShouldAllBe(n => n.Rate == 120m);
    }

    [Fact]
    public async Task The_calendar_can_be_read_back_by_its_partner()
    {
        var scenario = await Scenario.CreateAsync(Host, allotment: 6, rate: 130m);

        using HttpResponseMessage response = await Host.GetAsync(
            $"{scenario.RoomPath}/inventory?from={StaysHost.CheckIn:yyyy-MM-dd}&to={StaysHost.CheckOut:yyyy-MM-dd}", scenario.PartnerToken);

        response.StatusCode.ShouldBe(HttpStatusCode.OK);
        JsonElement nights = await StaysHost.JsonAsync(response);
        nights.GetArrayLength().ShouldBe(4);
        nights[0].GetProperty("available").GetInt32().ShouldBe(6);
        nights[0].GetProperty("rate").GetProperty("amount").GetString().ShouldBe("130.00");
    }

    [Theory]
    [InlineData(-3, 5)]
    [InlineData(0, 366)]
    public async Task A_range_outside_the_window_is_refused_and_nothing_is_written(int fromOffset, int toOffset)
    {
        var scenario = await Scenario.CreateAsync(Host, publish: false);
        List<Night> before = await scenario.NightsAsync();

        using HttpResponseMessage response = await scenario.SetNightsAsync(Today.AddDays(fromOffset), Today.AddDays(toOffset), allotment: 1, rate: 50m);

        response.StatusCode.ShouldBe(HttpStatusCode.BadRequest);
        JsonElement details = (await StaysHost.JsonAsync(response)).GetProperty("error").GetProperty("details");
        details[0].GetProperty("code").GetString().ShouldBe(StayErrorCode.OutsideRateWindow);
        (await scenario.NightsAsync()).ShouldBe(before);
    }

    [Fact]
    public async Task A_reversed_range_and_a_bad_rate_are_reported_together()
    {
        var scenario = await Scenario.CreateAsync(Host, publish: false);

        using HttpResponseMessage response = await Host.SendAsync(
            HttpMethod.Post, $"{scenario.RoomPath}/inventory:set",
            new { from = Today.AddDays(9), to = Today.AddDays(8), allotment = -1, rate = new { amount = "10.00", currency = "USD" } },
            scenario.PartnerToken);

        response.StatusCode.ShouldBe(HttpStatusCode.BadRequest);
        JsonElement details = (await StaysHost.JsonAsync(response)).GetProperty("error").GetProperty("details");
        details.EnumerateArray().Select(static d => d.GetProperty("target").GetString())
            .ShouldBe(["to", "allotment", "rate"], ignoreOrder: true);
    }

    [Fact]
    public async Task An_edit_that_cuts_one_night_below_what_is_sold_fails_whole()
    {
        var scenario = await Scenario.CreateAsync(Host, allotment: 5, rate: 100m);
        await Host.InDbAsync(db => db.Database.ExecuteSqlAsync(
            $"update stays.room_nights set held = 3 where room_type_id = {scenario.RoomTypeId} and night = {StaysHost.CheckIn.AddDays(1)}"));
        List<Night> before = await scenario.NightsAsync();
        long versionBefore = await scenario.AvailabilityVersionAsync();

        using HttpResponseMessage response = await scenario.SetNightsAsync(StaysHost.CheckIn, StaysHost.CheckOut, allotment: 2, rate: 777m);

        response.StatusCode.ShouldBe(HttpStatusCode.Conflict);
        (await StaysHost.ErrorCodeAsync(response)).ShouldBe(StayErrorCode.AllotmentBelowBooked);
        (await scenario.NightsAsync()).ShouldBe(before);
        (await scenario.AvailabilityVersionAsync()).ShouldBe(versionBefore);

        // Equal to what is held is fine: the rooms are still there for the traveller who holds them.
        (await scenario.SetNightsAsync(StaysHost.CheckIn, StaysHost.CheckOut, allotment: 3, rate: 777m)).StatusCode.ShouldBe(HttpStatusCode.OK);
        (await scenario.NightsAsync()).Where(n => n.Date >= StaysHost.CheckIn && n.Date <= StaysHost.CheckOut).ShouldAllBe(n => n.Rate == 777m);
    }

    [Fact]
    public async Task Availability_versions_rise_by_one_per_change_and_each_is_announced()
    {
        using EventProbe probe = stays.Probe();
        var scenario = await Scenario.CreateAsync(Host, publish: false);

        for (int i = 0; i < 4; i++)
        {
            (await scenario.SetNightsAsync(StaysHost.CheckIn, StaysHost.CheckOut, allotment: 5 + i)).StatusCode.ShouldBe(HttpStatusCode.OK);
        }

        IReadOnlyList<ProbedMessage> events = await scenario.EventsAsync(probe, "StayAvailabilityChanged", 5);
        long[] versions = [.. events.Select(static m => Scenario.Long(m, "availabilityVersion"))];
        versions.ShouldBe([1, 2, 3, 4, 5]);
        events.Select(static m => m.Data.GetProperty("roomTypeId").GetGuid()).Distinct().ShouldHaveSingleItem().ShouldBe(scenario.RoomTypeId);
    }

    [Fact]
    public async Task A_repeated_calendar_edit_runs_once_and_answers_the_same()
    {
        using EventProbe probe = stays.Probe();
        var scenario = await Scenario.CreateAsync(Host, publish: false);
        Guid requestId = Guid.NewGuid();
        Dictionary<string, string> headers = StaysHost.RepeatabilityHeaders(requestId, Host.Clock.GetUtcNow());

        using HttpResponseMessage first = await scenario.SetNightsAsync(Today.AddDays(60), Today.AddDays(62), 8, 140m, headers);
        using HttpResponseMessage again = await scenario.SetNightsAsync(Today.AddDays(60), Today.AddDays(62), 8, 140m, headers);

        first.StatusCode.ShouldBe(HttpStatusCode.OK);
        again.StatusCode.ShouldBe(HttpStatusCode.OK);
        again.Headers.GetValues("Repeatability-Result").ShouldContain("accepted");
        (await again.Content.ReadAsStringAsync()).ShouldBe(await first.Content.ReadAsStringAsync());

        // The same id for a different range is a client bug and is refused, not answered with the first range's result.
        using HttpResponseMessage different = await scenario.SetNightsAsync(Today.AddDays(60), Today.AddDays(70), 8, 140m, headers);
        different.StatusCode.ShouldBe(HttpStatusCode.BadRequest);

        // Setup made version 1; the one real edit made version 2; the repeats made nothing.
        IReadOnlyList<ProbedMessage> events = await scenario.EventsAsync(probe, "StayAvailabilityChanged", 2);
        events.Count.ShouldBe(2);
        (await scenario.AvailabilityVersionAsync()).ShouldBe(2);
    }

    [Fact]
    public async Task A_quote_gives_the_nightly_breakdown_the_total_and_a_token_that_pins_them()
    {
        var scenario = await Scenario.CreateAsync(Host, allotment: 5, rate: 120m);
        await scenario.SetNightsAsync(StaysHost.CheckIn.AddDays(1), StaysHost.CheckIn.AddDays(1), allotment: 5, rate: 150m);

        Quote quote = await scenario.QuoteAsync(quantity: 2, guests: 4);

        quote.Nights.Select(static n => n.Date).ShouldBe([StaysHost.CheckIn, StaysHost.CheckIn.AddDays(1), StaysHost.CheckIn.AddDays(2)]);
        quote.Nights.Select(static n => n.Rate.Amount).ShouldBe([120m, 150m, 120m]);
        quote.Total.Amount.ShouldBe(780m, "(120 + 150 + 120) x 2 rooms");
        quote.Total.Currency.ShouldBe("EUR");
        quote.ExpiresAt.ShouldBe(Host.Clock.GetUtcNow() + TimeSpan.FromMinutes(15));

        Host.Tokens.TryRead(quote.QuoteToken, Host.Clock.GetUtcNow(), out QuotePayload? pinned, out _).ShouldBeTrue();
        pinned!.PropertyId.ShouldBe(scenario.PropertyId);
        pinned.RoomTypeId.ShouldBe(scenario.RoomTypeId);
        pinned.CheckIn.ShouldBe(StaysHost.CheckIn);
        pinned.CheckOut.ShouldBe(StaysHost.CheckOut);
        pinned.Quantity.ShouldBe(2);
        pinned.Total.ShouldBe(quote.Total);
    }

    [Fact]
    public async Task A_quote_needs_no_sign_in()
    {
        var scenario = await Scenario.CreateAsync(Host);

        using HttpResponseMessage response = await Host.SendAsync(
            HttpMethod.Post, $"{scenario.RoomPath}/quotes", new { checkIn = StaysHost.CheckIn, checkOut = StaysHost.CheckOut, quantity = 1, guests = 2 });

        response.StatusCode.ShouldBe(HttpStatusCode.OK);
    }

    [Fact]
    public async Task The_stay_rules_have_their_own_codes()
    {
        var scenario = await Scenario.CreateAsync(Host);
        DateOnly checkIn = StaysHost.CheckIn;

        (string Name, DateOnly In, DateOnly Out, int Guests, string Code, string Target)[] cases =
        [
            ("check-out equals check-in", checkIn, checkIn, 2, StayErrorCode.CheckOutBeforeCheckIn, "checkOut"),
            ("check-out before check-in", checkIn, checkIn.AddDays(-2), 2, StayErrorCode.CheckOutBeforeCheckIn, "checkOut"),
            ("31 nights", checkIn, checkIn.AddDays(31), 2, StayErrorCode.StayTooLong, "checkOut"),
            ("in the past", Today.AddDays(-5), Today.AddDays(-3), 2, StayErrorCode.CheckInInPast, "checkIn"),
        ];

        foreach ((string name, DateOnly stayIn, DateOnly stayOut, int guests, string code, string target) in cases)
        {
            using HttpResponseMessage response = await Host.SendAsync(
                HttpMethod.Post, $"{scenario.RoomPath}/quotes", new { checkIn = stayIn, checkOut = stayOut, quantity = 1, guests });
            response.StatusCode.ShouldBe(HttpStatusCode.BadRequest, name);
            JsonElement details = (await StaysHost.JsonAsync(response)).GetProperty("error").GetProperty("details");
            details.EnumerateArray().ShouldContain(d => d.GetProperty("code").GetString() == code && d.GetProperty("target").GetString() == target, name);
        }
    }

    [Fact]
    public async Task Thirty_nights_is_the_longest_stay()
    {
        var scenario = await Scenario.CreateAsync(Host, rate: 100m);
        DateOnly checkIn = StaysHost.CheckIn;
        await scenario.SetNightsAsync(checkIn, checkIn.AddDays(30), allotment: 3, rate: 100m);

        Quote quote = await scenario.QuoteAsync(checkIn: checkIn, checkOut: checkIn.AddDays(30));

        quote.Nights.Count.ShouldBe(30);
        quote.Total.Amount.ShouldBe(3000m);
    }

    [Fact]
    public async Task More_guests_than_the_rooms_sleep_is_OccupancyExceeded()
    {
        var scenario = await Scenario.CreateAsync(Host, maxOccupancy: 2);

        using HttpResponseMessage response = await Host.SendAsync(
            HttpMethod.Post, $"{scenario.RoomPath}/quotes", new { checkIn = StaysHost.CheckIn, checkOut = StaysHost.CheckOut, quantity = 2, guests = 5 });

        response.StatusCode.ShouldBe(HttpStatusCode.BadRequest);
        JsonElement error = (await StaysHost.JsonAsync(response)).GetProperty("error");
        error.GetProperty("code").GetString().ShouldBe(StayErrorCode.OccupancyExceeded);
        error.GetProperty("target").GetString().ShouldBe("guests");

        (await scenario.QuoteAsync(quantity: 2, guests: 4)).ShouldNotBeNull();
    }

    [Fact]
    public async Task A_stay_with_a_night_short_of_rooms_or_without_a_rate_is_SoldOut()
    {
        var scenario = await Scenario.CreateAsync(Host, allotment: 3);

        // Asking for more rooms than there are.
        using HttpResponseMessage tooMany = await Host.SendAsync(
            HttpMethod.Post, $"{scenario.RoomPath}/quotes", new { checkIn = StaysHost.CheckIn, checkOut = StaysHost.CheckOut, quantity = 4, guests = 4 });
        tooMany.StatusCode.ShouldBe(HttpStatusCode.Conflict);
        (await StaysHost.ErrorCodeAsync(tooMany)).ShouldBe(StayErrorCode.SoldOut);

        // One night in the middle with none left.
        await scenario.SetNightsAsync(StaysHost.CheckIn.AddDays(1), StaysHost.CheckIn.AddDays(1), allotment: 0);
        using HttpResponseMessage hole = await Host.SendAsync(
            HttpMethod.Post, $"{scenario.RoomPath}/quotes", new { checkIn = StaysHost.CheckIn, checkOut = StaysHost.CheckOut, quantity = 1, guests = 2 });
        hole.StatusCode.ShouldBe(HttpStatusCode.Conflict);

        // Nights nobody has put on sale.
        using HttpResponseMessage unrated = await Host.SendAsync(
            HttpMethod.Post, $"{scenario.RoomPath}/quotes", new { checkIn = Today.AddDays(200), checkOut = Today.AddDays(202), quantity = 1, guests = 2 });
        unrated.StatusCode.ShouldBe(HttpStatusCode.Conflict);
        (await StaysHost.ErrorCodeAsync(unrated)).ShouldBe(StayErrorCode.SoldOut);
    }

    [Fact]
    public async Task A_property_that_is_not_published_cannot_be_quoted()
    {
        var scenario = await Scenario.CreateAsync(Host, publish: false);

        using HttpResponseMessage response = await Host.SendAsync(
            HttpMethod.Post, $"{scenario.RoomPath}/quotes", new { checkIn = StaysHost.CheckIn, checkOut = StaysHost.CheckOut, quantity = 1, guests = 2 });

        response.StatusCode.ShouldBe(HttpStatusCode.Conflict);
        (await StaysHost.ErrorCodeAsync(response)).ShouldBe(StayErrorCode.PropertyNotPublished);
    }

    [Fact]
    public async Task A_quote_for_something_that_does_not_exist_is_not_found()
    {
        var scenario = await Scenario.CreateAsync(Host);
        object body = new { checkIn = StaysHost.CheckIn, checkOut = StaysHost.CheckOut, quantity = 1, guests = 2 };

        using HttpResponseMessage property = await Host.SendAsync(HttpMethod.Post, $"/properties/{Guid.CreateVersion7()}/room-types/{scenario.RoomTypeId}/quotes", body);
        using HttpResponseMessage room = await Host.SendAsync(HttpMethod.Post, $"{scenario.PropertyPath}/room-types/{Guid.CreateVersion7()}/quotes", body);

        (await StaysHost.ErrorCodeAsync(property)).ShouldBe(StayErrorCode.PropertyNotFound);
        (await StaysHost.ErrorCodeAsync(room)).ShouldBe(StayErrorCode.RoomTypeNotFound);
    }

    [Fact]
    public async Task The_quote_total_is_formatted_with_two_decimals_on_the_wire()
    {
        var scenario = await Scenario.CreateAsync(Host, rate: 99.5m);

        using HttpResponseMessage response = await Host.SendAsync(
            HttpMethod.Post, $"{scenario.RoomPath}/quotes", new { checkIn = StaysHost.CheckIn, checkOut = StaysHost.CheckOut, quantity = 1, guests = 1 });

        JsonElement total = (await StaysHost.JsonAsync(response)).GetProperty("total");
        total.GetProperty("amount").GetString().ShouldBe((99.5m * 3).ToString("F2", CultureInfo.InvariantCulture));
        total.GetProperty("currency").GetString().ShouldBe("EUR");
    }
}
