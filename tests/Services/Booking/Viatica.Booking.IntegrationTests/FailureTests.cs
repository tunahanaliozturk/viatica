using System.Text.Json;
using Viatica.Contracts;
using Viatica.Testing;

namespace Viatica.Booking.IntegrationTests;

/// <summary>
/// BKG-3: the other half. Every way the checkout can fail ends with nothing held, ordered, authorised or charged, as
/// the owners' own books say, and the trip says why. Each test names what it sent and what it must never have sent.
/// </summary>
public sealed class FailureTests(WorldFixture fixture) : IClassFixture<WorldFixture>, IAsyncLifetime
{
    private BookingWorld world => fixture.World;

    // Nothing paused by an earlier test is still holding answers back.
    public async ValueTask InitializeAsync() => await world.Owners.ResumeAllAsync();

    public ValueTask DisposeAsync() => ValueTask.CompletedTask;

    [Fact]
    public async Task A_refused_hold_releases_what_was_held_voids_the_order_and_charges_nothing()
    {
        (Guid trip, Guid stay, Guid car, Guid flight) = await world.BasketAsync(car: new ItemPlan { Total = 90m, Fate = Fate.Refuse });

        await world.CheckoutAsync(trip);
        JsonElement failed = await world.WaitForTripAsync(trip, "Failed");
        ProbedMessage announced = await world.NextAsync(nameof(TripFailed), trip);

        announced.DataString("reason").ShouldBe("ItemRejected");
        failed.GetProperty("failure").GetString().ShouldBe("ItemRejected");
        BookingWorld.ItemStatus(failed, car).ShouldBe("Rejected");
        BookingWorld.ItemStatus(failed, stay).ShouldBe("Released");
        BookingWorld.ItemStatus(failed, flight).ShouldBe("Voided");

        // The owners agree.
        world.Owners.HoldOfItem(stay)!.State.ShouldBe("released");
        world.Owners.OrderOfItem(flight)!.State.ShouldBe("voided");
        world.Owners.PaymentOfTrip(trip).ShouldBeNull("nothing was authorised");

        // And Booking never asked for what a failed trip must not ask for.
        IReadOnlyList<IIntegrationEvent> sent = world.Owners.ReceivedFor(trip);
        sent.OfType<PaymentAuthorizationRequested>().ShouldBeEmpty();
        sent.OfType<FlightTicketingRequested>().ShouldBeEmpty();
        sent.OfType<StayHoldCommitRequested>().ShouldBeEmpty();
        sent.OfType<PaymentCaptureRequested>().ShouldBeEmpty();

        JsonElement operation = await BookingWorld.JsonAsync(await world.SendAsync(HttpMethod.Get, $"/operations/{trip}", token: world.Traveller));
        operation.GetProperty("status").GetString().ShouldBe("Failed");
        operation.GetProperty("error").GetProperty("code").GetString().ShouldBe("ItemRejected");
    }

    [Fact]
    public async Task A_declined_card_releases_everything_and_there_is_no_authorisation_to_void()
    {
        (Guid trip, Guid stay, Guid car, Guid flight) = await world.BasketAsync();
        world.Owners.Decline(trip);

        await world.CheckoutAsync(trip);
        await world.WaitForTripAsync(trip, "Failed");

        (await world.NextAsync(nameof(TripFailed), trip)).DataString("reason").ShouldBe("PaymentDeclined");
        world.Owners.HoldOfItem(stay)!.State.ShouldBe("released");
        world.Owners.HoldOfItem(car)!.State.ShouldBe("released");
        world.Owners.OrderOfItem(flight)!.State.ShouldBe("voided");
        world.Owners.PaymentOfTrip(trip)!.State.ShouldBe("declined");

        IReadOnlyList<IIntegrationEvent> sent = world.Owners.ReceivedFor(trip);
        sent.OfType<PaymentVoidRequested>().ShouldBeEmpty("a declined authorisation holds nothing");
        sent.OfType<FlightTicketingRequested>().ShouldBeEmpty();
        sent.OfType<PaymentCaptureRequested>().ShouldBeEmpty();
    }

