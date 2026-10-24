using System.Diagnostics;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Viatica.Contracts;
using Viatica.Flights.Application.Suppliers;
using Viatica.Flights.Domain;
using Viatica.Kernel;

namespace Viatica.Flights.Application.Orders;

// Ticket, void and cancel (FLT-4). Each one is idempotent twice over: the consumer's inbox drops the same event id,
// and the order's own state answers a second request, under a new event id, with what the first one did. The
// order is looked up by id and by trip and item together, so an event that names someone else's order finds
// nothing.

internal static class OrderLookup
{
    public static Task<FlightOrder?> FindAsync(
        this IFlightsDb db, Guid orderId, Guid tripId, Guid itemId, CancellationToken cancellationToken) =>
        db.Orders.FirstOrDefaultAsync(o => o.Id == orderId && o.TripId == tripId && o.ItemId == itemId, cancellationToken);
}

/// <summary>
/// Issue the tickets. The airline may refuse (<see cref="FlightTicketingRefused"/>), and that is a normal answer the
/// saga expects. An airline that cannot be reached is answered as a refusal too: the saga then voids the order, and
/// a void works whether or not the tickets were really issued, so a guess cannot leave a ticket behind.
/// </summary>
public sealed class FlightTicketingRequestedHandler(
    IFlightsDb db,
    IAirlineOrdering airlines,
    TimeProvider time,
    ILogger<FlightTicketingRequestedHandler> logger) : IIntegrationEventHandler<FlightTicketingRequested>
{
    public async Task HandleAsync(FlightTicketingRequested message, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(message);
        using Activity? activity = FlightsTelemetry.Source.StartActivity("flights.order.ticket");

        FlightOrder? order = await db.FindAsync(message.OrderId, message.TripId, message.ItemId, cancellationToken);
        if (order is null)
        {
            OrderLog.UnknownOrder(logger, nameof(FlightTicketingRequested), message.OrderId, message.ItemId);
            Refuse(message.OrderId, message.TripId, message.ItemId, "UnknownOrder", "unknown");
            return;
        }

        switch (order.Status)
        {
            case FlightOrderStatus.Ticketed:
                db.Publish(OrderEvents.Ticketed(order));
                return;
            case FlightOrderStatus.Voided or FlightOrderStatus.Cancelled:
                Refuse(order.Id, order.TripId, order.ItemId, $"Order{order.Status}", "closed");
                return;
        }

        TicketingOutcome outcome = await airlines.TicketAsync(order.AirlineCode, order.BookingReference, cancellationToken);
        switch (outcome)
        {
            case TicketsIssued issued:
                order.Ticket(issued.TicketNumbers, time.GetUtcNow());
                db.Publish(OrderEvents.Ticketed(order));
                Count("ticketed");
                OrderLog.Ticketed(logger, order.Id);
                break;

            case TicketsRefused refused:
                order.RecordTicketingRefusal(refused.Reason, time.GetUtcNow());
                Refuse(order.Id, order.TripId, order.ItemId, refused.Reason, "refused");
                break;

            case SupplierFailure:
                Refuse(order.Id, order.TripId, order.ItemId, "SupplierUnavailable", "unavailable");
                break;

            default:
                throw new UnreachableException("A ticketing outcome was never set.");
        }
    }

    private void Refuse(Guid orderId, Guid tripId, Guid itemId, string reason, string outcome)
    {
        db.Publish(new FlightTicketingRefused(orderId, tripId, itemId, reason));
        Count(outcome);
        OrderLog.TicketingRefused(logger, orderId, outcome);
    }

    private static void Count(string outcome) =>
        FlightsTelemetry.OrderOperations.Add(1,
            new KeyValuePair<string, object?>("operation", "ticket"),
            new KeyValuePair<string, object?>("outcome", outcome));
}

