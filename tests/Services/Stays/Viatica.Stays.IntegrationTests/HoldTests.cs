using System.Net;
using Microsoft.EntityFrameworkCore;
using Viatica.Contracts;
using Viatica.Stays.Application.Features.Quotes;
using Viatica.Stays.Domain;
using Viatica.Testing;

namespace Viatica.Stays.IntegrationTests;

/// <summary>
/// Booking's side of the saga, end to end: its instructions go out through an outbox to <c>booking.v1</c>, Stays
/// consumes them, and the answers are read back from <c>stays.v1</c>. Real Postgres, real Kafka.
/// </summary>
public sealed class HoldTests(StaysFixture stays) : IClassFixture<StaysFixture>
{
    private StaysHost Host => stays.Host;

    private BookingStub Booking => stays.Booking;

    private static bool IsAbout(ProbedMessage m, string type, Guid holdId) => m.Type == type && m.DataString("holdId") == holdId.ToString();

    /// <summary>
    /// Waits until everything sent before it on the trip has been handled. Events with one key are applied in the
    /// order they were sent, so the answer to a release of a hold that never existed can only come after the answers
    /// to what preceded it. It changes nothing else, which is why a test can use it to prove that something did not
    /// happen.
    /// </summary>
    private async Task BarrierAsync(EventProbe probe, Guid tripId)
    {
        var marker = new StayHoldReleaseRequested(tripId, Guid.CreateVersion7(), Guid.CreateVersion7());
        await Booking.SendAsync(marker);
        await probe.WaitForAsync(m => IsAbout(m, "StayHoldReleased", marker.HoldId));
    }

    private static DateOnly[] StayNights => [StaysHost.CheckIn, StaysHost.CheckIn.AddDays(1), StaysHost.CheckIn.AddDays(2)];

    [Fact]
    public async Task A_hold_reserves_every_night_of_the_stay_and_not_the_night_of_departure()
    {
        using EventProbe probe = stays.Probe();
        var scenario = await Scenario.CreateAsync(Host, allotment: 10, rate: 120m);
        Quote quote = await scenario.QuoteAsync(quantity: 2);
        StayHoldRequested request = Scenario.HoldRequest(quote.QuoteToken, quantity: 2);

        await Booking.SendAsync(request);

        ProbedMessage placed = await probe.WaitForAsync(m => IsAbout(m, "StayHoldPlaced", request.HoldId));
        placed.Key.ShouldBe(request.TripId.ToString("N"), "keyed by trip, so the saga's events stay in order");
        placed.Data.GetProperty("itemId").GetGuid().ShouldBe(request.ItemId);
        placed.Data.GetProperty("propertyId").GetGuid().ShouldBe(scenario.PropertyId);
        placed.Data.GetProperty("roomTypeId").GetGuid().ShouldBe(scenario.RoomTypeId);
        placed.Data.GetProperty("checkIn").GetString().ShouldBe("2026-12-01");
        placed.Data.GetProperty("checkOut").GetString().ShouldBe("2026-12-04");
        placed.Data.GetProperty("quantity").GetInt32().ShouldBe(2);
        placed.Data.GetProperty("total").GetProperty("amount").GetString().ShouldBe("720.00");
        placed.Data.GetProperty("expiresAt").GetDateTimeOffset().ShouldBe(Host.Clock.GetUtcNow().AddMinutes(15));

        List<Night> nights = await scenario.NightsAsync();
        nights.Where(n => StayNights.Contains(n.Date)).ShouldAllBe(n => n.Held == 2 && n.Sold == 0 && n.Available == 8);
        nights.Where(n => !StayNights.Contains(n.Date)).ShouldAllBe(n => n.Held == 0, "the night of departure and the nights around it are untouched");
    }