    [Fact]
    public async Task A_refused_ticket_voids_the_order_releases_the_rooms_and_voids_the_authorisation_without_committing_anything()
    {
        (Guid trip, Guid stay, Guid car, Guid flight) = await world.BasketAsync(flight: new ItemPlan { Total = 150m, Fate = Fate.RefuseTicketing });

        await world.CheckoutAsync(trip);
        await world.WaitForTripAsync(trip, "Failed");

        (await world.NextAsync(nameof(TripFailed), trip)).DataString("reason").ShouldBe("TicketingRefused");
        world.Owners.OrderOfItem(flight)!.State.ShouldBe("voided");
        world.Owners.HoldOfItem(stay)!.State.ShouldBe("released");
        world.Owners.HoldOfItem(car)!.State.ShouldBe("released");
        world.Owners.PaymentOfTrip(trip)!.State.ShouldBe("voided");

        IReadOnlyList<IIntegrationEvent> sent = world.Owners.ReceivedFor(trip);
        sent.OfType<StayHoldCommitRequested>().ShouldBeEmpty("ticketing comes first, so a refusal never has to undo a committed stay");
        sent.OfType<CarHoldCommitRequested>().ShouldBeEmpty();
        sent.OfType<PaymentCaptureRequested>().ShouldBeEmpty();
        sent.OfType<PaymentVoidRequested>().Count().ShouldBe(1);
    }

    [Fact]
    public async Task A_commit_lost_to_the_owners_sweeper_after_the_flight_was_ticketed_unwinds_the_whole_trip()
    {
        (Guid trip, Guid stay, Guid car, Guid flight) = await world.BasketAsync();

        // The car's owner reads the commit late; its sweeper reaches the hold first.
        world.Owners.Pause<CarHoldCommitRequested>();
        await world.CheckoutAsync(trip);
        await world.Owners.WaitForAsync<CarHoldCommitRequested>(e => e.TripId == trip);
        await world.Owners.WaitForAsync<FlightTicketingRequested>(e => e.TripId == trip);
        await world.Owners.ExpireAsync(car);
        await world.Owners.ResumeAsync<CarHoldCommitRequested>();

        await world.WaitForTripAsync(trip, "Failed");

        (await world.NextAsync(nameof(TripFailed), trip)).DataString("reason").ShouldBe("DeadlineReached");
        world.Owners.HoldOfItem(car)!.State.ShouldBe("expired");
        world.Owners.HoldOfItem(stay)!.State.ShouldBe("cancelled", "the stay was committed, so it is given back, not released");
        world.Owners.OrderOfItem(flight)!.State.ShouldBe("voided");
        world.Owners.PaymentOfTrip(trip)!.State.ShouldBe("voided");
        world.Owners.ReceivedFor(trip).OfType<PaymentCaptureRequested>().ShouldBeEmpty();
    }

    [Fact]
    public async Task A_capture_that_payments_refuses_gives_everything_back_and_the_trip_fails_instead_of_waiting_for_ever()
    {
        (Guid trip, Guid stay, Guid car, Guid flight) = await world.BasketAsync();
        world.Owners.RefuseCapture(trip);

        await world.CheckoutAsync(trip);
        await world.WaitForTripAsync(trip, "Failed");

        (await world.NextAsync(nameof(TripFailed), trip)).DataString("reason").ShouldBe("PaymentDeclined");
        world.Owners.HoldOfItem(stay)!.State.ShouldBe("cancelled");
        world.Owners.HoldOfItem(car)!.State.ShouldBe("cancelled");
        world.Owners.OrderOfItem(flight)!.State.ShouldBe("voided");
        world.Owners.PaymentOfTrip(trip)!.State.ShouldBe("voided");
        world.Owners.ReceivedFor(trip).OfType<PaymentCaptureRequested>().Count().ShouldBe(1);
    }

    [Fact]
    public async Task A_hold_that_arrives_after_the_trip_gave_up_on_the_item_is_released_exactly_once()
    {
        (Guid trip, Guid stay, Guid car, _) = await world.BasketAsync(
            stay: new ItemPlan { Total = 300m, Fate = Fate.RefuseThenSucceedLate }, car: new ItemPlan { Total = 90m, Fate = Fate.Refuse });

        await world.CheckoutAsync(trip);
        await world.WaitForTripAsync(trip, "Failed");

        // Whether the unwinding sent a release behind the stay's request before the refusal arrived depends on which
        // came first, so count from here: the failed trip is settled, and the late hold must add exactly one.
        int before = world.Owners.ReceivedFor(trip).OfType<StayHoldReleaseRequested>().Count();

        // Judged again, the stay is placed, long after the trip failed.
        await world.Owners.RunLateWorkAsync();
        await world.Owners.WaitForAsync<StayHoldReleaseRequested>(e => e.TripId == trip, atLeast: before + 1);
        await Eventually.MatchesAsync(() => Task.FromResult(world.Owners.HoldOfItem(stay)!.State), state => state == "released");

        // Give a second release the time to show itself before saying there is none.
        int inbox = await world.InboxCountAsync();
        await world.Owners.SayAsync(new StayHoldExpired(Guid.NewGuid(), trip, Guid.NewGuid()));
        await Eventually.MatchesAsync(world.InboxCountAsync, count => count > inbox);
        world.Owners.ReceivedFor(trip).OfType<StayHoldReleaseRequested>().Count().ShouldBe(before + 1);
        world.Owners.HoldOfItem(car).ShouldBeNull();
    }

