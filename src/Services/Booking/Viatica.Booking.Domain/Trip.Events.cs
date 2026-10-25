using Viatica.Kernel;

namespace Viatica.Booking.Domain;

// What each owner's answer means. Every method is a function of the trip's state and the answer, nothing else, and
// answers arrive late, twice, and out of order (docs/messaging.md). So each one asks "is this the answer I am waiting
// for?" and, if the trip has moved on, either ignores it or gives back what it carries: a hold that arrives after the
// trip failed is released, an order is voided, an authorisation is voided. Nothing a failed trip asked for stays held.

public sealed partial class Trip
{
    // Stays and cars.

    public IReadOnlyList<SagaDirective> OnHoldPlaced(
        Guid itemId, Guid holdId, Money total, DateTimeOffset expiresAt, ItemTerms terms, DateTimeOffset now)
    {
        ArgumentNullException.ThrowIfNull(total);
        ArgumentNullException.ThrowIfNull(terms);

        if (HoldItem(itemId, holdId) is not { } item)
        {
            return Complete(now);
        }

        bool asked = Status == TripStatus.Holding && item.Status == ItemStatus.Pending && item.Awaiting == ItemRequest.Hold;
        if (asked)
        {
            Take(item, total, expiresAt, terms);
            item.Awaiting = ItemRequest.None;
            Move(item, ItemStatus.Held);
            Advance(now);
        }
        else if (item.Status == ItemStatus.Pending && item.Awaiting == ItemRequest.Release)
        {
            // Unwinding sent a release behind the request, but the owner may have read it before it placed this hold (a
            // request judged again after a refusal), and then the release found nothing to give back. Release once more:
            // it is idempotent, and it costs one message to be sure.
            Take(item, total, expiresAt, terms);
            Move(item, ItemStatus.Held);
            Send(new ReleaseHold(item.Kind, item.Id, item.HoldId));
            CompensationSentAt = now;
        }
        else if (item.Status is ItemStatus.Rejected or ItemStatus.Released or ItemStatus.Expired)
        {
            // A hold nobody wants: the request was redelivered after a refusal, or the answer outlived the trip.
            ReleaseLate(item, now);
        }

        return Complete(now);
    }

    public IReadOnlyList<SagaDirective> OnHoldRejected(Guid itemId, Guid holdId, string reason, DateTimeOffset now)
    {
        if (HoldItem(itemId, holdId) is { Status: ItemStatus.Pending } item)
        {
            item.Refusal = reason;
            item.Awaiting = ItemRequest.None;
            Move(item, ItemStatus.Rejected);
            Advance(now);
        }

        return Complete(now);
    }

    /// <summary>
    /// The owner's sweeper took the hold back. Before the commit that ends the checkout; during it, it is a commit
    /// that lost the race, which the flights already ticketed must be unwound for.
    /// </summary>
    public IReadOnlyList<SagaDirective> OnHoldExpired(Guid itemId, Guid holdId, DateTimeOffset now)
    {
        if (HoldItem(itemId, holdId) is { Status: ItemStatus.Pending or ItemStatus.Held } item)
        {
            item.Awaiting = ItemRequest.None;
            Move(item, ItemStatus.Expired);
            Advance(now);
        }

        return Complete(now);
    }

    public IReadOnlyList<SagaDirective> OnHoldReleased(Guid itemId, Guid holdId, DateTimeOffset now)
    {
        if (HoldItem(itemId, holdId) is { Awaiting: ItemRequest.Release } item)
        {
            item.Awaiting = ItemRequest.None;
            if (item.Status is ItemStatus.Pending or ItemStatus.Held)
            {
                Move(item, ItemStatus.Released);
            }

            changed = true;
            Advance(now);
        }

        return Complete(now);
    }

    public IReadOnlyList<SagaDirective> OnHoldCommitted(Guid itemId, Guid holdId, DateTimeOffset now)
    {
        if (HoldItem(itemId, holdId) is { Status: ItemStatus.Held, Awaiting: ItemRequest.Commit } item)
        {
            item.Awaiting = ItemRequest.None;
            Move(item, ItemStatus.Committed);
            Advance(now);
        }

        return Complete(now);
    }