    [Fact]
    public async Task A_placed_hold_carries_the_partner_the_check_in_and_the_cancellation_policy_in_force_for_Booking()
    {
        using EventProbe probe = stays.Probe();
        var scenario = await Scenario.CreateAsync(Host, allotment: 10);
        Quote quote = await scenario.QuoteAsync();
        StayHoldRequested request = Scenario.HoldRequest(quote.QuoteToken);

        await Booking.SendAsync(request);
        ProbedMessage placed = await probe.WaitForAsync(m => IsAbout(m, "StayHoldPlaced", request.HoldId));

        System.Text.Json.JsonElement terms = placed.Data.GetProperty("terms");
        terms.GetProperty("partnerId").GetGuid().ShouldBe(scenario.PartnerId);
        terms.GetProperty("startsAt").GetDateTimeOffset().ShouldBe(new DateTimeOffset(2026, 12, 1, 15, 0, 0, TimeSpan.Zero), "check-in day at the property's check-in time, read as UTC");
        terms.GetProperty("cancellationPolicy").GetProperty("freeUntilHoursBeforeStart").GetInt32().ShouldBe(48);
        terms.GetProperty("cancellationPolicy").GetProperty("penaltyAfter").GetString().ShouldBe("FirstUnit");
    }

    [Fact]
    public async Task A_hold_announces_the_new_availability_with_a_higher_version()
    {
        using EventProbe probe = stays.Probe();
        var scenario = await Scenario.CreateAsync(Host, allotment: 10);
        Quote quote = await scenario.QuoteAsync(quantity: 3);
        StayHoldRequested request = Scenario.HoldRequest(quote.QuoteToken, quantity: 3);

        await Booking.SendAsync(request);
        await probe.WaitForAsync(m => IsAbout(m, "StayHoldPlaced", request.HoldId));

        IReadOnlyList<ProbedMessage> changes = await scenario.EventsAsync(probe, "StayAvailabilityChanged", 2);
        Scenario.Long(changes[1], "availabilityVersion").ShouldBeGreaterThan(Scenario.Long(changes[0], "availabilityVersion"));
        var announced = Scenario.Nights(changes[1]).EnumerateArray().ToDictionary(n => DateOnly.Parse(n.GetProperty("date").GetString()!), n => n.GetProperty("available").GetInt32());
        announced.Keys.ShouldBe(StayNights);
        announced.Values.ShouldAllBe(available => available == 7);
        changes[1].Key.ShouldBe(scenario.PropertyId.ToString("N"));
    }

    [Fact]
    public async Task A_request_sent_twice_under_one_hold_id_holds_once()
    {
        using EventProbe probe = stays.Probe();
        var scenario = await Scenario.CreateAsync(Host, allotment: 10);
        Quote quote = await scenario.QuoteAsync(quantity: 2);
        Guid trip = Guid.CreateVersion7();
        Guid hold = Guid.CreateVersion7();
        StayHoldRequested first = new(trip, Guid.CreateVersion7(), hold, quote.QuoteToken, 2);
        StayHoldRequested again = first with { };
        StayHoldRequested other = new(trip, Guid.CreateVersion7(), Guid.CreateVersion7(), (await scenario.QuoteAsync(quantity: 1)).QuoteToken, 1);

        // Two outbox rows, two event ids, one hold id: what a resent instruction looks like. The third, on the same
        // trip, is read after both, so once it is answered the duplicate has certainly been handled.
        await Booking.SendAsync(first, again, other);
        await probe.WaitForAsync(m => IsAbout(m, "StayHoldPlaced", other.HoldId));

        (await probe.WaitForCountAsync(1, m => IsAbout(m, "StayHoldPlaced", hold))).Count.ShouldBe(1);
        probe.Received.Count(m => IsAbout(m, "StayHoldPlaced", hold) || IsAbout(m, "StayHoldRejected", hold)).ShouldBe(1);
        (await scenario.NightsAsync()).Where(n => StayNights.Contains(n.Date)).ShouldAllBe(n => n.Held == 3, "2 rooms once, plus 1 for the other hold");
        (await Host.InDbAsync(db => db.Holds.CountAsync(h => h.Id == hold))).ShouldBe(1);
    }

    [Fact]
    public async Task A_tampered_token_is_rejected_as_invalid_and_holds_nothing()
    {
        using EventProbe probe = stays.Probe();
        var scenario = await Scenario.CreateAsync(Host);
        Quote quote = await scenario.QuoteAsync();
        string forged = quote.QuoteToken[..^3] + (quote.QuoteToken[^3] == 'A' ? 'B' : 'A') + quote.QuoteToken[^2..];
        StayHoldRequested request = Scenario.HoldRequest(forged);

        await Booking.SendAsync(request);

        ProbedMessage rejected = await probe.WaitForAsync(m => IsAbout(m, "StayHoldRejected", request.HoldId));
        rejected.Data.GetProperty("reason").GetString().ShouldBe("QuoteInvalid");
        (await scenario.NightsAsync()).ShouldAllBe(n => n.Held == 0);
    }

