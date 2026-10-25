using System.Net;
using System.Text.Json;
using Viatica.Contracts;
using Viatica.Testing;

namespace Viatica.Booking.IntegrationTests;

/// <summary>
/// BKG-4: a traveller cancels one item of a confirmed trip. The item's own policy decides the refund, the trip total
/// follows, and the money goes back as one payment operation. The policy arithmetic itself is in the unit tests; here
/// the whole path runs: the request, the owner's confirmation, the refund request, the answer, and the events.
/// </summary>
public sealed class CancellationTests(WorldFixture fixture) : IClassFixture<WorldFixture>, IAsyncLifetime
{
    private BookingWorld world => fixture.World;

    public async ValueTask InitializeAsync() => await world.Owners.ResumeAllAsync();

    public ValueTask DisposeAsync() => ValueTask.CompletedTask;

    private async Task<(Guid Trip, Guid Stay, Guid Car, Guid Flight)> ConfirmedAsync(ItemPlan? stay = null, ItemPlan? car = null, ItemPlan? flight = null)
    {
        (Guid trip, Guid s, Guid c, Guid f) = await world.BasketAsync(stay, car, flight);
        await world.CheckoutAsync(trip);
        await world.WaitForTripAsync(trip, "Confirmed");
        await world.Owners.WaitForAsync<PaymentCaptureRequested>(e => e.TripId == trip);
        return (trip, s, c, f);
    }

    private Task<HttpResponseMessage> CancelAsync(Guid trip, Guid item, string? token = null) =>
        world.SendAsync(HttpMethod.Post, $"/trips/{trip}/items/{item}/cancel", token: token ?? world.Traveller);

    private async Task<JsonElement> WaitForItemAsync(Guid trip, Guid item, string status) =>
        await Eventually.MatchesAsync(() => world.TripAsync(trip), t => BookingWorld.ItemStatus(t, item) == status);

    private static JsonElement ItemOf(JsonElement trip, Guid item) =>
        trip.GetProperty("items").EnumerateArray().Single(i => i.GetProperty("id").GetGuid() == item);

    [Fact]
    public async Task A_stay_cancelled_inside_its_free_window_refunds_everything_as_one_payment_operation()
    {
        (Guid trip, Guid stay, _, _) = await ConfirmedAsync(stay: new ItemPlan { Total = 300m, StartsIn = TimeSpan.FromDays(30), FreeHours = 48 });

        HttpResponseMessage accepted = await CancelAsync(trip, stay);

        accepted.StatusCode.ShouldBe(HttpStatusCode.Accepted);
        accepted.Headers.GetValues("Operation-Location").Single().ShouldBe($"http://localhost/operations/{stay}?api-version=2026-11-01");
        JsonElement cancelled = await WaitForItemAsync(trip, stay, "Cancelled");

        ItemOf(cancelled, stay).GetProperty("refund").GetProperty("amount").GetString().ShouldBe("300.00");
        cancelled.GetProperty("total").GetProperty("amount").GetString().ShouldBe("240.00", "the trip now costs the car and the flight");
        cancelled.GetProperty("refunded").GetProperty("amount").GetString().ShouldBe("300.00");

        PaymentRefundRequested refund = await world.Owners.WaitForAsync<PaymentRefundRequested>(e => e.TripId == trip);
        refund.Amount.Amount.ShouldBe(300m);
        refund.PaymentId.ShouldBe(world.Owners.PaymentOfTrip(trip)!.PaymentId);
        world.Owners.HoldOfItem(stay)!.State.ShouldBe("cancelled");

        ProbedMessage announced = await world.NextAsync(nameof(TripItemCancelled), trip);
        announced.Data.GetProperty("refund").GetProperty("amount").GetString().ShouldBe("300.00");
        announced.DataString("itemId").ShouldBe(stay.ToString());

        // The payment confirms the refund, and the operation says it is done.
        JsonElement refunded = await Eventually.MatchesAsync(
            () => world.TripAsync(trip), t => ItemOf(t, stay).GetProperty("refundStatus").GetString() == "Refunded");
        refunded.GetProperty("status").GetString().ShouldBe("Confirmed");
        JsonElement operation = await BookingWorld.JsonAsync(await world.SendAsync(HttpMethod.Get, $"/operations/{stay}", token: world.Traveller));
        operation.GetProperty("status").GetString().ShouldBe("Succeeded");
    }

