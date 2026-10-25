using System.Text.Json;
using Viatica.Contracts;
using Viatica.Testing;

namespace Viatica.Booking.IntegrationTests;

/// <summary>
/// The saga's deadline (docs/design.md): the shortest expiry known less two minutes, driven by a scheduled check that
/// needs no incoming event. The clock here is the host's own and the test moves it; a stage is held open by pausing the
/// owner whose answer would end it, so the trip is stuck exactly where the test wants it when time passes.
/// </summary>
public sealed class DeadlineTests(WorldFixture fixture) : IClassFixture<WorldFixture>, IAsyncLifetime
{
    private static readonly TimeSpan PastTheDeadline = TimeSpan.FromMinutes(14);

    private BookingWorld world => fixture.World;

    // Nothing paused by an earlier test is still holding answers back.
    public async ValueTask InitializeAsync() => await world.Owners.ResumeAllAsync();

    public ValueTask DisposeAsync() => ValueTask.CompletedTask;

    [Fact]
    public async Task A_trip_still_holding_at_its_deadline_unwinds_and_a_hold_that_lands_afterwards_is_released()
    {
        (Guid trip, Guid stay, Guid car, Guid flight) = await world.BasketAsync();
        world.Owners.Pause<StayHoldRequested>();

        await world.CheckoutAsync(trip);
        await Eventually.MatchesAsync(
            () => world.TripAsync(trip), t => BookingWorld.ItemStatus(t, car) == "Held" && BookingWorld.ItemStatus(t, flight) == "Ordered");

        world.Clock.Advance(PastTheDeadline);
        await Eventually.MatchesAsync(() => world.TripAsync(trip), t => t.GetProperty("status").GetString() is "Unwinding" or "Failed");

        // The stay's owner reads its request now, after the trip has given up: the hold is placed, and given back.
        await world.Owners.ResumeAsync<StayHoldRequested>();
        await world.WaitForTripAsync(trip, "Failed");
        await Eventually.MatchesAsync(() => Task.FromResult(world.Owners.HoldOfItem(stay)!.State), state => state == "released");

        (await world.NextAsync(nameof(TripFailed), trip)).DataString("reason").ShouldBe("DeadlineReached");
        world.Owners.HoldOfItem(car)!.State.ShouldBe("released");
        world.Owners.OrderOfItem(flight)!.State.ShouldBe("voided");
        world.Owners.ReceivedFor(trip).OfType<PaymentAuthorizationRequested>().ShouldBeEmpty();
    }

    [Fact]
    public async Task A_trip_still_authorising_at_its_deadline_unwinds_and_the_authorisation_that_lands_afterwards_is_voided_once()
    {
        (Guid trip, Guid stay, Guid car, Guid flight) = await world.BasketAsync();
        world.Owners.Pause<PaymentAuthorizationRequested>();

        await world.CheckoutAsync(trip);
        await world.Owners.WaitForAsync<PaymentAuthorizationRequested>(e => e.TripId == trip);

        world.Clock.Advance(PastTheDeadline);
        await Eventually.MatchesAsync(() => world.TripAsync(trip), t => t.GetProperty("status").GetString() == "Unwinding");
        world.Owners.ReceivedFor(trip).OfType<PaymentVoidRequested>().ShouldBeEmpty("nothing is authorised yet");

        await world.Owners.ResumeAsync<PaymentAuthorizationRequested>();
        await world.WaitForTripAsync(trip, "Failed");

        (await world.NextAsync(nameof(TripFailed), trip)).DataString("reason").ShouldBe("DeadlineReached");
        world.Owners.ReceivedFor(trip).OfType<PaymentVoidRequested>().Count().ShouldBe(1);
        world.Owners.PaymentOfTrip(trip)!.State.ShouldBe("voided");
        world.Owners.HoldOfItem(stay)!.State.ShouldBe("released");
        world.Owners.HoldOfItem(car)!.State.ShouldBe("released");
        world.Owners.OrderOfItem(flight)!.State.ShouldBe("voided");
        world.Owners.ReceivedFor(trip).OfType<FlightTicketingRequested>().ShouldBeEmpty();
    }

