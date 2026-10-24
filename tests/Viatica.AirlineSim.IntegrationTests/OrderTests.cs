using System.Net;
using Microsoft.EntityFrameworkCore;
using Viatica.AirlineSim.Api;
using Viatica.AirlineSim.Network;
using Viatica.AirlineSim.Orders;

namespace Viatica.AirlineSim.IntegrationTests;

public sealed class OrderTests(Postgres postgres)
{
    [Fact]
    public async Task Concurrent_orders_for_the_last_seats_of_a_bucket_never_oversell()
    {
        await using SimHost sim = await SimHost.StartAsync(postgres);
        OfferView offer = await sim.Client.OfferAsync("ZC", "IST", "LHR", 'K');
        string flight = offer.Segments[0].FlightNumber;
        await sim.SetSeatsAsync(flight, SimHost.Outbound, 'K', 3);

        HttpResponseMessage[] responses = await Task.WhenAll(
            Enumerable.Range(0, 40).Select(_ => sim.Client.CreateOrderAsync("ZC", offer.OfferId, Guid.NewGuid().ToString())));

        responses.Count(r => r.StatusCode == HttpStatusCode.Created).ShouldBe(3);
        responses.Count(r => r.StatusCode == HttpStatusCode.Conflict).ShouldBe(37);
        (await sim.SeatsLeftAsync(flight, SimHost.Outbound, 'K')).ShouldBe(0);
        (await sim.InDbAsync(db => db.Orders.CountAsync())).ShouldBe(3);
        foreach (HttpResponseMessage response in responses)
        {
            response.Dispose();
        }
    }

    [Fact]
    public async Task A_return_order_takes_seats_on_both_legs_or_on_neither()
    {
        await using SimHost sim = await SimHost.StartAsync(postgres);
        OfferView offer = await sim.Client.OfferAsync("ZC", "IST", "LHR", 'K', SimHost.Inbound, adults: 2);
        (string away, string back) = (offer.Segments[0].FlightNumber, offer.Segments[1].FlightNumber);
        int awayCapacity = new RouteNetwork(SimHost.DefaultSeed).Find(away)!.Bucket('K')!.Seats;
        await sim.SetSeatsAsync(back, SimHost.Inbound, 'K', 1);

        using HttpResponseMessage response = await sim.Client.CreateOrderAsync("ZC", offer.OfferId, "return-1", adults: 2);

        response.StatusCode.ShouldBe(HttpStatusCode.Conflict);
        (await sim.InDbAsync(db => db.SeatBuckets.CountAsync(b => b.FlightNumber == away && b.Remaining != awayCapacity))).ShouldBe(0);
        (await sim.SeatsLeftAsync(back, SimHost.Inbound, 'K')).ShouldBe(1);
    }

    [Fact]
    public async Task Creating_an_order_twice_with_one_key_returns_the_same_order()
    {
        await using SimHost sim = await SimHost.StartAsync(postgres);
        OfferView offer = await sim.Client.OfferAsync("ZC", "IST", "LHR", 'M');
        string flight = offer.Segments[0].FlightNumber;

        using HttpResponseMessage first = await sim.Client.CreateOrderAsync("ZC", offer.OfferId, "key-1");
        using HttpResponseMessage second = await sim.Client.CreateOrderAsync("ZC", offer.OfferId, "key-1");

        first.StatusCode.ShouldBe(HttpStatusCode.Created);
        second.StatusCode.ShouldBe(HttpStatusCode.OK);
        OrderView a = await first.ReadAsync(SimJson.Default.OrderView);
        OrderView b = await second.ReadAsync(SimJson.Default.OrderView);
        b.BookingReference.ShouldBe(a.BookingReference);
        int capacity = new RouteNetwork(SimHost.DefaultSeed).Find(flight)!.Bucket('M')!.Seats;
        (await sim.SeatsLeftAsync(flight, SimHost.Outbound, 'M')).ShouldBe(capacity - 1);
    }

    [Fact]
    public async Task Concurrent_creations_with_one_key_make_one_order()
    {
        await using SimHost sim = await SimHost.StartAsync(postgres);
        OfferView offer = await sim.Client.OfferAsync("ZC", "IST", "LHR", 'M');

        HttpResponseMessage[] responses = await Task.WhenAll(
            Enumerable.Range(0, 12).Select(_ => sim.Client.CreateOrderAsync("ZC", offer.OfferId, "same-key")));
        OrderView[] orders = await Task.WhenAll(responses.Select(r => r.ReadAsync(SimJson.Default.OrderView)));

        orders.Select(o => o.BookingReference).Distinct().Count().ShouldBe(1);
        (await sim.InDbAsync(db => db.Orders.CountAsync())).ShouldBe(1);
        foreach (HttpResponseMessage response in responses)
        {
            response.Dispose();
        }
    }

