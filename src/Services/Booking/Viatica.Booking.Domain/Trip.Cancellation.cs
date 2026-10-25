using Viatica.Kernel;

namespace Viatica.Booking.Domain;

// A traveller cancels one confirmed item (BKG-4). The owner gives the item back, the refund is worked out from that
// item's own policy, and the money goes back as one payment operation per cancellation. The trip total follows.

public sealed partial class Trip
{
    /// <summary>
    /// Asks the item's owner to give it back. Asking again while that is pending, or after it is done, changes
    /// nothing. An owner that never answers (Flights will not answer a cancel for an order it never made) leaves the
    /// request pending until <paramref name="timeout"/>, then marks it timed out so the traveller can ask again.
    /// </summary>
    public IReadOnlyList<SagaDirective> RequestCancellation(Guid itemId, DateTimeOffset now, TimeSpan timeout)
    {
        TripItem item = Find(itemId) ?? throw BookingErrors.ItemNotFound();

        if (Status is TripStatus.Holding or TripStatus.Authorizing or TripStatus.Ticketing or TripStatus.Committing
            or TripStatus.Capturing or TripStatus.Unwinding)
        {
            throw BookingErrors.CheckoutInProgress();
        }

        if (Status is TripStatus.Draft or TripStatus.Failed)
        {
            throw BookingErrors.ItemNotCancellable("Only items of a confirmed trip can be cancelled.");
        }

        if (item.Status == ItemStatus.Cancelled || item.Cancel == CancelState.Requested)
        {
            return [];
        }

        if (item.Status is not (ItemStatus.Committed or ItemStatus.Ticketed))
        {
            throw BookingErrors.ItemNotCancellable("The item is not confirmed.");
        }

        if (item.IsStayOrCar && item.StartsAt is { } starts && starts <= now)
        {
            throw BookingErrors.ItemNotCancellable("The stay or rental has started, so it can no longer be cancelled.");
        }

        item.Cancel = CancelState.Requested;
        item.CancelRequestedAt = now;
        item.CancelDeadline = now + timeout;
        item.Awaiting = ItemRequest.Cancel;
        Announce(new ItemStatusAnnouncement(item.Id, item.Kind, item.Status, CancelState.Requested));
        Send(item.IsStayOrCar
            ? new CancelBooking(item.Kind, item.Id, item.HoldId)
            : new CancelFlightOrder(item.Id, item.OrderId!.Value));

        return Complete(now);
    }

    /// <summary>
    /// The owner confirmed the cancellation. A stay or car refunds what its own policy says for the moment the
    /// traveller asked, not the moment the owner answered, so a slow answer never changes the amount. A flight
    /// refunds what the airline's fare rules gave, which Flights reports.
    /// </summary>
    private void CompleteCancellation(TripItem item, Money? providerRefund, DateTimeOffset now)
    {
        Money total = item.Total ?? item.ExpectedTotal;
        Money refund = item.IsStayOrCar
            ? item.StartsAt is { } starts
                ? CancellationPolicies.Refund(total, item.Units, starts, item.FreeCancellationHours, item.Penalty, item.CancelRequestedAt ?? now)
                : Money.Zero(total.Currency)
            : providerRefund ?? Money.Zero(total.Currency);

        // Never give back more than was charged for the item, whatever the owner says.
        if (refund > total)
        {
            refund = total;
        }

        item.Awaiting = ItemRequest.None;
        item.Cancel = CancelState.None;
        item.RefundAmount = refund.Amount;
        Move(item, ItemStatus.Cancelled);

        if (refund.Amount > 0m)
        {
            item.RefundId = Guid.CreateVersion7();
            item.Refund = RefundStatus.Requested;
            Send(new RefundPayment(PaymentId, item.RefundId.Value, refund));
        }

        Announce(new ItemCancelledAnnouncement(item.Id, refund));

        if (items.All(static i => i.Status == ItemStatus.Cancelled))
        {
            SetStatus(TripStatus.Cancelled);
        }
    }
}
