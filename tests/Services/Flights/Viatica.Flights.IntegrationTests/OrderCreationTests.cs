using System.Text.Json;
using Viatica.Contracts;
using Viatica.Flights.Domain;
using Viatica.Kernel.Pii;
using Viatica.Testing;

namespace Viatica.Flights.IntegrationTests;

/// <summary>FLT-4 and FLT-5, order creation: Booking's request in over Kafka, the airline's order, the answer out over Kafka.</summary>
[Collection(DefaultFlightsGroup.Name)]
public sealed class OrderCreationTests(DefaultFlights flights) : IAsyncLifetime
{
    private readonly Scenario scenario = new(flights);

    public async ValueTask InitializeAsync() => await flights.Sim.QuietAsync();

    public ValueTask DisposeAsync() => ValueTask.CompletedTask;

    [Fact]
    public async Task A_valid_request_creates_one_order_and_announces_it()
    {
        PricedOffer offer = await scenario.PriceAsync("ZC");
        Guid trip = Guid.CreateVersion7();
        Guid item = Guid.CreateVersion7();
        int ordersBefore = await flights.Sim.CountOrdersAsync("ZC");

        await flights.Booking.SendAsync(scenario.RequestFor(offer, trip, item));
        ProbedMessage created = await scenario.NextAsync(nameof(FlightOrderCreated), trip);

        created.Key.ShouldBe(trip.ToString("N"));
        created.DataString("itemId").ShouldBe(item.ToString());
        created.DataString("airlineCode").ShouldBe("ZC");
        created.DataString("bookingReference").ShouldNotBeNullOrEmpty();
        created.Data.GetProperty("total").GetProperty("amount").GetString().ShouldBe(offer.Total.Amount.ToString("F2", System.Globalization.CultureInfo.InvariantCulture));
        created.Data.GetProperty("total").GetProperty("currency").GetString().ShouldBe("EUR");
        created.Data.GetProperty("ticketingTimeLimit").GetDateTimeOffset().ShouldBeGreaterThan(DateTimeOffset.UtcNow);

        // The airline holds exactly one order, under our key, and our row mirrors it.
        JsonElement atAirline = (await flights.Sim.FindOrderAsync("ZC", Scenario.Key(item)))!.Value;
        atAirline.GetProperty("BookingReference").GetString().ShouldBe(created.DataString("bookingReference"));
        atAirline.GetProperty("Status").GetString().ShouldBe("Booked");
        (await flights.Sim.CountOrdersAsync("ZC")).ShouldBe(ordersBefore + 1);

        FlightOrder row = await scenario.RowAsync(Guid.Parse(created.DataString("orderId")!));
        row.TripId.ShouldBe(trip);
        row.ItemId.ShouldBe(item);
        row.Status.ShouldBe(FlightOrderStatus.Booked);
        row.Total.Amount.ShouldBe(offer.Total.Amount);
        row.IdempotencyKey.ShouldBe(Scenario.Key(item));
    }

    [Fact]
    public async Task Passenger_data_is_stored_sealed_under_the_services_own_key()
    {
        PlacedOrder order = await scenario.OrderAsync("ZC");

        FlightOrder row = await scenario.RowAsync(order.OrderId);

        row.SealedPassengers.ShouldNotContain("Zeynebnur");
        row.SealedPassengers.ShouldNotContain("Karabulutoglu");
        row.SealedPassengers.ShouldNotContain("TR8675309X");

        // The broker key does not open it: the stored copy has a key of its own, bound to the item.
        flights.Host.BrokerSealer.TryOpenPassengers(row.SealedPassengers, order.TripId, order.ItemId, out _).ShouldBeFalse();
        flights.Host.StorageSealer.TryOpenPassengers(row.SealedPassengers, $"flight-order:{order.ItemId:N}", out IReadOnlyList<PassengerDetails> stored).ShouldBeTrue();
        stored.ShouldBe(Scenario.OneAdult);
    }

    [Fact]
    public async Task A_family_is_ordered_with_each_travellers_type_checked_against_the_offer()
    {
        PricedOffer offer = await scenario.PriceAsync("ZC", adults: 2, children: 1, infants: 1);
        Guid trip = Guid.CreateVersion7();

        await flights.Booking.SendAsync(scenario.RequestFor(offer, trip, Guid.CreateVersion7()));

        (await scenario.NextAsync(nameof(FlightOrderCreated), trip)).DataString("airlineCode").ShouldBe("ZC");
    }