    [Fact]
    public async Task A_token_for_cheaper_rooms_than_asked_for_is_rejected()
    {
        using EventProbe probe = stays.Probe();
        var scenario = await Scenario.CreateAsync(Host);
        Quote quote = await scenario.QuoteAsync(quantity: 1);
        StayHoldRequested request = Scenario.HoldRequest(quote.QuoteToken, quantity: 5);

        await Booking.SendAsync(request);

        (await probe.WaitForAsync(m => IsAbout(m, "StayHoldRejected", request.HoldId))).Data.GetProperty("reason").GetString().ShouldBe("QuoteInvalid");
        (await scenario.NightsAsync()).ShouldAllBe(n => n.Held == 0);
    }

    [Fact]
    public async Task A_token_that_is_not_one_of_ours_is_rejected()
    {
        using EventProbe probe = stays.Probe();
        var scenario = await Scenario.CreateAsync(Host);
        StayHoldRequested request = Scenario.HoldRequest("eyJ2IjoxfQ.AAAA");

        await Booking.SendAsync(request);

        (await probe.WaitForAsync(m => IsAbout(m, "StayHoldRejected", request.HoldId))).Data.GetProperty("reason").GetString().ShouldBe("QuoteInvalid");
    }

    [Fact]
    public async Task An_expired_token_is_rejected_as_expired()
    {
        using EventProbe probe = stays.Probe();
        var scenario = await Scenario.CreateAsync(Host);
        Quote quote = await scenario.QuoteAsync();
        Host.Clock.Advance(TimeSpan.FromMinutes(15));
        StayHoldRequested request = Scenario.HoldRequest(quote.QuoteToken);

        await Booking.SendAsync(request);

        (await probe.WaitForAsync(m => IsAbout(m, "StayHoldRejected", request.HoldId))).Data.GetProperty("reason").GetString().ShouldBe("QuoteExpired");
        (await scenario.NightsAsync()).ShouldAllBe(n => n.Held == 0);
    }

    [Fact]
    public async Task A_property_taken_off_sale_after_the_quote_cannot_be_held()
    {
        using EventProbe probe = stays.Probe();
        var scenario = await Scenario.CreateAsync(Host);
        Quote quote = await scenario.QuoteAsync();
        string etag = StaysHost.ETagOf(await Host.GetAsync(scenario.PropertyPath, scenario.PartnerToken));
        (await Host.SendAsync(HttpMethod.Post, $"{scenario.PropertyPath}/unpublish", token: scenario.PartnerToken, ifMatch: etag)).StatusCode.ShouldBe(HttpStatusCode.OK);
        StayHoldRequested request = Scenario.HoldRequest(quote.QuoteToken);

        await Booking.SendAsync(request);

        (await probe.WaitForAsync(m => IsAbout(m, "StayHoldRejected", request.HoldId))).Data.GetProperty("reason").GetString().ShouldBe("NotBookable");
        (await scenario.NightsAsync()).ShouldAllBe(n => n.Held == 0);
    }

    [Fact]
    public async Task When_the_rooms_have_gone_the_hold_is_rejected_as_sold_out()
    {
        using EventProbe probe = stays.Probe();
        var scenario = await Scenario.CreateAsync(Host, allotment: 2);
        Quote quoteForTwo = await scenario.QuoteAsync(quantity: 2);
        Quote quoteForOne = await scenario.QuoteAsync(quantity: 1);
        StayHoldRequested winner = Scenario.HoldRequest(quoteForTwo.QuoteToken, quantity: 2);
        StayHoldRequested loser = Scenario.HoldRequest(quoteForOne.QuoteToken, quantity: 1);

        await Booking.SendAsync(winner);
        await probe.WaitForAsync(m => IsAbout(m, "StayHoldPlaced", winner.HoldId));
        await Booking.SendAsync(loser);

        (await probe.WaitForAsync(m => IsAbout(m, "StayHoldRejected", loser.HoldId))).Data.GetProperty("reason").GetString().ShouldBe("SoldOut");
        (await scenario.NightsAsync()).Where(n => StayNights.Contains(n.Date)).ShouldAllBe(n => n.Held == 2);
    }

