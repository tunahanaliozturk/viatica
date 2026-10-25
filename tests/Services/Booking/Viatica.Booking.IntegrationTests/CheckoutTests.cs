using System.Net;
using System.Text.Json;
using Viatica.Contracts;
using Viatica.Testing;

namespace Viatica.Booking.IntegrationTests;

/// <summary>
/// BKG-2 and the happy path of BKG-3, against the real host and real topics: the order of the saga as the owners saw
/// it, the operation resource, the progress stream, and what a repeated checkout does and does not do.
/// </summary>
public sealed class CheckoutTests(WorldFixture fixture) : IClassFixture<WorldFixture>, IAsyncLifetime
{
    private BookingWorld world => fixture.World;

    // Nothing paused by an earlier test is still holding answers back.
    public async ValueTask InitializeAsync() => await world.Owners.ResumeAllAsync();

    public ValueTask DisposeAsync() => ValueTask.CompletedTask;

    [Fact]
    public async Task A_trip_with_a_stay_a_car_and_a_flight_is_held_authorised_ticketed_committed_and_captured_in_that_order()
    {
        (Guid trip, Guid stay, Guid car, Guid flight) = await world.BasketAsync(
            stay: new ItemPlan { Total = 300m }, car: new ItemPlan { Total = 90m }, flight: new ItemPlan { Total = 150m });

        HttpResponseMessage accepted = await world.CheckoutAsync(trip);

        accepted.StatusCode.ShouldBe(HttpStatusCode.Accepted);
        accepted.Headers.GetValues("Operation-Location").Single().ShouldBe($"http://localhost/operations/{trip}?api-version=2026-11-01");
        (await BookingWorld.JsonAsync(accepted)).GetProperty("id").GetString().ShouldBe(trip.ToString());

        JsonElement confirmed = await world.WaitForTripAsync(trip, "Confirmed");
        confirmed.GetProperty("total").GetProperty("amount").GetString().ShouldBe("540.00");
        foreach (Guid item in new[] { stay, car, flight })
        {
            BookingWorld.ItemStatus(confirmed, item).ShouldBeOneOf("Committed", "Ticketed");
        }

        // The order of the saga, as the owners read it: every hold request, then the authorisation, then the tickets,
        // then the commits, then the capture. Nothing is committed before the airline has ticketed.
        IReadOnlyList<IIntegrationEvent> sent = world.Owners.ReceivedFor(trip);
        int Index<T>() => sent.ToList().FindIndex(static e => e is T);
        int LastIndex<T>() => sent.ToList().FindLastIndex(static e => e is T);

        LastIndex<StayHoldRequested>().ShouldBeLessThan(Index<PaymentAuthorizationRequested>());
        LastIndex<CarHoldRequested>().ShouldBeLessThan(Index<PaymentAuthorizationRequested>());
        LastIndex<FlightOrderRequested>().ShouldBeLessThan(Index<PaymentAuthorizationRequested>());
        Index<PaymentAuthorizationRequested>().ShouldBeLessThan(Index<FlightTicketingRequested>());
        Index<FlightTicketingRequested>().ShouldBeLessThan(Index<StayHoldCommitRequested>());
        Index<FlightTicketingRequested>().ShouldBeLessThan(Index<CarHoldCommitRequested>());
        LastIndex<StayHoldCommitRequested>().ShouldBeLessThan(Index<PaymentCaptureRequested>());
        LastIndex<CarHoldCommitRequested>().ShouldBeLessThan(Index<PaymentCaptureRequested>());

        sent.OfType<PaymentAuthorizationRequested>().Single().Amount.Amount.ShouldBe(540m);
        sent.OfType<PaymentCaptureRequested>().Single().Amount.Amount.ShouldBe(540m);
        sent.OfType<PaymentAuthorizationRequested>().Single().PaymentId.ShouldBe(sent.OfType<PaymentCaptureRequested>().Single().PaymentId);

        ProbedMessage done = await world.NextAsync(nameof(TripConfirmed), trip);
        done.Data.GetProperty("total").GetProperty("amount").GetString().ShouldBe("540.00");
        done.Data.GetProperty("items").GetArrayLength().ShouldBe(3);
        done.Data.GetProperty("travellerId").GetGuid().ShouldBe(world.TravellerId);

        // What the owners ended with: the books agree with the trip.
        world.Owners.HoldOfItem(stay)!.State.ShouldBe("committed");
        world.Owners.HoldOfItem(car)!.State.ShouldBe("committed");
        world.Owners.OrderOfItem(flight)!.State.ShouldBe("ticketed");
        world.Owners.PaymentOfTrip(trip)!.State.ShouldBe("captured");
        world.Owners.PaymentOfTrip(trip)!.Amount.ShouldBe(540m);

        // The passenger list reached the airline's side intact, and the token reached Payments.
        world.Owners.PassengersOf(flight)!.Single().Surname.ShouldBe("Karabulutoglu");
        world.Owners.PaymentTokenOf(trip).ShouldBe("pm_test_visa");

        JsonElement operation = await BookingWorld.JsonAsync(await world.SendAsync(HttpMethod.Get, $"/operations/{trip}", token: world.Traveller));
        operation.GetProperty("status").GetString().ShouldBe("Succeeded");
        operation.GetProperty("resourceLocation").GetString().ShouldBe($"http://localhost/trips/{trip}?api-version=2026-11-01");
    }