    [Fact]
    public async Task The_same_event_delivered_twice_and_the_same_request_under_a_new_id_make_one_order()
    {
        PricedOffer offer = await scenario.PriceAsync("ZC");
        Guid trip = Guid.CreateVersion7();
        Guid item = Guid.CreateVersion7();
        FlightOrderRequested request = scenario.RequestFor(offer, trip, item);
        int ordersBefore = await flights.Sim.CountOrdersAsync("ZC");
        int postsBefore = scenario.OrderPosts("ZC");

        Guid eventId = Guid.CreateVersion7();
        await flights.Booking.SendAsync(request, eventId);
        await flights.Booking.SendAsync(request, eventId);
        ProbedMessage first = await scenario.NextAsync(nameof(FlightOrderCreated), trip);

        // A producer that retried sends the same request under an id of its own.
        await flights.Booking.SendAsync(request);
        IReadOnlyList<ProbedMessage> announced = await flights.Probe.WaitForCountAsync(
            2, m => m.Type == nameof(FlightOrderCreated) && m.DataString("tripId") == trip.ToString(), TimeSpan.FromSeconds(45));

        announced.Select(static m => m.DataString("orderId")).Distinct().Count().ShouldBe(1);
        announced.Select(static m => m.DataString("bookingReference")).Distinct().Count().ShouldBe(1);
        (await scenario.RowsForAsync(item)).ShouldBe(1);
        (await flights.Sim.CountOrdersAsync("ZC")).ShouldBe(ordersBefore + 1);
        scenario.OrderPosts("ZC").ShouldBe(postsBefore + 1, "the airline was asked to create the order once");
        first.DataString("orderId").ShouldBe(announced[0].DataString("orderId"));
    }

    [Fact]
    public async Task An_offer_the_cache_has_forgotten_is_rejected_without_asking_the_airline()
    {
        PricedOffer offer = await scenario.PriceAsync("ZC");
        Guid trip = Guid.CreateVersion7();
        Guid item = Guid.CreateVersion7();
        int calls = flights.Host.Wire.Total;

        await flights.Booking.SendAsync(scenario.RequestFor(offer with { OfferId = "0192f0c47a3b7cde8f004f7c2b1d9e01" }, trip, item));
        ProbedMessage rejected = await scenario.NextAsync(nameof(FlightOrderRejected), trip);

        rejected.DataString("reason").ShouldBe("OfferExpired");
        rejected.DataString("itemId").ShouldBe(item.ToString());
        flights.Host.Wire.Total.ShouldBe(calls);
        (await scenario.RowsForAsync(item)).ShouldBe(0);
    }

    [Fact]
    public async Task A_price_that_is_not_the_one_booking_expected_is_rejected_as_a_price_change()
    {
        PricedOffer offer = await scenario.PriceAsync("ZC");
        Guid trip = Guid.CreateVersion7();
        int calls = flights.Host.Wire.Total;

        await flights.Booking.SendAsync(scenario.RequestFor(offer, trip, Guid.CreateVersion7(), expected: offer.Total with { Amount = offer.Total.Amount + 1m }));

        (await scenario.NextAsync(nameof(FlightOrderRejected), trip)).DataString("reason").ShouldBe("PriceChanged");
        flights.Host.Wire.Total.ShouldBe(calls);
    }

    [Fact]
    public async Task A_flight_that_sold_out_since_the_reprice_is_rejected_as_sold_out()
    {
        PricedOffer offer = await scenario.PriceAsync("ZC");
        Guid trip = Guid.CreateVersion7();
        foreach ((string flight, DateOnly date) in offer.Flights)
        {
            await flights.Sim.SetSeatsAsync(flight, date, offer.FareClass[0], remaining: 0);
        }

        await flights.Booking.SendAsync(scenario.RequestFor(offer, trip, Guid.CreateVersion7()));

        (await scenario.NextAsync(nameof(FlightOrderRejected), trip)).DataString("reason").ShouldBe("SoldOut");
    }