    /// <summary>A stay or car was given back: because the checkout failed after it was committed, or because the traveller cancelled.</summary>
    public IReadOnlyList<SagaDirective> OnBookingCancelled(Guid itemId, Guid holdId, DateTimeOffset now)
    {
        if (HoldItem(itemId, holdId) is { Status: ItemStatus.Committed, Awaiting: ItemRequest.Cancel } item)
        {
            if (item.Cancel != CancelState.None)
            {
                CompleteCancellation(item, providerRefund: null, now);
            }
            else
            {
                item.Awaiting = ItemRequest.None;
                Move(item, ItemStatus.Cancelled);
                Advance(now);
            }
        }

        return Complete(now);
    }

    // Flights.

    public IReadOnlyList<SagaDirective> OnFlightOrderCreated(
        Guid itemId, Guid orderId, Money total, DateTimeOffset ticketingTimeLimit, DateTimeOffset now)
    {
        ArgumentNullException.ThrowIfNull(total);

        if (FlightItem(itemId) is not { } item || item.OrderId is { } known && known != orderId)
        {
            return Complete(now);
        }

        bool asked = Status == TripStatus.Holding && item.Status == ItemStatus.Pending && item.Awaiting == ItemRequest.Hold;
        if (asked)
        {
            item.OrderId = orderId;
            item.Record(total, ticketingTimeLimit);
            item.Awaiting = ItemRequest.None;
            Move(item, ItemStatus.Ordered);
            Advance(now);
        }
        else if (item.OrderId is null)
        {
            // The order exists and nothing wants it: the trip failed while the airline was still working, or Flights
            // created the order after a timeout we had already given up on. Void it, whatever the item says.
            item.OrderId = orderId;
            item.Record(total, ticketingTimeLimit);
            if (item.Status == ItemStatus.Pending)
            {
                Move(item, ItemStatus.Ordered);
            }

            VoidLate(item, now);
            if (Status == TripStatus.Unwinding)
            {
                Advance(now);
            }
        }

        return Complete(now);
    }

    public IReadOnlyList<SagaDirective> OnFlightOrderRejected(Guid itemId, string reason, DateTimeOffset now)
    {
        if (FlightItem(itemId) is { Status: ItemStatus.Pending, OrderId: null } item)
        {
            item.Refusal = reason;
            item.Awaiting = ItemRequest.None;
            Move(item, ItemStatus.Rejected);
            Advance(now);
        }

        return Complete(now);
    }

    public IReadOnlyList<SagaDirective> OnFlightTicketed(Guid itemId, Guid orderId, DateTimeOffset now)
    {
        if (FlightItem(itemId) is { Status: ItemStatus.Ordered } item && item.OrderId == orderId)
        {
            if (item.Awaiting == ItemRequest.Ticketing)
            {
                item.Awaiting = ItemRequest.None;
            }

            item.Refusal = null;
            Move(item, ItemStatus.Ticketed);
            Advance(now);
        }

        return Complete(now);
    }

    /// <summary>
    /// The airline would not ticket (or could not be asked). The order is still booked: it is voided with the rest.
    /// An answer to a ticketing request we no longer wait for, because the trip already unwound, is ignored; the
    /// void that was sent covers it.
    /// </summary>
    public IReadOnlyList<SagaDirective> OnFlightTicketingRefused(Guid itemId, Guid orderId, string reason, DateTimeOffset now)
    {
        if (FlightItem(itemId) is { Status: ItemStatus.Ordered, Awaiting: ItemRequest.Ticketing } item && item.OrderId == orderId)
        {
            item.Awaiting = ItemRequest.None;
            item.Refusal = reason;
            changed = true;
            Advance(now);
        }

        return Complete(now);
    }

    public IReadOnlyList<SagaDirective> OnFlightOrderVoided(Guid itemId, Guid orderId, DateTimeOffset now)
    {
        if (FlightItem(itemId) is { Awaiting: ItemRequest.Void } item && item.OrderId == orderId)
        {
            item.Awaiting = ItemRequest.None;
            Move(item, ItemStatus.Voided);
            changed = true;
            Advance(now);
        }
        else if (FlightItem(itemId) is { Awaiting: ItemRequest.Cancel, Status: ItemStatus.Ticketed } cancelling && cancelling.OrderId == orderId)
        {
            // Asked to cancel an order that was never ticketed: Flights voids it instead and there is nothing to refund.
            CompleteCancellation(cancelling, Money.Zero(cancelling.Currency), now);
        }

        return Complete(now);
    }