    [Fact]
    public async Task Every_state_change_of_a_trip_is_an_event_on_the_topic_in_the_order_it_happened()
    {
        (Guid trip, Guid stay, _, _) = await world.BasketAsync();
        await world.CheckoutAsync(trip);
        await world.WaitForTripAsync(trip, "Confirmed");
        await world.NextAsync(nameof(TripConfirmed), trip);

        IReadOnlyList<ProbedMessage> states = await world.AllAsync(nameof(TripStateChanged), trip, 7);
        states.Select(static m => m.DataString("state")).ShouldBe(["Draft", "Holding", "Authorizing", "Ticketing", "Committing", "Capturing", "Confirmed"]);

        IReadOnlyList<ProbedMessage> stayStates = await world.Probe.WaitForCountAsync(
            3, m => m.Type == nameof(TripItemStateChanged) && m.DataString("itemId") == stay.ToString());
        stayStates.Select(static m => m.DataString("state")).ShouldBe(["Pending", "Held", "Committed"]);
    }

    [Fact]
    public async Task The_progress_stream_sends_the_trip_each_time_it_changes_and_closes_when_the_checkout_ends()
    {
        (Guid trip, _, _, _) = await world.BasketAsync();

        // Held at capture for a moment, so the stream has a state to send that is neither the first nor the last. A stream
        // sends the latest state each time it looks, so which of the quicker states it catches depends on timing.
        world.Owners.Pause<PaymentCaptureRequested>();
        Task<IReadOnlyList<(int Id, JsonElement Trip)>> reading = world.ReadProgressAsync(trip);
        await world.CheckoutAsync(trip);
        await world.WaitForTripAsync(trip, "Capturing");
        await Task.Delay(500);
        await world.Owners.ResumeAsync<PaymentCaptureRequested>();
        IReadOnlyList<(int Id, JsonElement Trip)> events = await reading;

        events.Select(static e => e.Id).ShouldBe(events.Select(static e => e.Id).Order(), "event ids are the trip's revisions and only go up");
        events.Select(static e => e.Id).Distinct().Count().ShouldBe(events.Count);
        events[^1].Trip.GetProperty("status").GetString().ShouldBe("Confirmed");
        string?[] statuses = [.. events.Select(static e => e.Trip.GetProperty("status").GetString())];
        statuses.ShouldContain("Capturing", "the state the trip waited in");
        statuses[0].ShouldNotBe("Confirmed", "it began before the end");

        // A client that reconnects with the last event it had is sent nothing and the stream closes.
        (await world.ReadProgressAsync(trip, events[^1].Id)).ShouldBeEmpty();

        // And one that is behind is sent where the trip is now, not every step it missed: a stream carries the latest
        // state, and what led to it is on the topic.
        IReadOnlyList<(int Id, JsonElement Trip)> behind = await world.ReadProgressAsync(trip, events[0].Id);
        behind.Select(static e => e.Id).ShouldBe([events[^1].Id]);
    }

    [Fact]
    public async Task A_checkout_repeated_with_the_same_body_changes_nothing_and_asks_nobody_twice()
    {
        (Guid trip, _, _, _) = await world.BasketAsync();

        HttpResponseMessage first = await world.CheckoutAsync(trip);
        HttpResponseMessage second = await world.CheckoutAsync(trip);
        HttpResponseMessage third = await world.CheckoutAsync(trip);
        await world.WaitForTripAsync(trip, "Confirmed");
        await world.Owners.WaitForAsync<TripConfirmed>(e => e.TripId == trip);
        HttpResponseMessage afterwards = await world.CheckoutAsync(trip);

        new[] { first, second, third, afterwards }.ShouldAllBe(static r => r.StatusCode == HttpStatusCode.Accepted);
        (await BookingWorld.JsonAsync(afterwards)).GetProperty("status").GetString().ShouldBe("Succeeded");

        IReadOnlyList<IIntegrationEvent> sent = world.Owners.ReceivedFor(trip);
        sent.OfType<StayHoldRequested>().Count().ShouldBe(1);
        sent.OfType<CarHoldRequested>().Count().ShouldBe(1);
        sent.OfType<FlightOrderRequested>().Count().ShouldBe(1);
        sent.OfType<PaymentAuthorizationRequested>().Count().ShouldBe(1);
        sent.OfType<PaymentCaptureRequested>().Count().ShouldBe(1);
        sent.OfType<TripConfirmed>().Count().ShouldBe(1);
    }