    [Fact]
    public async Task An_airline_that_is_down_is_a_rejection_and_nothing_is_looked_up()
    {
        PricedOffer offer = await scenario.PriceAsync("ZC");
        Guid trip = Guid.CreateVersion7();
        Guid item = Guid.CreateVersion7();
        await flights.Sim.SetKnobsAsync("ZC", new { Outage = true });
        int lookups = scenario.OrderLookups("ZC");

        await flights.Booking.SendAsync(scenario.RequestFor(offer, trip, item));

        (await scenario.NextAsync(nameof(FlightOrderRejected), trip)).DataString("reason").ShouldBe("SupplierUnavailable");
        scenario.OrderLookups("ZC").ShouldBe(lookups, "a 503 means the airline did nothing; there is nothing to look up");
        (await scenario.RowsForAsync(item)).ShouldBe(0);
    }

    [Fact]
    public async Task Passengers_that_do_not_fit_the_offer_are_rejected_before_the_airline_is_asked()
    {
        PricedOffer offer = await scenario.PriceAsync("ZC");
        DateOnly born = DateOnly.FromDateTime(DateTime.UtcNow).AddYears(-3);
        PassengerDetails[][] unfit =
        [
            [new(PassengerType.Adult, "Zeynebnur", "Karabulutoglu", born)],                           // a toddler as an adult
            [new(PassengerType.Adult, "Zeynebnur", "Karabulutoglu", new DateOnly(1987, 5, 17)), Scenario.Zeynebnur],   // two for a one passenger offer
            [new(PassengerType.Adult, "Zeynebnur 7", "Karabulutoglu", new DateOnly(1987, 5, 17))],    // not a name
            [new(PassengerType.Child, "Zeynebnur", "Karabulutoglu", new DateOnly(1987, 5, 17))],      // wrong type for the offer
        ];
        int posts = scenario.OrderPosts("ZC");

        foreach (PassengerDetails[] passengers in unfit)
        {
            Guid trip = Guid.CreateVersion7();
            await flights.Booking.SendAsync(scenario.RequestFor(offer, trip, Guid.CreateVersion7(), passengers));

            (await scenario.NextAsync(nameof(FlightOrderRejected), trip)).DataString("reason").ShouldBe("InvalidPassengers");
        }

        scenario.OrderPosts("ZC").ShouldBe(posts);
    }

    [Fact]
    public async Task A_passenger_list_copied_from_another_trips_event_does_not_authenticate()
    {
        PricedOffer offer = await scenario.PriceAsync("ZC");
        Guid trip = Guid.CreateVersion7();
        Guid item = Guid.CreateVersion7();
        FlightOrderRequested real = scenario.RequestFor(offer, Guid.CreateVersion7(), Guid.CreateVersion7());

        // The ciphertext of another trip's event, replayed for this one.
        await flights.Booking.SendAsync(real with { TripId = trip, ItemId = item });

        (await scenario.NextAsync(nameof(FlightOrderRejected), trip)).DataString("reason").ShouldBe("InvalidPassengers");
        (await scenario.RowsForAsync(item)).ShouldBe(0);
    }

    [Fact]
    public async Task Something_that_is_not_a_sealed_list_at_all_is_rejected_too()
    {
        PricedOffer offer = await scenario.PriceAsync("ZC");
        Guid trip = Guid.CreateVersion7();

        await flights.Booking.SendAsync(scenario.RequestFor(offer, trip, Guid.CreateVersion7()) with { EncryptedPassengers = "bm90IHNlYWxlZA" });

        (await scenario.NextAsync(nameof(FlightOrderRejected), trip)).DataString("reason").ShouldBe("InvalidPassengers");
    }

    [Fact]
    public async Task What_the_service_publishes_carries_no_passenger_data()
    {
        PlacedOrder order = await scenario.OrderAsync("ZC");
        await flights.Booking.SendAsync(new FlightTicketingRequested(order.TripId, order.ItemId, order.OrderId));
        await scenario.NextAsync(nameof(FlightTicketed), order.TripId);

        foreach (ProbedMessage message in flights.Probe.Received)
        {
            string raw = System.Text.Encoding.UTF8.GetString(message.Value);
            raw.ShouldNotContain("Zeynebnur");
            raw.ShouldNotContain("Karabulutoglu");
            raw.ShouldNotContain("TR8675309X");
        }
    }
}