    /// <summary>
    /// The airline cancelled the order under its fare rules and says what it refunds. That answers a traveller's
    /// cancellation, and also a void request that came after the airline's void window closed (docs/services/flights.md).
    /// </summary>
    public IReadOnlyList<SagaDirective> OnFlightOrderCancelled(Guid itemId, Guid orderId, Money refund, DateTimeOffset now)
    {
        ArgumentNullException.ThrowIfNull(refund);

        if (FlightItem(itemId) is not { } item || item.OrderId != orderId)
        {
            return Complete(now);
        }

        if (item.Awaiting == ItemRequest.Void)
        {
            // The trip failed and the airline could only cancel. What it refunds the platform is the platform's loss;
            // the traveller was never charged, so there is no payment refund to make.
            item.Awaiting = ItemRequest.None;
            Move(item, ItemStatus.Cancelled);
            changed = true;
            Advance(now);
        }
        else if (item.Awaiting == ItemRequest.Cancel && item.Status == ItemStatus.Ticketed)
        {
            CompleteCancellation(item, refund, now);
        }

        return Complete(now);
    }

    // Payments.

    public IReadOnlyList<SagaDirective> OnPaymentAuthorized(Guid paymentId, DateTimeOffset now)
    {
        if (paymentId != PaymentId)
        {
            return Complete(now);
        }

        bool unwanted = Status is TripStatus.Unwinding or TripStatus.Failed;
        switch (Payment)
        {
            case PaymentStatus.AuthorizationRequested when !unwanted:
                Payment = PaymentStatus.Authorized;
                changed = true;
                Advance(now);
                break;

            case PaymentStatus.AuthorizationRequested or PaymentStatus.None or PaymentStatus.Declined when unwanted:
                // Granted after the trip gave up: the card is held for nothing until it is voided.
                Payment = PaymentStatus.VoidRequested;
                Send(new VoidPayment(PaymentId));
                CompensationSentAt = now;
                Advance(now);
                break;
        }

        return Complete(now);
    }

    public IReadOnlyList<SagaDirective> OnPaymentDeclined(Guid paymentId, DateTimeOffset now)
    {
        if (paymentId == PaymentId && Payment == PaymentStatus.AuthorizationRequested)
        {
            Payment = PaymentStatus.Declined;
            changed = true;
            Advance(now);
        }

        return Complete(now);
    }

    public IReadOnlyList<SagaDirective> OnPaymentCaptured(Guid paymentId, DateTimeOffset now)
    {
        if (paymentId == PaymentId && Payment == PaymentStatus.CaptureRequested)
        {
            Payment = PaymentStatus.Captured;
            changed = true;
            Advance(now);
        }

        return Complete(now);
    }

    public IReadOnlyList<SagaDirective> OnPaymentVoided(Guid paymentId, DateTimeOffset now)
    {
        if (paymentId == PaymentId && Payment == PaymentStatus.VoidRequested)
        {
            Payment = PaymentStatus.Voided;
            changed = true;
            Advance(now);
        }

        return Complete(now);
    }

    public IReadOnlyList<SagaDirective> OnPaymentRefunded(Guid paymentId, Guid refundId, DateTimeOffset now)
    {
        if (paymentId == PaymentId && items.Find(i => i.RefundId == refundId && i.Refund == RefundStatus.Requested) is { } item)
        {
            item.Refund = RefundStatus.Refunded;
            changed = true;
        }

        return Complete(now);
    }

    // Helpers.

    private TripItem? HoldItem(Guid itemId, Guid holdId) =>
        items.Find(i => i.Id == itemId && i.IsStayOrCar && i.HoldId == holdId);

    private TripItem? FlightItem(Guid itemId) =>
        items.Find(i => i.Id == itemId && i.Kind == ItemKind.Flight);

    private static void Take(TripItem item, Money total, DateTimeOffset expiresAt, ItemTerms terms)
    {
        item.Record(total, expiresAt);
        item.Record(terms);
    }

    /// <summary>Gives back a hold the trip has no use for. Asking twice is harmless at the owner, but the trip asks once.</summary>
    private void ReleaseLate(TripItem item, DateTimeOffset now)
    {
        if (item.Awaiting == ItemRequest.Release)
        {
            return;
        }

        item.Awaiting = ItemRequest.Release;
        Send(new ReleaseHold(item.Kind, item.Id, item.HoldId));
        CompensationSentAt = now;
    }

    private void VoidLate(TripItem item, DateTimeOffset now)
    {
        if (item.Awaiting == ItemRequest.Void || item.OrderId is not { } order)
        {
            return;
        }

        item.Awaiting = ItemRequest.Void;
        Send(new VoidFlightOrder(item.Id, order));
        CompensationSentAt = now;
    }
}