    [Fact]
    public async Task One_short_night_refuses_the_whole_stay_and_leaves_the_other_nights_as_they_were()
    {
        using EventProbe probe = stays.Probe();
        var scenario = await Scenario.CreateAsync(Host, allotment: 5);
        Quote quote = await scenario.QuoteAsync(quantity: 2);

        // The middle night loses a room after the quote, so the first and last night have 2 and the middle has 1.
        await scenario.SetNightsAsync(StaysHost.CheckIn.AddDays(1), StaysHost.CheckIn.AddDays(1), allotment: 1);
        StayHoldRequested request = Scenario.HoldRequest(quote.QuoteToken, quantity: 2);

        await Booking.SendAsync(request);

        (await probe.WaitForAsync(m => IsAbout(m, "StayHoldRejected", request.HoldId))).Data.GetProperty("reason").GetString().ShouldBe("SoldOut");
        (await scenario.NightsAsync()).ShouldAllBe(n => n.Held == 0 && n.Sold == 0, "the first and last night must not keep the rooms they gave");
    }

    [Fact]
    public async Task The_quoted_price_holds_even_if_the_rates_change_before_the_hold()
    {
        using EventProbe probe = stays.Probe();
        var scenario = await Scenario.CreateAsync(Host, rate: 120m);
        Quote quote = await scenario.QuoteAsync();
        quote.Total.Amount.ShouldBe(360m);
        (await scenario.SetNightsAsync(StaysHost.CheckIn, StaysHost.CheckOut, allotment: 10, rate: 200m)).StatusCode.ShouldBe(HttpStatusCode.OK);
        StayHoldRequested request = Scenario.HoldRequest(quote.QuoteToken);

        await Booking.SendAsync(request);

        ProbedMessage placed = await probe.WaitForAsync(m => IsAbout(m, "StayHoldPlaced", request.HoldId));
        placed.Data.GetProperty("total").GetProperty("amount").GetString().ShouldBe("360.00");
        (await Host.InDbAsync(db => db.Holds.AsNoTracking().Where(h => h.Id == request.HoldId).Select(static h => h.Total).SingleAsync())).ShouldBe(360m);
    }

    [Fact]
    public async Task Commit_turns_held_rooms_into_sold_rooms_once()
    {
        using EventProbe probe = stays.Probe();
        var scenario = await Scenario.CreateAsync(Host, allotment: 10);
        Quote quote = await scenario.QuoteAsync(quantity: 2);
        StayHoldRequested request = Scenario.HoldRequest(quote.QuoteToken, quantity: 2);
        await Booking.SendAsync(request);
        await probe.WaitForAsync(m => IsAbout(m, "StayHoldPlaced", request.HoldId));

        // Sent twice, as a saga resending after a restart would, then a release that must change nothing for a
        // committed hold. The barrier is answered only after all three were handled.
        var commit = new StayHoldCommitRequested(request.TripId, request.ItemId, request.HoldId);
        await Booking.SendAsync(commit, commit with { }, new StayHoldReleaseRequested(request.TripId, request.ItemId, request.HoldId));
        await BarrierAsync(probe, request.TripId);

        probe.Received.Count(m => IsAbout(m, "StayHoldCommitted", request.HoldId)).ShouldBe(1);
        probe.Received.ShouldNotContain(m => IsAbout(m, "StayHoldReleased", request.HoldId), "a committed hold is not released");
        (await scenario.NightsAsync()).Where(n => StayNights.Contains(n.Date)).ShouldAllBe(n => n.Sold == 2 && n.Held == 0 && n.Available == 8);
        (await scenario.AvailabilityVersionAsync()).ShouldBe(2, "held to sold leaves the number available as it was, so nothing is announced");
        (await Host.InDbAsync(db => db.Holds.AsNoTracking().Where(h => h.Id == request.HoldId).Select(static h => h.Status).SingleAsync())).ShouldBe(HoldStatus.Committed);
    }

