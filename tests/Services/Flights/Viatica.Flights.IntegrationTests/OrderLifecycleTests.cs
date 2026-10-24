using System.Text.Json;
using Viatica.Contracts;
using Viatica.Flights.Domain;
using Viatica.Testing;

namespace Viatica.Flights.IntegrationTests;

/// <summary>Ticketing, void and cancel, driven by the saga's events and answered with the airline's outcome (FLT-4).</summary>
[Collection(DefaultFlightsGroup.Name)]
public sealed class OrderLifecycleTests(DefaultFlights flights) : IAsyncLifetime
{
    private readonly Scenario scenario = new(flights);

    public async ValueTask InitializeAsync() => await flights.Sim.QuietAsync();

    public ValueTask DisposeAsync() => ValueTask.CompletedTask;

    private Task Send(IIntegrationEvent message, Guid? eventId = null) => flights.Booking.SendAsync(message, eventId);

    // --- ticketing ---

    [Fact]
    public async Task Ticketing_issues_one_ticket_number_per_passenger_and_is_answered_again_when_asked_again()
    {
        PlacedOrder order = await scenario.OrderAsync("ZC");

        await Send(new FlightTicketingRequested(order.TripId, order.ItemId, order.OrderId));
        ProbedMessage ticketed = await scenario.NextAsync(nameof(FlightTicketed), order.TripId);

        string[] numbers = [.. ticketed.Data.GetProperty("ticketNumbers").EnumerateArray().Select(static n => n.GetString()!)];
        numbers.Length.ShouldBe(1);
        numbers[0].ShouldMatch("^901[0-9]{10}$");
        ticketed.DataString("orderId").ShouldBe(order.OrderId.ToString());
        (await scenario.RowAsync(order.OrderId)).Status.ShouldBe(FlightOrderStatus.Ticketed);
        (await flights.Sim.GetOrderAsync("ZC", order.BookingReference)).GetProperty("Status").GetString().ShouldBe("Ticketed");

        // A second request, under a new event id, is answered from the order and does not reach the airline.
        int asked = scenario.Calls("ZC", "ticket");
        await Send(new FlightTicketingRequested(order.TripId, order.ItemId, order.OrderId));
        IReadOnlyList<ProbedMessage> answers = await flights.Probe.WaitForCountAsync(
            2, m => m.Type == nameof(FlightTicketed) && m.DataString("tripId") == order.TripId.ToString(), TimeSpan.FromSeconds(45));

        answers[1].Data.GetProperty("ticketNumbers")[0].GetString().ShouldBe(numbers[0]);
        scenario.Calls("ZC", "ticket").ShouldBe(asked);
    }

    [Fact]
    public async Task An_airline_that_refuses_to_ticket_is_reported_and_the_order_stays_booked_for_the_void()
    {
        PlacedOrder order = await scenario.OrderAsync("JG");
        await flights.Sim.SetKnobsAsync("JG", new { TicketingRefusalRate = 1.0 });

        await Send(new FlightTicketingRequested(order.TripId, order.ItemId, order.OrderId));
        ProbedMessage refused = await scenario.NextAsync(nameof(FlightTicketingRefused), order.TripId);

        refused.DataString("orderId").ShouldBe(order.OrderId.ToString());
        refused.DataString("reason")!.ShouldContain("TICKETING_REFUSED");
        FlightOrder row = await scenario.RowAsync(order.OrderId);
        row.Status.ShouldBe(FlightOrderStatus.Booked);
        row.TicketingRefusal!.ShouldContain("TICKETING_REFUSED");

        // The saga unwinds with a void; the seat goes back to the airline.
        int? seatsBefore = await flights.Sim.SeatsLeftAsync(order.Offer.Flights.First().Flight, order.Offer.Flights.First().Date, order.Offer.FareClass[0]);
        await Send(new FlightOrderVoidRequested(order.TripId, order.ItemId, order.OrderId));
        await scenario.NextAsync(nameof(FlightOrderVoided), order.TripId);

        (await flights.Sim.SeatsLeftAsync(order.Offer.Flights.First().Flight, order.Offer.Flights.First().Date, order.Offer.FareClass[0])).ShouldBe(seatsBefore + 1);
        (await scenario.RowAsync(order.OrderId)).Status.ShouldBe(FlightOrderStatus.Voided);
    }

    [Fact]
    public async Task Ticketing_an_order_that_was_voided_is_refused_and_not_sent_to_the_airline()
    {
        PlacedOrder order = await scenario.OrderAsync("ZC");
        await Send(new FlightOrderVoidRequested(order.TripId, order.ItemId, order.OrderId));
        await scenario.NextAsync(nameof(FlightOrderVoided), order.TripId);
        int asked = scenario.Calls("ZC", "ticket");

        await Send(new FlightTicketingRequested(order.TripId, order.ItemId, order.OrderId));

        (await scenario.NextAsync(nameof(FlightTicketingRefused), order.TripId)).DataString("reason").ShouldBe("OrderVoided");
        scenario.Calls("ZC", "ticket").ShouldBe(asked);
    }

