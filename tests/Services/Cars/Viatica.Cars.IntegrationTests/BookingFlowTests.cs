using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Viatica.Cars.Domain;
using Viatica.Contracts;
using Viatica.Testing;

namespace Viatica.Cars.IntegrationTests;

/// <summary>
/// Booking's side of the saga over Kafka, against the whole service: requests go out through an outbox and relay
/// on the booking topic, Cars' consumer and inbox apply them, and the answers come back on the cars topic.
/// </summary>
public sealed class BookingFlowTests(PostgresFixture postgres, KafkaFixture kafka, ValkeyFixture valkey) : IAsyncLifetime
{
    private CarsHost host = null!;
    private BookingStandIn booking = null!;

    public async ValueTask InitializeAsync()
    {
        host = await CarsHost.StartAsync(postgres, kafka, valkey);
        booking = await BookingStandIn.StartAsync(postgres, kafka, host.BookingTopic);
    }

    public async ValueTask DisposeAsync()
    {
        await booking.DisposeAsync();
        host.Dispose();
    }

    private static DateTimeOffset Monday => Scenario.Monday;

    private async Task<ProbedMessage> AnswerAsync(string type, Guid holdId) =>
        await host.Probe.WaitForAsync(e => e.Type == type && e.DataString("holdId") == holdId.ToString());

    [Fact]
    public async Task A_checkout_holds_commits_and_cancels_a_car_and_every_step_is_answered_on_the_cars_topic()
    {
        Marketplace m = await Scenario.ArrangeAsync(host, allotment: 2, rate: 40m);
        string token = await Scenario.TokenAsync(host.Anonymous(), m, Monday, Monday.AddHours(48));
        Guid trip = Guid.CreateVersion7();
        Guid item = Guid.CreateVersion7();
        Guid hold = Guid.CreateVersion7();

        await booking.SendAsync(new CarHoldRequested(trip, item, hold, token, 35));
        ProbedMessage placed = await AnswerAsync("CarHoldPlaced", hold);

        placed.Key.ShouldBe(trip.ToString("N"));
        placed.DataString("tripId").ShouldBe(trip.ToString());
        placed.DataString("itemId").ShouldBe(item.ToString());
        placed.DataString("locationId").ShouldBe(m.LocationId.ToString());
        placed.DataString("dropOffLocationId").ShouldBe(m.LocationId.ToString());
        placed.Data.GetProperty("total").GetProperty("amount").GetString().ShouldBe("80.00");
        placed.Data.GetProperty("total").GetProperty("currency").GetString().ShouldBe("EUR");
        DateTimeOffset.Parse(placed.DataString("pickUpAt")!, System.Globalization.CultureInfo.InvariantCulture).ShouldBe(Monday);
        DateTimeOffset.Parse(placed.DataString("expiresAt")!, System.Globalization.CultureInfo.InvariantCulture)
            .ShouldBe(host.Clock.GetUtcNow() + TimeSpan.FromMinutes(15));
        (await host.DaysAsync(m, "2026-12-07", "2026-12-08")).Select(static d => d.Held).ShouldBe([1, 1]);

        await booking.SendAsync(new CarHoldCommitRequested(trip, item, hold));
        ProbedMessage committed = await AnswerAsync("CarHoldCommitted", hold);
        committed.Key.ShouldBe(trip.ToString("N"));
        (await host.DaysAsync(m, "2026-12-07", "2026-12-08")).Select(static d => (d.Held, d.Sold)).ShouldBe([(0, 1), (0, 1)]);

        await booking.SendAsync(new CarBookingCancelRequested(trip, item, hold));
        await AnswerAsync("CarBookingCancelled", hold);
        (await host.DaysAsync(m, "2026-12-07", "2026-12-08")).Select(static d => (d.Held, d.Sold)).ShouldBe([(0, 0), (0, 0)]);
        await host.AssertInventoryIsConsistentAsync(m);
    }

    [Fact]
    public async Task A_placed_hold_carries_the_partner_the_pick_up_and_the_cancellation_policy_in_force_for_Booking()
    {
        Marketplace m = await Scenario.ArrangeAsync(host, allotment: 2, rate: 40m);
        string token = await Scenario.TokenAsync(host.Anonymous(), m, Monday, Monday.AddHours(48));
        var request = new CarHoldRequested(Guid.CreateVersion7(), Guid.CreateVersion7(), Guid.CreateVersion7(), token, 35);

        await booking.SendAsync(request);
        ProbedMessage placed = await AnswerAsync("CarHoldPlaced", request.HoldId);

        JsonElement terms = placed.Data.GetProperty("terms");
        terms.GetProperty("partnerId").GetGuid().ShouldBe(m.PartnerId);
        terms.GetProperty("startsAt").GetDateTimeOffset().ShouldBe(Monday);
        terms.GetProperty("cancellationPolicy").GetProperty("freeUntilHoursBeforeStart").GetInt32().ShouldBe(48);
        terms.GetProperty("cancellationPolicy").GetProperty("penaltyAfter").GetString().ShouldBe("FirstUnit", "the first day of a rental is the first unit");

        // The same request again is answered again, and the answer carries the terms as well.
        await booking.SendAsync(request);
        IReadOnlyList<ProbedMessage> answers = await host.Probe.WaitForCountAsync(2, e => e.Type == "CarHoldPlaced" && e.DataString("holdId") == request.HoldId.ToString());
        foreach (ProbedMessage answer in answers)
        {
            answer.Data.TryGetProperty("terms", out _).ShouldBeTrue();
        }
    }