/// <summary>
/// Release an unticketed order, or void a ticketed one inside the airline's void window. Past the window the order
/// is cancelled under the fare rules instead (FLT-4), and the answer is <see cref="FlightOrderCancelled"/> with the
/// refund, because that is what happened.
/// </summary>
public sealed class FlightOrderVoidRequestedHandler(
    IFlightsDb db,
    IAirlineOrdering airlines,
    TimeProvider time,
    ILogger<FlightOrderVoidRequestedHandler> logger) : IIntegrationEventHandler<FlightOrderVoidRequested>
{
    public async Task HandleAsync(FlightOrderVoidRequested message, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(message);
        using Activity? activity = FlightsTelemetry.Source.StartActivity("flights.order.void");

        FlightOrder? order = await db.FindAsync(message.OrderId, message.TripId, message.ItemId, cancellationToken);
        if (order is null)
        {
            // Nothing was ordered here, so nothing is left to release. That is a void, and the saga is waiting to
            // hear it.
            OrderLog.UnknownOrder(logger, nameof(FlightOrderVoidRequested), message.OrderId, message.ItemId);
            db.Publish(new FlightOrderVoided(message.OrderId, message.TripId, message.ItemId));
            return;
        }

        switch (order.Status)
        {
            case FlightOrderStatus.Voided:
                OrderLog.NothingToDo(logger, nameof(FlightOrderVoidRequested), order.Id, order.Status);
                db.Publish(OrderEvents.Voided(order));
                return;
            case FlightOrderStatus.Cancelled:
                OrderLog.NothingToDo(logger, nameof(FlightOrderVoidRequested), order.Id, order.Status);
                db.Publish(OrderEvents.Cancelled(order));
                return;
        }

        VoidOutcome outcome = await airlines.VoidAsync(order.AirlineCode, order.BookingReference, cancellationToken);
        switch (outcome)
        {
            case OrderVoided:
                MarkVoided(order, "voided");
                break;

            case OrderGone:
                OrderLog.AirlineHasNoOrder(logger, order.Id);
                MarkVoided(order, "gone");
                break;

            case VoidWindowClosed:
                OrderLog.VoidWindowClosed(logger, order.Id);
                await CancelInsteadAsync(order, cancellationToken);
                break;

            case SupplierFailure:
                throw new SupplierUnavailableException("a void");

            default:
                throw new UnreachableException("A void outcome was never set.");
        }
    }

    private void MarkVoided(FlightOrder order, string outcome)
    {
        order.Void(time.GetUtcNow());
        db.Publish(OrderEvents.Voided(order));
        FlightsTelemetry.OrderOperations.Add(1,
            new KeyValuePair<string, object?>("operation", "void"),
            new KeyValuePair<string, object?>("outcome", outcome));
        OrderLog.Voided(logger, order.Id);
    }

    private async Task CancelInsteadAsync(FlightOrder order, CancellationToken cancellationToken)
    {
        CancelOutcome outcome = await airlines.CancelAsync(order.AirlineCode, order.BookingReference, cancellationToken);
        switch (outcome)
        {
            case OrderCancelled cancelled:
                // Our record may still say Booked if an earlier ticketing answer was lost; the airline's word wins.
                order.Ticket([], time.GetUtcNow());
                order.Cancel(cancelled.Refund, time.GetUtcNow());
                db.Publish(OrderEvents.Cancelled(order));
                FlightsTelemetry.OrderOperations.Add(1,
                    new KeyValuePair<string, object?>("operation", "void"),
                    new KeyValuePair<string, object?>("outcome", "cancelled_instead"));
                OrderLog.Cancelled(logger, order.Id);
                break;

            case NotTicketed or OrderGone or SupplierFailure:
                throw new SupplierUnavailableException("a cancel after the void window closed");

            default:
                throw new UnreachableException("A cancel outcome was never set.");
        }
    }
}

/// <summary>
/// Cancel a ticketed order under the fare rules and report the refund. An order that was never ticketed has nothing
/// to refund and is voided instead; the answer then is <see cref="FlightOrderVoided"/>.
/// </summary>
public sealed class FlightOrderCancelRequestedHandler(
    IFlightsDb db,
    IAirlineOrdering airlines,
    TimeProvider time,
    ILogger<FlightOrderCancelRequestedHandler> logger) : IIntegrationEventHandler<FlightOrderCancelRequested>
{
    public async Task HandleAsync(FlightOrderCancelRequested message, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(message);
        using Activity? activity = FlightsTelemetry.Source.StartActivity("flights.order.cancel");

        FlightOrder? order = await db.FindAsync(message.OrderId, message.TripId, message.ItemId, cancellationToken);
        if (order is null)
        {
            // Unlike a void, a cancel carries a refund, and a refund is not something to invent for an order this
            // service has no record of. The error is logged and the saga's deadline deals with the item.
            OrderLog.UnknownOrder(logger, nameof(FlightOrderCancelRequested), message.OrderId, message.ItemId);
            return;
        }

        switch (order.Status)
        {
            case FlightOrderStatus.Cancelled:
                OrderLog.NothingToDo(logger, nameof(FlightOrderCancelRequested), order.Id, order.Status);
                db.Publish(OrderEvents.Cancelled(order));
                return;
            case FlightOrderStatus.Voided:
                OrderLog.NothingToDo(logger, nameof(FlightOrderCancelRequested), order.Id, order.Status);
                db.Publish(OrderEvents.Voided(order));
                return;
            case FlightOrderStatus.Booked:
                await VoidInsteadAsync(order, cancellationToken);
                return;
        }

        CancelOutcome outcome = await airlines.CancelAsync(order.AirlineCode, order.BookingReference, cancellationToken);
        switch (outcome)
        {
            case OrderCancelled cancelled:
                order.Cancel(cancelled.Refund, time.GetUtcNow());
                db.Publish(OrderEvents.Cancelled(order));
                Count("cancelled");
                OrderLog.Cancelled(logger, order.Id);
                break;

            case OrderGone:
                OrderLog.AirlineHasNoOrder(logger, order.Id);
                throw new SupplierUnavailableException("a cancel of an order the airline no longer shows");

            case NotTicketed:
                // Our record says ticketed and the airline says not. One of them is wrong and a retry will not fix
                // it, so the message goes to a person through the dead letter topic.
                throw new InvalidOperationException("The airline reports the order as not ticketed, but this service has it ticketed.");

            case SupplierFailure:
                throw new SupplierUnavailableException("a cancel");

            default:
                throw new UnreachableException("A cancel outcome was never set.");
        }
    }

    private async Task VoidInsteadAsync(FlightOrder order, CancellationToken cancellationToken)
    {
        VoidOutcome outcome = await airlines.VoidAsync(order.AirlineCode, order.BookingReference, cancellationToken);
        switch (outcome)
        {
            case OrderVoided or OrderGone:
                order.Void(time.GetUtcNow());
                db.Publish(OrderEvents.Voided(order));
                Count("voided_instead");
                OrderLog.Voided(logger, order.Id);
                break;

            case VoidWindowClosed or SupplierFailure:
                throw new SupplierUnavailableException("a void in place of a cancel");

            default:
                throw new UnreachableException("A void outcome was never set.");
        }
    }

    private static void Count(string outcome) =>
        FlightsTelemetry.OrderOperations.Add(1,
            new KeyValuePair<string, object?>("operation", "cancel"),
            new KeyValuePair<string, object?>("outcome", outcome));
}