    [Fact]
    public async Task Ticketing_an_order_nobody_created_is_refused_and_so_is_one_named_with_the_wrong_trip()
    {
        PlacedOrder order = await scenario.OrderAsync("ZC");
        Guid strangerTrip = Guid.CreateVersion7();

        await Send(new FlightTicketingRequested(Guid.CreateVersion7(), Guid.CreateVersion7(), Guid.CreateVersion7()));
        await Send(new FlightTicketingRequested(strangerTrip, order.ItemId, order.OrderId));

        (await scenario.NextAsync(nameof(FlightTicketingRefused), strangerTrip)).DataString("reason").ShouldBe("UnknownOrder");
        (await scenario.RowAsync(order.OrderId)).Status.ShouldBe(FlightOrderStatus.Booked);
    }

    [Fact]
    public async Task An_airline_that_cannot_be_reached_to_ticket_is_a_refusal_and_the_order_can_still_be_voided()
    {
        PlacedOrder order = await scenario.OrderAsync("ZC");
        await flights.Sim.SetKnobsAsync("ZC", new { Outage = true });

        await Send(new FlightTicketingRequested(order.TripId, order.ItemId, order.OrderId));
        (await scenario.NextAsync(nameof(FlightTicketingRefused), order.TripId)).DataString("reason").ShouldBe("SupplierUnavailable");

        await flights.Sim.SetKnobsAsync("ZC", new { Outage = false });
        await Send(new FlightOrderVoidRequested(order.TripId, order.ItemId, order.OrderId));
        await scenario.NextAsync(nameof(FlightOrderVoided), order.TripId);
    }

    // --- void ---

    [Fact]
    public async Task Voiding_a_booked_order_releases_the_seat_and_a_second_request_changes_nothing()
    {
        PlacedOrder order = await scenario.OrderAsync("ZC");
        (string flight, DateOnly date) = order.Offer.Flights.First();
        int? held = await flights.Sim.SeatsLeftAsync(flight, date, order.Offer.FareClass[0]);

        await Send(new FlightOrderVoidRequested(order.TripId, order.ItemId, order.OrderId));
        ProbedMessage voided = await scenario.NextAsync(nameof(FlightOrderVoided), order.TripId);

        voided.DataString("orderId").ShouldBe(order.OrderId.ToString());
        (await flights.Sim.SeatsLeftAsync(flight, date, order.Offer.FareClass[0])).ShouldBe(held + 1);
        (await flights.Sim.GetOrderAsync("ZC", order.BookingReference)).GetProperty("Status").GetString().ShouldBe("Voided");

        int asked = scenario.Calls("ZC", "void");
        await Send(new FlightOrderVoidRequested(order.TripId, order.ItemId, order.OrderId));
        await flights.Probe.WaitForCountAsync(
            2, m => m.Type == nameof(FlightOrderVoided) && m.DataString("tripId") == order.TripId.ToString(), TimeSpan.FromSeconds(45));

        scenario.Calls("ZC", "void").ShouldBe(asked);
        (await flights.Sim.SeatsLeftAsync(flight, date, order.Offer.FareClass[0])).ShouldBe(held + 1, "the seat is given back once");
    }

    [Fact]
    public async Task Voiding_a_ticketed_order_inside_the_airlines_window_is_a_void()
    {
        PlacedOrder order = await scenario.TicketedOrderAsync("ZC");

        await Send(new FlightOrderVoidRequested(order.TripId, order.ItemId, order.OrderId));
        await scenario.NextAsync(nameof(FlightOrderVoided), order.TripId);

        (await scenario.RowAsync(order.OrderId)).Status.ShouldBe(FlightOrderStatus.Voided);
        (await flights.Sim.GetOrderAsync("ZC", order.BookingReference)).GetProperty("Status").GetString().ShouldBe("Voided");
    }

    [Fact]
    public async Task Past_the_void_window_the_order_is_cancelled_under_the_fare_rules_and_the_refund_is_reported()
    {
        PlacedOrder order = await scenario.TicketedOrderAsync("ZC");
        flights.Sim.Clock.Advance(TimeSpan.FromHours(25));

        await Send(new FlightOrderVoidRequested(order.TripId, order.ItemId, order.OrderId));
        ProbedMessage cancelled = await scenario.NextAsync(nameof(FlightOrderCancelled), order.TripId);

        JsonElement atAirline = await flights.Sim.GetOrderAsync("ZC", order.BookingReference);
        atAirline.GetProperty("Status").GetString().ShouldBe("Cancelled");
        decimal refund = atAirline.GetProperty("RefundAmount").GetDecimal();
        refund.ShouldBeGreaterThan(0m, "a K fare refunds its taxes");
        cancelled.Data.GetProperty("refund").Amount().ShouldBe(refund);
        FlightOrder row = await scenario.RowAsync(order.OrderId);
        row.Status.ShouldBe(FlightOrderStatus.Cancelled);
        row.Refund!.Amount.ShouldBe(refund);
    }