    [Fact]
    public async Task A_release_gives_the_car_back_and_says_so()
    {
        Marketplace m = await Scenario.ArrangeAsync(host, allotment: 1);
        string token = await Scenario.TokenAsync(host.Anonymous(), m, Monday, Monday.AddHours(24));
        Guid trip = Guid.CreateVersion7();
        Guid item = Guid.CreateVersion7();
        Guid hold = Guid.CreateVersion7();
        await booking.SendAsync(new CarHoldRequested(trip, item, hold, token, 35));
        await AnswerAsync("CarHoldPlaced", hold);

        await booking.SendAsync(new CarHoldReleaseRequested(trip, item, hold));

        await AnswerAsync("CarHoldReleased", hold);
        (await host.DaysAsync(m, "2026-12-07", "2026-12-07")).Single().Held.ShouldBe(0);
    }

    [Fact]
    public async Task A_refusal_names_its_reason_and_holds_nothing()
    {
        Marketplace m = await Scenario.ArrangeAsync(host, allotment: 1);
        string token = await Scenario.TokenAsync(host.Anonymous(), m, Monday, Monday.AddHours(24));
        string[] parts = token.Split('.');
        string tampered = parts[0] + "." + parts[1][..^2] + (parts[1][^2] == 'A' ? "B" : "A") + parts[1][^1];
        Guid tamperedHold = Guid.CreateVersion7();
        Guid expiredHold = Guid.CreateVersion7();

        await booking.SendAsync(new CarHoldRequested(Guid.CreateVersion7(), Guid.CreateVersion7(), tamperedHold, tampered, 35));
        ProbedMessage invalid = await AnswerAsync("CarHoldRejected", tamperedHold);
        invalid.DataString("reason").ShouldBe("QuoteInvalid");

        host.Clock.Advance(TimeSpan.FromMinutes(15));
        await booking.SendAsync(new CarHoldRequested(Guid.CreateVersion7(), Guid.CreateVersion7(), expiredHold, token, 35));
        ProbedMessage expired = await AnswerAsync("CarHoldRejected", expiredHold);
        expired.DataString("reason").ShouldBe("QuoteExpired");

        (await host.DaysAsync(m, "2026-12-07", "2026-12-07")).Single().Held.ShouldBe(0);
    }

    [Fact]
    public async Task Of_two_checkouts_for_the_last_car_one_is_held_and_one_is_rejected_as_sold_out()
    {
        Marketplace m = await Scenario.ArrangeAsync(host, allotment: 1);
        string token = await Scenario.TokenAsync(host.Anonymous(), m, Monday, Monday.AddHours(24));
        Guid[] holds = [Guid.CreateVersion7(), Guid.CreateVersion7()];

        await booking.SendAsync(
            new CarHoldRequested(Guid.CreateVersion7(), Guid.CreateVersion7(), holds[0], token, 35),
            new CarHoldRequested(Guid.CreateVersion7(), Guid.CreateVersion7(), holds[1], token, 35));

        IReadOnlyList<ProbedMessage> answers = await host.Probe.WaitForCountAsync(
            2, e => e.Type is "CarHoldPlaced" or "CarHoldRejected" && holds.Any(h => e.DataString("holdId") == h.ToString()));
        answers.Count(static a => a.Type == "CarHoldPlaced").ShouldBe(1);
        ProbedMessage rejected = answers.Single(static a => a.Type == "CarHoldRejected");
        rejected.DataString("reason").ShouldBe("SoldOut");
        await host.AssertInventoryIsConsistentAsync(m);
    }

