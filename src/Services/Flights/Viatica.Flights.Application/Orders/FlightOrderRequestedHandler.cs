using System.Diagnostics;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Viatica.Contracts;
using Viatica.Flights.Application.Offers;
using Viatica.Flights.Application.Suppliers;
using Viatica.Flights.Domain;
using Viatica.Kernel;
using Viatica.Kernel.Pii;

namespace Viatica.Flights.Application.Orders;

/// <summary>
/// Booking asks for an airline order for a re-priced offer (FLT-4, FLT-5). Ends in exactly one of
/// <see cref="FlightOrderCreated"/> or <see cref="FlightOrderRejected"/>, however often the request is delivered.
/// </summary>
/// <remarks>
/// <para>
/// The checks that need no airline come first and cost nothing: is the offer still cached, is the price what
/// Booking expected, can the passenger list be opened and does it fit the offer. Only then is the airline called,
/// once, with the item id as the idempotency key. A request redelivered under a new event id finds the order it
/// made the first time and answers again with it; a crash between the airline's answer and our commit is repaired
/// by the same key, because the airline hands back the order it already made.
/// </para>
/// <para>
/// A creation that times out is the dangerous case: the airline may have made the order. Whether it did is asked,
/// by key, before anything is decided. Only a lookup that says "no such order" turns it into a rejection. A lookup
/// that cannot be answered either throws, so the consumer retries; guessing would leave a seat held for nobody.
/// </para>
/// </remarks>
public sealed class FlightOrderRequestedHandler(
    IFlightsDb db,
    IOfferStore offers,
    IAirlineOrdering airlines,
    PassengerProtector protector,
    TimeProvider time,
    ILogger<FlightOrderRequestedHandler> logger) : IIntegrationEventHandler<FlightOrderRequested>
{
    public async Task HandleAsync(FlightOrderRequested message, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(message);
        using Activity? activity = FlightsTelemetry.Source.StartActivity("flights.order.create");

        FlightOrder? existing = await db.Orders.FirstOrDefaultAsync(o => o.ItemId == message.ItemId, cancellationToken);
        if (existing is not null)
        {
            AnswerAgain(existing);
            return;
        }

        StoredOffer? stored = await offers.GetOfferAsync(message.OfferId, cancellationToken);
        if (stored is null || stored.Offer.ExpiresAt <= time.GetUtcNow())
        {
            Reject(message, FlightRejectionReason.OfferExpired);
            return;
        }

        if (!Matches(stored.Offer.Total, message.ExpectedTotal))
        {
            Reject(message, FlightRejectionReason.PriceChanged);
            return;
        }

        if (!protector.TryOpenFromBroker(message.EncryptedPassengers, message.TripId, message.ItemId, out IReadOnlyList<PassengerDetails> passengers))
        {
            OrderLog.PassengersUnreadable(logger, message.TripId, message.ItemId);
            Reject(message, FlightRejectionReason.InvalidPassengers);
            return;
        }

        DateOnly travelDate = DateOnly.FromDateTime(stored.Offer.Segments[0].DepartureLocal);
        IReadOnlyList<PassengerProblem> problems = PassengerRules.Validate(passengers, stored.Passengers, travelDate);
        if (problems.Count > 0)
        {
            // Position, field and code only. The values that were wrong are exactly what must not be logged.
            OrderLog.PassengersRefused(logger, message.TripId, message.ItemId,
                string.Join(", ", problems.Select(static p => $"{p.Code}@{(p.Index < 0 ? "list" : p.Index.ToString(System.Globalization.CultureInfo.InvariantCulture))}.{p.Field}")));
            Reject(message, FlightRejectionReason.InvalidPassengers);
            return;
        }

        string airline = stored.Offer.Airline;
        var request = new NewSupplierOrder(stored.SupplierOfferId, FlightOrder.IdempotencyKeyFor(message.ItemId), passengers);
        CreateOrderOutcome outcome = await airlines.CreateOrderAsync(airline, request, cancellationToken);

        SupplierOrder? created = outcome switch
        {
            OrderCreated made => made.Order,
            OrderRefused refused => RejectAndNull(message, Map(refused.Reason)),
            SupplierFailure { MayHaveReachedAirline: false } => RejectAndNull(message, FlightRejectionReason.SupplierUnavailable),
            SupplierFailure => await LookUpAsync(message, airline, request.IdempotencyKey, cancellationToken),
            null => throw new UnreachableException("A create order outcome was never set."),
        };

        if (created is null)
        {
            return;
        }

        FlightOrder order = FlightOrder.Create(
            message.TripId,
            message.ItemId,
            airline,
            created.BookingReference,
            message.OfferId,
            created.Total,
            created.TicketingTimeLimit,
            protector.SealForStorage(passengers, message.ItemId),
            time.GetUtcNow());

        db.Orders.Add(order);
        db.Publish(OrderEvents.Created(order));
        FlightsTelemetry.Orders.Add(1, new KeyValuePair<string, object?>("outcome", "created"));
        OrderLog.Created(logger, order.TripId, order.ItemId, order.Id, airline);
    }

    /// <summary>The creation timed out or failed in flight. Ask the airline whether it made the order anyway.</summary>
    private async Task<SupplierOrder?> LookUpAsync(
        FlightOrderRequested message, string airline, string idempotencyKey, CancellationToken cancellationToken)
    {
        OrderLog.LookingUpAfterTimeout(logger, message.ItemId, airline);

        FindOrderOutcome found = await airlines.FindOrderAsync(airline, idempotencyKey, cancellationToken);
        switch (found)
        {
            case OrderFound hit:
                OrderLog.FoundAfterTimeout(logger, message.ItemId);
                return hit.Order;

            case NoSuchOrder:
                Reject(message, FlightRejectionReason.SupplierUnavailable);
                return null;

            case SupplierFailure:
                throw new SupplierUnavailableException("the lookup of an order after a timed out creation");

            default:
                throw new UnreachableException("A find order outcome was never set.");
        }
    }

    private SupplierOrder? RejectAndNull(FlightOrderRequested message, FlightRejectionReason reason)
    {
        Reject(message, reason);
        return null;
    }

    private void Reject(FlightOrderRequested message, FlightRejectionReason reason)
    {
        db.Publish(new FlightOrderRejected(message.TripId, message.ItemId, reason));
        FlightsTelemetry.Orders.Add(1, new KeyValuePair<string, object?>("outcome", $"rejected_{reason}"));
        OrderLog.Rejected(logger, message.TripId, message.ItemId, reason);
    }

    /// <summary>A second request for an item we already ordered. Repeat the answer; never order twice.</summary>
    private void AnswerAgain(FlightOrder existing)
    {
        OrderLog.AlreadyOrdered(logger, existing.ItemId, existing.Id, existing.Status);

        // An order that has moved on (ticketed, voided) was announced by the event that moved it. Saying "created"
        // again would suggest it can still be ticketed.
        if (existing.Status == FlightOrderStatus.Booked)
        {
            db.Publish(OrderEvents.Created(existing));
        }
    }

    private static FlightRejectionReason Map(RefusalReason reason) => reason switch
    {
        RefusalReason.SoldOut => FlightRejectionReason.SoldOut,
        RefusalReason.OfferExpired => FlightRejectionReason.OfferExpired,
        RefusalReason.InvalidPassengers => FlightRejectionReason.InvalidPassengers,
        _ => FlightRejectionReason.SupplierUnavailable,
    };

    private static bool Matches(Money offered, Price expected)
    {
        try
        {
            return offered == new Money(expected.Amount, expected.Currency);
        }
        catch (ArgumentException)
        {
            // An amount with more decimals than the currency has cannot be a price we quoted.
            return false;
        }
    }
}