    [Theory]
    [InlineData(CancellationPenalty.FirstUnit, "200.00")]
    [InlineData(CancellationPenalty.FullAmount, "0.00")]
    public async Task A_stay_cancelled_after_its_free_window_refunds_by_the_penalty_in_force_when_it_was_booked(CancellationPenalty penalty, string refund)
    {
        (Guid trip, Guid stay, _, _) = await ConfirmedAsync(stay: new ItemPlan { Total = 300m, StartsIn = TimeSpan.FromHours(24), FreeHours = 48, Penalty = penalty, Units = 3 });
        int refundsBefore = world.Owners.Count<PaymentRefundRequested>(e => e.TripId == trip);

        (await CancelAsync(trip, stay)).StatusCode.ShouldBe(HttpStatusCode.Accepted);
        JsonElement cancelled = await WaitForItemAsync(trip, stay, "Cancelled");

        ItemOf(cancelled, stay).GetProperty("refund").GetProperty("amount").GetString().ShouldBe(refund);
        await world.NextAsync(nameof(TripItemCancelled), trip, m => m.DataString("itemId") == stay.ToString());

        if (refund == "0.00")
        {
            await world.SettledAsync(trip);
            world.Owners.Count<PaymentRefundRequested>(e => e.TripId == trip).ShouldBe(refundsBefore, "there is nothing to give back, so no payment operation");
        }
        else
        {
            (await world.Owners.WaitForAsync<PaymentRefundRequested>(e => e.TripId == trip)).Amount.Amount.ShouldBe(200m);
        }
    }

    [Fact]
    public async Task A_car_cancelled_inside_its_penalty_window_keeps_one_day_and_a_flight_refunds_what_the_airline_says()
    {
        (Guid trip, _, Guid car, Guid flight) = await ConfirmedAsync(
            car: new ItemPlan { Total = 90m, StartsIn = TimeSpan.FromHours(24), FreeHours = 48, Units = 3 },
            flight: new ItemPlan { Total = 150m });

        await CancelAsync(trip, car);
        JsonElement afterCar = await WaitForItemAsync(trip, car, "Cancelled");
        ItemOf(afterCar, car).GetProperty("refund").GetProperty("amount").GetString().ShouldBe("60.00");

        await CancelAsync(trip, flight);
        JsonElement afterFlight = await WaitForItemAsync(trip, flight, "Cancelled");
        ItemOf(afterFlight, flight).GetProperty("refund").GetProperty("amount").GetString().ShouldBe("75.00", "the airline's fare rules, as Flights reports them");

        await world.Owners.WaitForAsync<PaymentRefundRequested>(e => e.TripId == trip, atLeast: 2);
        IReadOnlyList<PaymentRefundRequested> refunds = [.. world.Owners.Received.OfType<PaymentRefundRequested>().Where(r => r.TripId == trip)];
        refunds.Select(static r => r.Amount.Amount).ShouldBe([60m, 75m]);
        refunds.Select(static r => r.RefundId).Distinct().Count().ShouldBe(2, "one payment operation per cancellation");
        afterFlight.GetProperty("refunded").GetProperty("amount").GetString().ShouldBe("135.00");
    }

    [Fact]
    public async Task Cancelling_every_item_cancels_the_trip()
    {
        (Guid trip, Guid stay, Guid car, Guid flight) = await ConfirmedAsync();

        foreach (Guid item in new[] { stay, car, flight })
        {
            await CancelAsync(trip, item);
            await WaitForItemAsync(trip, item, "Cancelled");
        }

        JsonElement done = await world.WaitForTripAsync(trip, "Cancelled");
        done.GetProperty("total").GetProperty("amount").GetString().ShouldBe("0.00");
        (await BookingWorld.JsonAsync(await world.SendAsync(HttpMethod.Get, $"/operations/{trip}", token: world.Traveller)))
            .GetProperty("status").GetString().ShouldBe("Succeeded");
    }

    [Fact]
    public async Task Asking_twice_cancels_once_and_refunds_once()
    {
        (Guid trip, Guid stay, _, _) = await ConfirmedAsync();

        HttpResponseMessage first = await CancelAsync(trip, stay);
        HttpResponseMessage second = await CancelAsync(trip, stay);
        await WaitForItemAsync(trip, stay, "Cancelled");
        HttpResponseMessage third = await CancelAsync(trip, stay);
        await world.SettledAsync(trip);

        new[] { first, second, third }.ShouldAllBe(static r => r.StatusCode == HttpStatusCode.Accepted);
        (await BookingWorld.JsonAsync(third)).GetProperty("status").GetString().ShouldBe("Succeeded");
        world.Owners.ReceivedFor(trip).OfType<StayBookingCancelRequested>().Count().ShouldBe(1);
        world.Owners.ReceivedFor(trip).OfType<PaymentRefundRequested>().Count().ShouldBe(1);
    }

