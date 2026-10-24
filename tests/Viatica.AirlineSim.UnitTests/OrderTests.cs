using Viatica.AirlineSim.Network;
using Viatica.AirlineSim.Orders;

namespace Viatica.AirlineSim.UnitTests;

public sealed class OrderTests
{
    private static readonly DateTimeOffset Now = new(2026, 11, 2, 8, 0, 0, TimeSpan.Zero);
    private static readonly RouteNetwork Network = new(20261024);

    [Fact]
    public void A_booked_order_can_be_voided_at_any_time()
    {
        Order order = Book('K');

        order.Void(Now.AddDays(5)).ShouldBeNull();
        order.Status.ShouldBe(OrderStatus.Voided);
    }

    [Fact]
    public void A_ticketed_order_can_be_voided_inside_the_void_window()
    {
        Order order = Ticketed('K');

        order.Void(Now + Order.VoidWindow).ShouldBeNull();
        order.Status.ShouldBe(OrderStatus.Voided);
    }

    [Fact]
    public void A_ticketed_order_cannot_be_voided_after_the_void_window()
    {
        Order order = Ticketed('K');

        order.Void(Now + Order.VoidWindow + TimeSpan.FromSeconds(1)).ShouldBe(SupplierError.VoidWindowClosed);
        order.Status.ShouldBe(OrderStatus.Ticketed);
    }

    [Fact]
    public void A_refundable_fare_refunds_the_total_less_the_fee_per_seated_passenger()
    {
        Order order = Ticketed('Y', new PassengerMix(2, 0, 1));

        order.Cancel(Now.AddDays(3)).ShouldBeNull();

        order.Status.ShouldBe(OrderStatus.Cancelled);
        order.RefundAmount.ShouldBe(order.TotalAmount - (2 * FareClass.CancellationFeePerPassenger));
    }

    [Fact]
    public void A_non_refundable_fare_refunds_the_taxes_only()
    {
        Order order = Ticketed('K');

        order.Cancel(Now.AddDays(3)).ShouldBeNull();

        order.RefundAmount.ShouldBe(order.TaxAmount);
    }

    [Fact]
    public void The_cancellation_fee_never_makes_a_refund_negative()
    {
        FareClass.Find('Y')!.Refund(total: 100m, taxes: 30m, seatedPassengers: 2).ShouldBe(0m);
    }

    [Fact]
    public void A_booked_order_is_voided_not_cancelled()
    {
        Book('Y').Cancel(Now).ShouldBe(SupplierError.OrderNotTicketed);
    }

    [Fact]
    public void Transitions_are_idempotent_and_closed_orders_stay_closed()
    {
        Order voided = Book('K');
        voided.Void(Now).ShouldBeNull();
        voided.Void(Now).ShouldBeNull();
        voided.Ticket(Now, () => false, "901").ShouldBe(SupplierError.OrderClosed(OrderStatus.Voided));
        voided.Cancel(Now).ShouldBe(SupplierError.OrderClosed(OrderStatus.Voided));

        Order ticketed = Ticketed('K');
        string[] tickets = ticketed.TicketNumbers;
        ticketed.Ticket(Now, () => true, "901").ShouldBeNull();
        ticketed.TicketNumbers.ShouldBe(tickets);
    }

    [Fact]
    public void A_refusal_is_drawn_once_and_then_remembered()
    {
        Order order = Book('K');
        int draws = 0;

        order.Ticket(Now, () => { draws++; return true; }, "901").ShouldBe(SupplierError.TicketingRefused);
        order.Ticket(Now, () => { draws++; return false; }, "901").ShouldBe(SupplierError.TicketingRefused);

        draws.ShouldBe(1);
        order.Status.ShouldBe(OrderStatus.Booked);
    }

    [Fact]
    public void Ticketing_after_the_time_limit_is_refused()
    {
        Order order = Book('K');

        order.Ticket(order.TicketingTimeLimit.AddSeconds(1), () => false, "901").ShouldBe(SupplierError.TicketingTimeLimitExpired);
    }

    [Fact]
    public void Ticketing_issues_one_ticket_per_passenger_with_the_airline_prefix()
    {
        Order order = Ticketed('K', new PassengerMix(2, 1, 1));

        order.TicketNumbers.Length.ShouldBe(4);
        order.TicketNumbers.ShouldAllBe(number => number.Length == 13 && number.StartsWith("901", StringComparison.Ordinal));
    }

    [Fact]
    public void The_ticketing_time_limit_is_a_day_or_an_hour_before_departure_whichever_comes_first()
    {
        var farAway = new Leg(Network.Between("ZC", "IST", "LHR")[0], new DateOnly(2026, 12, 15));
        DateOnly today = DateOnly.FromDateTime(Now.UtcDateTime);
        Leg laterToday = Network.Flights
            .Select(flight => new Leg(flight, today))
            .First(leg => leg.Flight.DepartureUtc(today) > Now.AddHours(3) && leg.Flight.DepartureUtc(today) < Now.AddHours(12));

        Order.Book("ZC", "k1", "o", 'K', [farAway], new PassengerMix(1, 0, 0), new Price(100, 20), Now)
            .TicketingTimeLimit.ShouldBe(Now + Order.TicketingWindow);
        Order.Book("ZC", "k2", "o", 'K', [laterToday], new PassengerMix(1, 0, 0), new Price(100, 20), Now)
            .TicketingTimeLimit.ShouldBe(laterToday.Flight.DepartureUtc(today).AddHours(-1));
    }

    private static Order Book(char fareClass, PassengerMix? mix = null)
    {
        mix ??= new PassengerMix(1, 0, 0);
        Leg[] legs = [new Leg(Network.Between("ZC", "IST", "LHR")[0], new DateOnly(2026, 12, 15))];
        Price price = RouteNetwork.PriceOf(legs, FareClass.Find(fareClass)!, mix);
        return Order.Book("ZC", Guid.NewGuid().ToString(), "offer", fareClass, legs, mix, price, Now);
    }

    private static Order Ticketed(char fareClass, PassengerMix? mix = null)
    {
        Order order = Book(fareClass, mix);
        order.Ticket(Now, () => false, "901").ShouldBeNull();
        return order;
    }
}