    [Fact]
    public async Task Voiding_an_order_this_service_never_made_is_a_void_because_nothing_is_left_to_release()
    {
        Guid trip = Guid.CreateVersion7();

        await Send(new FlightOrderVoidRequested(trip, Guid.CreateVersion7(), Guid.CreateVersion7()));

        await scenario.NextAsync(nameof(FlightOrderVoided), trip);
    }

    [Fact]
    public async Task A_void_the_airline_cannot_take_yet_is_retried_until_it_can_and_never_answered_with_a_guess()
    {
        PlacedOrder order = await scenario.OrderAsync("ZC");
        await flights.Sim.SetKnobsAsync("ZC", new { Outage = true });
        int asked = scenario.Calls("ZC", "void");

        await Send(new FlightOrderVoidRequested(order.TripId, order.ItemId, order.OrderId));
        await Eventually.MatchesAsync(() => Task.FromResult(scenario.Calls("ZC", "void")), calls => calls > asked, TimeSpan.FromSeconds(20));
        flights.Probe.Received.ShouldNotContain(m => m.Type == nameof(FlightOrderVoided) && m.DataString("tripId") == order.TripId.ToString());

        await flights.Sim.SetKnobsAsync("ZC", new { Outage = false });
        await scenario.NextAsync(nameof(FlightOrderVoided), order.TripId);
        (await scenario.RowAsync(order.OrderId)).Status.ShouldBe(FlightOrderStatus.Voided);
    }

    // --- cancel ---

    [Fact]
    public async Task Cancelling_a_ticketed_order_reports_the_refund_the_fare_gives_and_repeats_the_same_answer()
    {
        PlacedOrder order = await scenario.TicketedOrderAsync("ZC");

        await Send(new FlightOrderCancelRequested(order.TripId, order.ItemId, order.OrderId));
        ProbedMessage cancelled = await scenario.NextAsync(nameof(FlightOrderCancelled), order.TripId);

        JsonElement atAirline = await flights.Sim.GetOrderAsync("ZC", order.BookingReference);
        atAirline.GetProperty("Status").GetString().ShouldBe("Cancelled");
        cancelled.Data.GetProperty("refund").Amount().ShouldBe(atAirline.GetProperty("RefundAmount").GetDecimal());
        cancelled.Data.GetProperty("refund").GetProperty("currency").GetString().ShouldBe("EUR");
        (await scenario.RowAsync(order.OrderId)).Status.ShouldBe(FlightOrderStatus.Cancelled);

        int asked = scenario.Calls("ZC", "cancel");
        await Send(new FlightOrderCancelRequested(order.TripId, order.ItemId, order.OrderId));
        IReadOnlyList<ProbedMessage> answers = await flights.Probe.WaitForCountAsync(
            2, m => m.Type == nameof(FlightOrderCancelled) && m.DataString("tripId") == order.TripId.ToString(), TimeSpan.FromSeconds(45));

        answers[1].Data.GetProperty("refund").Amount().ShouldBe(answers[0].Data.GetProperty("refund").Amount());
        scenario.Calls("ZC", "cancel").ShouldBe(asked);
    }

    [Fact]
    public async Task Cancelling_an_order_that_was_never_ticketed_voids_it_instead()
    {
        PlacedOrder order = await scenario.OrderAsync("ZC");

        await Send(new FlightOrderCancelRequested(order.TripId, order.ItemId, order.OrderId));

        await scenario.NextAsync(nameof(FlightOrderVoided), order.TripId);
        (await scenario.RowAsync(order.OrderId)).Status.ShouldBe(FlightOrderStatus.Voided);
        (await flights.Sim.GetOrderAsync("ZC", order.BookingReference)).GetProperty("Status").GetString().ShouldBe("Voided");
    }

    [Fact]
    public async Task Cancelling_an_order_this_service_never_made_invents_no_refund()
    {
        Guid trip = Guid.CreateVersion7();

        await Send(new FlightOrderCancelRequested(trip, Guid.CreateVersion7(), Guid.CreateVersion7()));
        // A cancel for a different, real order proves the consumer moved past the unknown one.
        PlacedOrder real = await scenario.TicketedOrderAsync("ZC");
        await Send(new FlightOrderCancelRequested(real.TripId, real.ItemId, real.OrderId));
        await scenario.NextAsync(nameof(FlightOrderCancelled), real.TripId);

        flights.Probe.Received.ShouldNotContain(m => m.DataString("tripId") == trip.ToString());
    }
}