    [Fact]
    public async Task What_cannot_be_cancelled_says_why()
    {
        // A draft has nothing confirmed.
        Guid draft = await world.NewTripAsync();
        Guid item = await world.AddStayAsync(draft);
        HttpResponseMessage notYet = await CancelAsync(draft, item);
        notYet.StatusCode.ShouldBe(HttpStatusCode.Conflict);
        (await BookingWorld.ErrorCodeAsync(notYet)).ShouldBe("ItemNotCancellable");

        // A checkout still running is not the time.
        world.Owners.Pause<PaymentCaptureRequested>();
        (Guid running, Guid stay, _, _) = await world.BasketAsync();
        await world.CheckoutAsync(running);
        await world.Owners.WaitForAsync<PaymentCaptureRequested>(e => e.TripId == running);
        HttpResponseMessage busy = await CancelAsync(running, stay);
        busy.StatusCode.ShouldBe(HttpStatusCode.Conflict);
        (await BookingWorld.ErrorCodeAsync(busy)).ShouldBe("CheckoutInProgress");
        await world.Owners.ResumeAsync<PaymentCaptureRequested>();
        await world.WaitForTripAsync(running, "Confirmed");

        // An item that is not on the trip, a trip that is not yours.
        HttpResponseMessage missing = await CancelAsync(running, Guid.NewGuid());
        missing.StatusCode.ShouldBe(HttpStatusCode.NotFound);
        (await BookingWorld.ErrorCodeAsync(missing)).ShouldBe("ItemNotFound");
        HttpResponseMessage stranger = await CancelAsync(running, stay, BookingWorld.TravellerToken());
        stranger.StatusCode.ShouldBe(HttpStatusCode.NotFound);
        (await BookingWorld.ErrorCodeAsync(stranger)).ShouldBe("TripNotFound");
    }

    [Fact]
    public async Task A_stay_that_has_started_cannot_be_cancelled()
    {
        (Guid trip, Guid stay, _, _) = await ConfirmedAsync(stay: new ItemPlan { Total = 300m, StartsIn = TimeSpan.FromHours(1) });
        world.Clock.Advance(TimeSpan.FromHours(2));

        HttpResponseMessage late = await CancelAsync(trip, stay);

        late.StatusCode.ShouldBe(HttpStatusCode.Conflict);
        (await BookingWorld.ErrorCodeAsync(late)).ShouldBe("ItemNotCancellable");
        world.Owners.ReceivedFor(trip).OfType<StayBookingCancelRequested>().ShouldBeEmpty();
    }

    [Fact]
    public async Task A_cancel_its_owner_never_answers_times_out_can_be_asked_again_and_a_late_answer_still_counts()
    {
        (Guid trip, _, _, Guid flight) = await ConfirmedAsync(flight: new ItemPlan { Total = 150m });
        world.Owners.Pause<FlightOrderCancelRequested>();

        (await CancelAsync(trip, flight)).StatusCode.ShouldBe(HttpStatusCode.Accepted);
        await world.Owners.WaitForAsync<FlightOrderCancelRequested>(e => e.TripId == trip);
        (await BookingWorld.JsonAsync(await world.SendAsync(HttpMethod.Get, $"/operations/{flight}", token: world.Traveller)))
            .GetProperty("status").GetString().ShouldBe("Running");

        world.Clock.Advance(TimeSpan.FromMinutes(3));
        JsonElement timedOut = await Eventually.MatchesAsync(
            async () => await BookingWorld.JsonAsync(await world.SendAsync(HttpMethod.Get, $"/operations/{flight}", token: world.Traveller)),
            o => o.GetProperty("status").GetString() == "Failed");
        timedOut.GetProperty("error").GetProperty("code").GetString().ShouldBe("CancellationTimedOut");
        BookingWorld.ItemStatus(await world.TripAsync(trip), flight).ShouldBe("Ticketed", "the flight is still the traveller's");

        // Asked again, it is sent again.
        (await CancelAsync(trip, flight)).StatusCode.ShouldBe(HttpStatusCode.Accepted);
        await world.Owners.WaitForAsync<FlightOrderCancelRequested>(e => e.TripId == trip, atLeast: 2);

        // The owner answers at last. The answer is taken, and the refund is the airline's.
        await world.Owners.ResumeAsync<FlightOrderCancelRequested>();
        JsonElement cancelled = await WaitForItemAsync(trip, flight, "Cancelled");
        ItemOf(cancelled, flight).GetProperty("refund").GetProperty("amount").GetString().ShouldBe("75.00");
        await world.SettledAsync(trip);
        world.Owners.ReceivedFor(trip).OfType<PaymentRefundRequested>().Count().ShouldBe(1, "two answers, one refund");
    }
}