    [Fact]
    public async Task A_key_reused_for_another_offer_is_refused()
    {
        await using SimHost sim = await SimHost.StartAsync(postgres);
        OfferView first = await sim.Client.OfferAsync("ZC", "IST", "LHR", 'K');
        OfferView other = await sim.Client.OfferAsync("ZC", "IST", "LHR", 'M');

        (await sim.Client.CreateOrderAsync("ZC", first.OfferId, "key-2")).StatusCode.ShouldBe(HttpStatusCode.Created);
        using HttpResponseMessage reused = await sim.Client.CreateOrderAsync("ZC", other.OfferId, "key-2");

        reused.StatusCode.ShouldBe(HttpStatusCode.Conflict);
        (await reused.ErrorCodeAsync()).ShouldBe("IDEMPOTENCY_KEY_REUSED");
    }

    [Fact]
    public async Task An_order_is_found_by_booking_reference_and_by_idempotency_key()
    {
        await using SimHost sim = await SimHost.StartAsync(postgres);
        OfferView offer = await sim.Client.OfferAsync("JG", "MAD", "LHR", 'K');
        using HttpResponseMessage created = await sim.Client.CreateOrderAsync("JG", offer.OfferId, "find-me");
        OrderView order = await created.ReadAsync(SimJson.Default.OrderView);

        OrderView byReference = await (await sim.Client.GetAsync($"/ndc/JG/orders/{order.BookingReference}")).ReadAsync(SimJson.Default.OrderView);
        OrderView byKey = await (await sim.Client.GetAsync("/ndc/JG/orders?idempotencyKey=find-me")).ReadAsync(SimJson.Default.OrderView);
        using HttpResponseMessage otherAirline = await sim.Client.GetAsync($"/ndc/ZC/orders/{order.BookingReference}");

        byReference.BookingReference.ShouldBe(order.BookingReference);
        byKey.BookingReference.ShouldBe(order.BookingReference);
        byKey.Status.ShouldBe(OrderStatus.Booked);
        byKey.Price.ShouldBe(offer.Price);
        byKey.TicketingTimeLimit.ShouldBe(SimHost.Start + Order.TicketingWindow);
        otherAirline.StatusCode.ShouldBe(HttpStatusCode.NotFound);
    }

    [Fact]
    public async Task Passengers_must_match_the_offer()
    {
        await using SimHost sim = await SimHost.StartAsync(postgres);
        OfferView offer = await sim.Client.OfferAsync("ZC", "IST", "LHR", 'K', adults: 2);

        using HttpResponseMessage response = await sim.Client.CreateOrderAsync("ZC", offer.OfferId, "pax", adults: 1);

        response.StatusCode.ShouldBe(HttpStatusCode.BadRequest);
        (await sim.InDbAsync(db => db.Orders.CountAsync())).ShouldBe(0);
    }

    [Fact]
    public async Task An_expired_offer_cannot_be_ordered()
    {
        await using SimHost sim = await SimHost.StartAsync(postgres);
        OfferView offer = await sim.Client.OfferAsync("ZC", "IST", "LHR", 'K');
        sim.Clock.Advance(TimeSpan.FromMinutes(16));

        using HttpResponseMessage response = await sim.Client.CreateOrderAsync("ZC", offer.OfferId, "late");

        response.StatusCode.ShouldBe(HttpStatusCode.Gone);
    }

    [Fact]
    public async Task Voiding_before_ticketing_returns_the_seats()
    {
        await using SimHost sim = await SimHost.StartAsync(postgres);
        OfferView offer = await sim.Client.OfferAsync("ZC", "IST", "LHR", 'K', adults: 2);
        string flight = offer.Segments[0].FlightNumber;
        OrderView order = await sim.Client.BookAsync("ZC", offer, adults: 2);
        int afterBooking = await sim.SeatsLeftAsync(flight, SimHost.Outbound, 'K');

        using HttpResponseMessage voided = await sim.Client.OrderActionAsync("ZC", order.BookingReference, "void");
        using HttpResponseMessage again = await sim.Client.OrderActionAsync("ZC", order.BookingReference, "void");

        voided.StatusCode.ShouldBe(HttpStatusCode.OK);
        again.StatusCode.ShouldBe(HttpStatusCode.OK);
        (await again.ReadAsync(SimJson.Default.OrderView)).Status.ShouldBe(OrderStatus.Voided);
        (await sim.SeatsLeftAsync(flight, SimHost.Outbound, 'K')).ShouldBe(afterBooking + 2);
    }