    [Fact]
    public async Task A_trip_still_ticketing_at_its_deadline_unwinds_and_the_ticket_that_lands_afterwards_is_voided()
    {
        (Guid trip, Guid stay, Guid car, Guid flight) = await world.BasketAsync();
        world.Owners.Pause<FlightTicketingRequested>();

        await world.CheckoutAsync(trip);
        await world.Owners.WaitForAsync<FlightTicketingRequested>(e => e.TripId == trip);

        world.Clock.Advance(PastTheDeadline);
        await Eventually.MatchesAsync(() => world.TripAsync(trip), t => t.GetProperty("status").GetString() == "Unwinding");
        world.Owners.ReceivedFor(trip).OfType<FlightOrderVoidRequested>().ShouldBeEmpty("a ticketing request is open; voiding now would race it");

        await world.Owners.ResumeAsync<FlightTicketingRequested>();
        await world.WaitForTripAsync(trip, "Failed");

        (await world.NextAsync(nameof(TripFailed), trip)).DataString("reason").ShouldBe("DeadlineReached");
        world.Owners.OrderOfItem(flight)!.State.ShouldBe("voided");
        world.Owners.PaymentOfTrip(trip)!.State.ShouldBe("voided");
        world.Owners.HoldOfItem(stay)!.State.ShouldBe("released");
        world.Owners.HoldOfItem(car)!.State.ShouldBe("released");
        world.Owners.ReceivedFor(trip).OfType<StayHoldCommitRequested>().ShouldBeEmpty();
    }

    [Fact]
    public async Task A_trip_that_has_started_committing_is_not_unwound_by_the_deadline_and_confirms_when_the_commits_land()
    {
        (Guid trip, _, _, _) = await world.BasketAsync();
        world.Owners.Pause<StayHoldCommitRequested>();

        await world.CheckoutAsync(trip);
        await world.Owners.WaitForAsync<StayHoldCommitRequested>(e => e.TripId == trip);

        world.Clock.Advance(TimeSpan.FromHours(2));
        await Task.Delay(500); // several scheduled checks, on the host's real interval, with the clock far past the deadline
        (await world.TripAsync(trip)).GetProperty("status").GetString().ShouldBe("Committing");

        await world.Owners.ResumeAsync<StayHoldCommitRequested>();
        await world.WaitForTripAsync(trip, "Confirmed");
        world.Owners.ReceivedFor(trip).OfType<StayHoldReleaseRequested>().ShouldBeEmpty();
    }

    [Fact]
    public async Task A_capture_answered_long_after_the_deadline_still_confirms_the_trip()
    {
        (Guid trip, _, _, _) = await world.BasketAsync();
        world.Owners.Pause<PaymentCaptureRequested>();

        await world.CheckoutAsync(trip);
        await world.Owners.WaitForAsync<PaymentCaptureRequested>(e => e.TripId == trip);

        world.Clock.Advance(TimeSpan.FromHours(5));
        await Task.Delay(500);
        (await world.TripAsync(trip)).GetProperty("status").GetString().ShouldBe("Capturing");

        await world.Owners.ResumeAsync<PaymentCaptureRequested>();
        JsonElement confirmed = await world.WaitForTripAsync(trip, "Confirmed");

        confirmed.GetProperty("total").GetProperty("amount").GetString().ShouldBe("540.00");
        world.Owners.PaymentOfTrip(trip)!.State.ShouldBe("captured");
    }

    [Fact]
    public async Task The_deadline_follows_the_shortest_expiry_the_owners_report_and_not_a_fixed_guess()
    {
        // The airline gives only ten minutes to ticket; the rooms are held for fifteen. The deadline is eight minutes in.
        (Guid trip, _, _, _) = await world.BasketAsync(flight: new ItemPlan { Total = 150m, TicketingLimit = TimeSpan.FromMinutes(10) });
        world.Owners.Pause<PaymentAuthorizationRequested>();

        await world.CheckoutAsync(trip);
        await world.Owners.WaitForAsync<PaymentAuthorizationRequested>(e => e.TripId == trip);

        world.Clock.Advance(TimeSpan.FromMinutes(7));
        await Task.Delay(500);
        (await world.TripAsync(trip)).GetProperty("status").GetString().ShouldBe("Authorizing", "seven minutes in, the deadline is a minute away");

        world.Clock.Advance(TimeSpan.FromMinutes(2));
        await Eventually.MatchesAsync(() => world.TripAsync(trip), t => t.GetProperty("status").GetString() == "Unwinding");
        await world.Owners.ResumeAsync<PaymentAuthorizationRequested>();
        await world.WaitForTripAsync(trip, "Failed");
    }
}