    [Fact]
    public async Task Release_gives_the_rooms_back_once_and_announces_it()
    {
        using EventProbe probe = stays.Probe();
        var scenario = await Scenario.CreateAsync(Host, allotment: 10);
        Quote quote = await scenario.QuoteAsync(quantity: 4);
        StayHoldRequested request = Scenario.HoldRequest(quote.QuoteToken, quantity: 4);
        await Booking.SendAsync(request);
        await probe.WaitForAsync(m => IsAbout(m, "StayHoldPlaced", request.HoldId));

        var release = new StayHoldReleaseRequested(request.TripId, request.ItemId, request.HoldId);
        await Booking.SendAsync(release, release with { });
        await BarrierAsync(probe, request.TripId);

        probe.Received.Count(m => IsAbout(m, "StayHoldReleased", request.HoldId)).ShouldBe(1);
        (await scenario.NightsAsync()).ShouldAllBe(n => n.Held == 0 && n.Sold == 0 && n.Available == 10);
        IReadOnlyList<ProbedMessage> changes = await scenario.EventsAsync(probe, "StayAvailabilityChanged", 3);
        long[] versions = [.. changes.Select(static m => Scenario.Long(m, "availabilityVersion"))];
        versions.ShouldBe([1, 2, 3], "set-up, hold, release");
        (await scenario.AvailabilityVersionAsync()).ShouldBe(3, "the second release announced nothing");
        Scenario.Nights(changes[2])[0].GetProperty("available").GetInt32().ShouldBe(10);
    }

    [Fact]
    public async Task Releasing_a_hold_that_was_never_placed_still_answers_released()
    {
        using EventProbe probe = stays.Probe();
        var release = new StayHoldReleaseRequested(Guid.CreateVersion7(), Guid.CreateVersion7(), Guid.CreateVersion7());

        await Booking.SendAsync(release);

        await probe.WaitForAsync(m => IsAbout(m, "StayHoldReleased", release.HoldId));
    }

    [Fact]
    public async Task Cancelling_a_committed_stay_puts_its_rooms_back_on_sale()
    {
        using EventProbe probe = stays.Probe();
        var scenario = await Scenario.CreateAsync(Host, allotment: 10);
        Quote quote = await scenario.QuoteAsync(quantity: 2);
        StayHoldRequested request = Scenario.HoldRequest(quote.QuoteToken, quantity: 2);
        await Booking.SendAsync(request, new StayHoldCommitRequested(request.TripId, request.ItemId, request.HoldId));
        await probe.WaitForAsync(m => IsAbout(m, "StayHoldCommitted", request.HoldId));

        var cancel = new StayBookingCancelRequested(request.TripId, request.ItemId, request.HoldId);
        await Booking.SendAsync(cancel, cancel with { });
        await BarrierAsync(probe, request.TripId);

        probe.Received.Count(m => IsAbout(m, "StayBookingCancelled", request.HoldId)).ShouldBe(1);
        (await scenario.NightsAsync()).ShouldAllBe(n => n.Sold == 0 && n.Held == 0 && n.Available == 10);
        (await Host.InDbAsync(db => db.Holds.AsNoTracking().Where(h => h.Id == request.HoldId).Select(static h => h.Status).SingleAsync())).ShouldBe(HoldStatus.Cancelled);
    }

    [Fact]
    public async Task A_hold_that_was_never_committed_cannot_be_cancelled_as_a_booking()
    {
        using EventProbe probe = stays.Probe();
        var scenario = await Scenario.CreateAsync(Host, allotment: 10);
        Quote quote = await scenario.QuoteAsync();
        StayHoldRequested request = Scenario.HoldRequest(quote.QuoteToken);
        await Booking.SendAsync(request, new StayBookingCancelRequested(request.TripId, request.ItemId, request.HoldId));
        await probe.WaitForAsync(m => IsAbout(m, "StayHoldPlaced", request.HoldId));
        await BarrierAsync(probe, request.TripId);

        probe.Received.ShouldNotContain(m => IsAbout(m, "StayBookingCancelled", request.HoldId));
        (await scenario.NightsAsync()).Where(n => StayNights.Contains(n.Date)).ShouldAllBe(n => n.Held == 1);
    }
}