    [Fact]
    public async Task A_ticket_can_be_voided_inside_the_void_window_only()
    {
        await using SimHost sim = await SimHost.StartAsync(postgres);
        OfferView offer = await sim.Client.OfferAsync("ZC", "IST", "LHR", 'K');
        OrderView inside = await sim.Client.BookAsync("ZC", offer);
        OrderView outside = await sim.Client.BookAsync("ZC", offer);
        (await sim.Client.OrderActionAsync("ZC", inside.BookingReference, "ticket")).StatusCode.ShouldBe(HttpStatusCode.OK);
        (await sim.Client.OrderActionAsync("ZC", outside.BookingReference, "ticket")).StatusCode.ShouldBe(HttpStatusCode.OK);

        sim.Clock.Advance(TimeSpan.FromHours(23));
        using HttpResponseMessage inTime = await sim.Client.OrderActionAsync("ZC", inside.BookingReference, "void");
        sim.Clock.Advance(TimeSpan.FromHours(1) + TimeSpan.FromMinutes(1));
        using HttpResponseMessage tooLate = await sim.Client.OrderActionAsync("ZC", outside.BookingReference, "void");

        inTime.StatusCode.ShouldBe(HttpStatusCode.OK);
        tooLate.StatusCode.ShouldBe(HttpStatusCode.Conflict);
        (await tooLate.ErrorCodeAsync()).ShouldBe("VOID_WINDOW_CLOSED");
    }

    [Theory]
    [InlineData('Y')]
    [InlineData('J')]
    public async Task Cancelling_a_refundable_ticket_refunds_the_total_less_the_fee(char fareClass)
    {
        await using SimHost sim = await SimHost.StartAsync(postgres);
        OfferView offer = await sim.Client.OfferAsync("ZC", "IST", "LHR", fareClass, adults: 2);
        OrderView order = await sim.Client.BookAsync("ZC", offer, adults: 2);
        (await sim.Client.OrderActionAsync("ZC", order.BookingReference, "ticket")).StatusCode.ShouldBe(HttpStatusCode.OK);
        sim.Clock.Advance(TimeSpan.FromDays(2));

        using HttpResponseMessage response = await sim.Client.OrderActionAsync("ZC", order.BookingReference, "cancel");
        OrderView cancelled = await response.ReadAsync(SimJson.Default.OrderView);

        cancelled.Status.ShouldBe(OrderStatus.Cancelled);
        cancelled.RefundAmount.ShouldBe(offer.Price.Total - (2 * FareClass.CancellationFeePerPassenger));
    }

    [Theory]
    [InlineData('K')]
    [InlineData('M')]
    public async Task Cancelling_a_non_refundable_ticket_refunds_the_taxes_and_returns_the_seat(char fareClass)
    {
        await using SimHost sim = await SimHost.StartAsync(postgres);
        OfferView offer = await sim.Client.OfferAsync("ZC", "IST", "LHR", fareClass);
        string flight = offer.Segments[0].FlightNumber;
        OrderView order = await sim.Client.BookAsync("ZC", offer);
        (await sim.Client.OrderActionAsync("ZC", order.BookingReference, "ticket")).StatusCode.ShouldBe(HttpStatusCode.OK);
        int afterTicketing = await sim.SeatsLeftAsync(flight, SimHost.Outbound, fareClass);
        sim.Clock.Advance(TimeSpan.FromDays(2));

        using HttpResponseMessage response = await sim.Client.OrderActionAsync("ZC", order.BookingReference, "cancel");

        (await response.ReadAsync(SimJson.Default.OrderView)).RefundAmount.ShouldBe(offer.Price.Taxes);
        (await sim.SeatsLeftAsync(flight, SimHost.Outbound, fareClass)).ShouldBe(afterTicketing + 1);
    }

    [Fact]
    public async Task Ticketing_after_the_time_limit_is_refused()
    {
        await using SimHost sim = await SimHost.StartAsync(postgres);
        OrderView order = await sim.Client.BookAsync("ZC", await sim.Client.OfferAsync("ZC", "IST", "LHR", 'K'));
        sim.Clock.Advance(Order.TicketingWindow + TimeSpan.FromMinutes(1));

        using HttpResponseMessage response = await sim.Client.OrderActionAsync("ZC", order.BookingReference, "ticket");

        response.StatusCode.ShouldBe(HttpStatusCode.Conflict);
        (await response.ErrorCodeAsync()).ShouldBe("TICKETING_TIME_LIMIT_EXPIRED");
    }
}