    [Fact]
    public async Task An_order_that_appears_after_the_trip_gave_up_on_the_flight_is_voided_exactly_once()
    {
        (Guid trip, _, _, Guid flight) = await world.BasketAsync(
            car: new ItemPlan { Total = 90m, Fate = Fate.Refuse }, flight: new ItemPlan { Total = 150m, Fate = Fate.RefuseThenSucceedLate });

        await world.CheckoutAsync(trip);
        await world.WaitForTripAsync(trip, "Failed");
        int before = world.Owners.ReceivedFor(trip).OfType<FlightOrderVoidRequested>().Count();
        before.ShouldBe(0, "the order was refused, so there was nothing to void");

        await world.Owners.RunLateWorkAsync();
        await world.Owners.WaitForAsync<FlightOrderVoidRequested>(e => e.TripId == trip);
        await Eventually.MatchesAsync(() => Task.FromResult(world.Owners.OrderOfItem(flight)!.State), state => state == "voided");

        int inbox = await world.InboxCountAsync();
        await world.Owners.SayAsync(new FlightOrderVoided(Guid.NewGuid(), trip, Guid.NewGuid()));
        await Eventually.MatchesAsync(world.InboxCountAsync, count => count > inbox);
        world.Owners.ReceivedFor(trip).OfType<FlightOrderVoidRequested>().Count().ShouldBe(1);
    }

    [Fact]
    public async Task An_authorisation_that_arrives_after_the_trip_failed_is_voided_exactly_once()
    {
        (Guid trip, _, _, _) = await world.BasketAsync(car: new ItemPlan { Total = 90m, Fate = Fate.Refuse });
        await world.CheckoutAsync(trip);
        await world.WaitForTripAsync(trip, "Failed");
        Guid paymentId = await world.Host.InDbAsync(async db => (await Microsoft.EntityFrameworkCore.EntityFrameworkQueryableExtensions.SingleAsync(db.Trips, t => t.Id == trip)).PaymentId);

        var late = new PaymentAuthorized(paymentId, trip, new Price(540m, "EUR"));
        await world.Owners.SayAsync(late);
        await world.Owners.WaitForAsync<PaymentVoidRequested>(e => e.TripId == trip);

        // The same answer twice more, each under an event id of its own, and the void's own answer. Booking has taken
        // them all in once its inbox has three more rows; by then it has had its chance to void again.
        int before = await world.InboxCountAsync();
        await world.Owners.SayAsync(late);
        await world.Owners.SayAsync(late);
        await world.Owners.SayAsync(new PaymentVoided(paymentId, trip));
        await Eventually.MatchesAsync(world.InboxCountAsync, count => count >= before + 3);

        world.Owners.ReceivedFor(trip).OfType<PaymentVoidRequested>().Count().ShouldBe(1);
    }

    [Fact]
    public async Task An_unanswered_release_is_sent_again_after_the_resend_interval()
    {
        (Guid trip, Guid stay, _, _) = await world.BasketAsync(car: new ItemPlan { Total = 90m, Fate = Fate.Refuse });

        world.Owners.Pause<StayHoldReleaseRequested>();
        await world.CheckoutAsync(trip);
        await world.Owners.WaitForAsync<StayHoldReleaseRequested>(e => e.TripId == trip);
        (await world.TripAsync(trip)).GetProperty("status").GetString().ShouldBe("Unwinding");

        world.Clock.Advance(TimeSpan.FromSeconds(31));
        await world.Owners.WaitForAsync<StayHoldReleaseRequested>(e => e.TripId == trip, atLeast: 2);

        await world.Owners.ResumeAsync<StayHoldReleaseRequested>();
        await world.WaitForTripAsync(trip, "Failed");
        world.Owners.HoldOfItem(stay)!.State.ShouldBe("released");
    }
}