    [Fact]
    public async Task Checkout_repeated_at_the_same_moment_runs_the_saga_once()
    {
        (Guid trip, _, _, _) = await world.BasketAsync();

        HttpResponseMessage[] answers = await Task.WhenAll(Enumerable.Range(0, 12).Select(_ => world.CheckoutAsync(trip)));
        await world.WaitForTripAsync(trip, "Confirmed");

        answers.ShouldAllBe(static r => r.StatusCode == HttpStatusCode.Accepted);
        world.Owners.ReceivedFor(trip).OfType<StayHoldRequested>().Count().ShouldBe(1);
        world.Owners.ReceivedFor(trip).OfType<PaymentAuthorizationRequested>().Count().ShouldBe(1);
    }

    [Fact]
    public async Task The_same_trip_with_a_different_body_is_a_conflict_and_changes_nothing()
    {
        (Guid trip, _, _, _) = await world.BasketAsync();
        (await world.CheckoutAsync(trip)).StatusCode.ShouldBe(HttpStatusCode.Accepted);

        HttpResponseMessage other = await world.CheckoutAsync(trip, BookingWorld.Details(given: "Someone"));

        other.StatusCode.ShouldBe(HttpStatusCode.Conflict);
        (await BookingWorld.ErrorCodeAsync(other)).ShouldBe("CheckoutConflict");

        HttpResponseMessage otherCard = await world.CheckoutAsync(trip, BookingWorld.Details(payment: "pm_test_mastercard"));
        otherCard.StatusCode.ShouldBe(HttpStatusCode.Conflict);

        await world.WaitForTripAsync(trip, "Confirmed");
        world.Owners.PaymentTokenOf(trip).ShouldBe("pm_test_visa");
        world.Owners.ReceivedFor(trip).OfType<PaymentAuthorizationRequested>().Count().ShouldBe(1);
    }

    [Fact]
    public async Task A_checkout_that_is_not_valid_names_every_field_and_starts_nothing()
    {
        (Guid trip, _, _, _) = await world.BasketAsync();

        HttpResponseMessage bad = await world.CheckoutAsync(trip, new
        {
            contact = new { email = "not-an-email" },
            passengers = Array.Empty<object>(),
            paymentMethodToken = "",
        });

        bad.StatusCode.ShouldBe(HttpStatusCode.BadRequest);
        JsonElement error = (await BookingWorld.JsonAsync(bad)).GetProperty("error");
        error.GetProperty("code").GetString().ShouldBe("InvalidRequest");
        error.GetProperty("details").EnumerateArray().Select(static d => d.GetProperty("code").GetString())
            .ShouldBe(["InvalidEmail", "InvalidPassengerCount", "NoAdult", "PaymentMethodTokenRequired"], ignoreOrder: true);
        (await world.TripAsync(trip)).GetProperty("status").GetString().ShouldBe("Draft");
    }

    [Fact]
    public async Task An_empty_trip_and_a_trip_that_is_not_yours_cannot_be_checked_out()
    {
        Guid empty = await world.NewTripAsync();
        HttpResponseMessage nothing = await world.CheckoutAsync(empty);
        nothing.StatusCode.ShouldBe(HttpStatusCode.Conflict);
        (await BookingWorld.ErrorCodeAsync(nothing)).ShouldBe("TripEmpty");

        (Guid trip, _, _, _) = await world.BasketAsync();
        HttpResponseMessage stranger = await world.CheckoutAsync(trip, token: BookingWorld.TravellerToken());
        stranger.StatusCode.ShouldBe(HttpStatusCode.NotFound);
        (await BookingWorld.ErrorCodeAsync(stranger)).ShouldBe("TripNotFound");
        (await world.CheckoutAsync(trip, token: null)).StatusCode.ShouldBe(HttpStatusCode.Accepted);
    }
}