    [Fact]
    public async Task The_same_message_delivered_twice_is_applied_once_and_answered_once()
    {
        Marketplace m = await Scenario.ArrangeAsync(host, allotment: 3);
        string token = await Scenario.TokenAsync(host.Anonymous(), m, Monday, Monday.AddHours(24));
        var request = new CarHoldRequested(Guid.CreateVersion7(), Guid.CreateVersion7(), Guid.CreateVersion7(), token, 35);

        await booking.DeliverTwiceAsync(request);

        // A marker request behind it on the same key: once it is answered, the duplicates ahead of it have been read.
        var marker = new CarHoldRequested(request.TripId, Guid.CreateVersion7(), Guid.CreateVersion7(), token, 35);
        await booking.SendAsync(marker);
        await AnswerAsync("CarHoldPlaced", marker.HoldId);

        host.Probe.Received.Count(e => e.Type == "CarHoldPlaced" && e.DataString("holdId") == request.HoldId.ToString()).ShouldBe(1);
        (await host.QueryAsync(db => db.Holds.CountAsync(h => h.Id == request.HoldId))).ShouldBe(1);
        (await host.DaysAsync(m, "2026-12-07", "2026-12-07")).Single().Held.ShouldBe(2);
    }

    [Fact]
    public async Task The_same_request_resent_under_a_new_event_id_is_answered_again_without_holding_a_second_car()
    {
        Marketplace m = await Scenario.ArrangeAsync(host, allotment: 3);
        string token = await Scenario.TokenAsync(host.Anonymous(), m, Monday, Monday.AddHours(24));
        var request = new CarHoldRequested(Guid.CreateVersion7(), Guid.CreateVersion7(), Guid.CreateVersion7(), token, 35);

        await booking.SendAsync(request);
        await booking.SendAsync(request);

        IReadOnlyList<ProbedMessage> answers = await host.Probe.WaitForCountAsync(
            2, e => e.Type == "CarHoldPlaced" && e.DataString("holdId") == request.HoldId.ToString());
        answers[1].DataString("expiresAt").ShouldBe(answers[0].DataString("expiresAt"));
        (await host.DaysAsync(m, "2026-12-07", "2026-12-07")).Single().Held.ShouldBe(1);
    }

    [Fact]
    public async Task Requests_for_holds_this_service_never_made_do_not_stop_the_consumer()
    {
        Marketplace m = await Scenario.ArrangeAsync(host, allotment: 1);
        string token = await Scenario.TokenAsync(host.Anonymous(), m, Monday, Monday.AddHours(24));
        Guid stranger = Guid.CreateVersion7();
        Guid trip = Guid.CreateVersion7();

        await booking.SendAsync(
            new CarHoldCommitRequested(trip, Guid.CreateVersion7(), stranger),
            new CarBookingCancelRequested(trip, Guid.CreateVersion7(), stranger),
            new CarHoldReleaseRequested(trip, Guid.CreateVersion7(), stranger),
            new CarHoldRequested(trip, Guid.CreateVersion7(), Guid.CreateVersion7(), token, 35));

        // The release of a hold that was never made is confirmed, so Booking's unwinding can finish; the commit and
        // the cancellation of it are ignored; and the request behind them all is still served.
        await AnswerAsync("CarHoldReleased", stranger);
        await host.Probe.WaitForAsync(e => e.Type == "CarHoldPlaced" && e.Key == trip.ToString("N"));
        host.Probe.Received.Any(e => e.Type is "CarHoldCommitted" or "CarBookingCancelled" && e.DataString("holdId") == stranger.ToString()).ShouldBeFalse();
    }

    [Fact]
    public async Task The_background_sweeper_expires_a_hold_and_tells_booking_without_being_asked()
    {
        using CarsHost sweeping = await CarsHost.StartAsync(postgres, kafka, valkey, sweepInterval: TimeSpan.FromMinutes(1));
        await using BookingStandIn standIn = await BookingStandIn.StartAsync(postgres, kafka, sweeping.BookingTopic);
        Marketplace m = await Scenario.ArrangeAsync(sweeping, allotment: 1);
        string token = await Scenario.TokenAsync(sweeping.Anonymous(), m, Monday, Monday.AddHours(24));
        var request = new CarHoldRequested(Guid.CreateVersion7(), Guid.CreateVersion7(), Guid.CreateVersion7(), token, 35);
        await standIn.SendAsync(request);
        await sweeping.Probe.WaitForAsync(e => e.Type == "CarHoldPlaced" && e.DataString("holdId") == request.HoldId.ToString());

        sweeping.Clock.Advance(TimeSpan.FromMinutes(16));

        ProbedMessage expired = await sweeping.Probe.WaitForAsync(e => e.Type == "CarHoldExpired" && e.DataString("holdId") == request.HoldId.ToString());
        expired.DataString("tripId").ShouldBe(request.TripId.ToString());
        (await sweeping.DaysAsync(m, "2026-12-07", "2026-12-07")).Single().Held.ShouldBe(0);
        (await sweeping.HoldRowAsync(request.HoldId)).Status.ShouldBe(HoldStatus.Expired);
    }
}
