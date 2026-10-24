using Microsoft.EntityFrameworkCore;
using Relevox;
using Viatica.Flights.Domain;
using Viatica.Kernel;

namespace Viatica.Flights.Application.Features.Orders;

/// <summary>An order as operators see it. There is no passenger data here, sealed or otherwise.</summary>
public sealed record FlightOrderView(
    Guid Id,
    Guid TripId,
    Guid ItemId,
    string Airline,
    string BookingReference,
    FlightOrderStatus Status,
    Money Total,
    DateTimeOffset TicketingTimeLimit,
    IReadOnlyList<string> TicketNumbers,
    Money? Refund,
    string? TicketingRefusal,
    DateTimeOffset CreatedAt,
    DateTimeOffset UpdatedAt,
    uint Version);

public sealed record GetFlightOrderQuery(Guid OrderId) : IRequest<FlightOrderView>;

public sealed class GetFlightOrderHandler(IFlightsDb db) : IRequestHandler<GetFlightOrderQuery, FlightOrderView>
{
    public async Task<FlightOrderView> Handle(GetFlightOrderQuery request, CancellationToken cancellationToken)
    {
        FlightOrder order = await db.Orders.AsNoTracking().FirstOrDefaultAsync(o => o.Id == request.OrderId, cancellationToken)
            ?? throw ApiErrorException.NotFound("There is no such flight order.");

        return new FlightOrderView(
            order.Id,
            order.TripId,
            order.ItemId,
            order.AirlineCode,
            order.BookingReference,
            order.Status,
            order.Total,
            order.TicketingTimeLimit,
            order.TicketNumbers,
            order.Refund,
            order.TicketingRefusal,
            order.CreatedAt,
            order.UpdatedAt,
            order.Version);
    }
}
