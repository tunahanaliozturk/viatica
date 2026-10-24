using Viatica.AirlineSim.Network;
using Viatica.AirlineSim.Offers;
using Viatica.AirlineSim.Orders;

namespace Viatica.AirlineSim.Api;

internal static class Views
{
    public static OfferView Offer(OfferToken token, IEnumerable<Leg> legs, FareClass fareClass, int seatsLeft) =>
        new(
            token.Encode(),
            token.Expiry,
            fareClass.Code.ToString(),
            fareClass.Cabin,
            fareClass.Refundable,
            PriceView.From(token.Price),
            seatsLeft,
            [.. legs.Select(Segment)]);

    public static SegmentView Segment(Leg leg) =>
        new(
            leg.Flight.Number,
            leg.Flight.Origin.Code,
            leg.Flight.Destination.Code,
            leg.Flight.DepartureLocal(leg.Date),
            leg.Flight.ArrivalLocal(leg.Date),
            leg.Flight.DurationMinutes,
            leg.Flight.Aircraft);

    public static OrderView Order(Order order, RouteNetwork network) =>
        new(
            order.BookingReference,
            order.IdempotencyKey,
            order.Status,
            order.FareClass.ToString(),
            new PassengerMix(order.Adults, order.Children, order.Infants),
            PriceView.From(new Price(order.BaseAmount, order.TaxAmount)),
            order.CreatedAt,
            order.TicketingTimeLimit,
            order.TicketedAt,
            order.TicketNumbers,
            order.VoidedAt,
            order.CancelledAt,
            order.RefundAmount,
            [.. order.Legs.Select(key => Leg.Parse(key, order.Airline, network)!.Value).Select(Segment)]);
}
