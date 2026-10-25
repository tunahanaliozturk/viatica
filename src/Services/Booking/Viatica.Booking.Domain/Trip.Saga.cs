using Viatica.Kernel;

namespace Viatica.Booking.Domain;

// The stages of the checkout saga and the way out of it. Order of the stages (ADR 0006): hold everything, authorise,
// ticket the flights, commit the stays and cars, capture. The step that an owner can still refuse comes first, so
// a failure never has to undo a committed stay.

public sealed partial class Trip
{
    private static bool InCheckoutBeforeCommit(TripStatus status) =>
        status is TripStatus.Holding or TripStatus.Authorizing or TripStatus.Ticketing;

    /// <summary>Moves the saga as far forward as the facts on the trip allow, or into unwinding if one of them is a loss.</summary>
    private void Advance(DateTimeOffset now)
    {
        while (StepForward(now))
        {
        }
    }

    private bool StepForward(DateTimeOffset now)
    {
        if (Status == TripStatus.Unwinding)
        {
            ContinueUnwinding(now);
            return false;
        }

        if (Status is not (TripStatus.Holding or TripStatus.Authorizing or TripStatus.Ticketing or TripStatus.Committing or TripStatus.Capturing))
        {
            return false;
        }

        if (Status != TripStatus.Capturing && LossOnTheWay() is { } reason)
        {
            BeginUnwinding(reason, now);
            return false;
        }

        switch (Status)
        {
            case TripStatus.Holding when items.All(static i => i.IsSecured):
                Money amount = Sum(items, static i => i.Total!);
                PaymentAmount = amount.Amount;
                Currency = amount.Currency;
                Payment = PaymentStatus.AuthorizationRequested;
                SetStatus(TripStatus.Authorizing);
                Send(new AuthorizePayment(PaymentId, amount));
                return false;

            case TripStatus.Authorizing when Payment == PaymentStatus.Authorized:
                return EnterTicketing();

            case TripStatus.Ticketing when items.Where(static i => i.Kind == ItemKind.Flight).All(static i => i.Status == ItemStatus.Ticketed):
                return EnterCommitting();

            case TripStatus.Committing when items.Where(static i => i.IsStayOrCar).All(static i => i.Status == ItemStatus.Committed):
                return EnterCapturing();

            case TripStatus.Capturing when Payment == PaymentStatus.Captured:
                Confirm();
                return false;

            default:
                return false;
        }
    }

    /// <summary>Why the checkout cannot go on, or null. An owner that said no, a hold that is gone, a card that was declined.</summary>
    private FailureReason? LossOnTheWay()
    {
        if (Payment == PaymentStatus.Declined)
        {
            return FailureReason.PaymentDeclined;
        }

        if (items.Any(static i => i.Kind == ItemKind.Flight && i.Refusal is not null && i.Status == ItemStatus.Ordered))
        {
            return FailureReason.TicketingRefused;
        }

        if (items.Any(static i => i.Status == ItemStatus.Rejected))
        {
            return FailureReason.ItemRejected;
        }

        // A hold the owner's sweeper took back. Before the commit that is the deadline arriving early; during it,
        // it is the one case where a flight may already be ticketed (docs/design.md, "Item states").
        return items.Any(static i => i.Status == ItemStatus.Expired) ? FailureReason.DeadlineReached : null;
    }

    private bool EnterTicketing()
    {
        List<TripItem> flights = [.. items.Where(static i => i.Kind == ItemKind.Flight)];
        if (flights.Count == 0)
        {
            return EnterCommitting();
        }

        SetStatus(TripStatus.Ticketing);
        foreach (TripItem flight in flights)
        {
            flight.Awaiting = ItemRequest.Ticketing;
            Send(new RequestFlightTicketing(flight.Id, flight.OrderId!.Value));
        }

        return false;
    }

    private bool EnterCommitting()
    {
        List<TripItem> rooms = [.. items.Where(static i => i.IsStayOrCar)];
        if (rooms.Count == 0)
        {
            return EnterCapturing();
        }

        // From here the deadline no longer applies: the flights are ticketed, and a commit that has been asked for is
        // answered or lost to the owner's sweeper, which the lost-commit path handles.
        SetStatus(TripStatus.Committing);
        Deadline = null;
        foreach (TripItem room in rooms)
        {
            room.Awaiting = ItemRequest.Commit;
            Send(new CommitHold(room.Kind, room.Id, room.HoldId));
        }

        return false;
    }

    private bool EnterCapturing()
    {
        SetStatus(TripStatus.Capturing);
        Deadline = null;
        Payment = PaymentStatus.CaptureRequested;
        Send(new CapturePayment(PaymentId, new Money(PaymentAmount!.Value, Currency)));
        return false;
    }

    private void Confirm()
    {
        SetStatus(TripStatus.Confirmed);
        Send(new TripConfirmedAnnouncement(
            new Money(PaymentAmount!.Value, Currency),
            [.. items.Select(static i => new ConfirmedItem(i.Id, i.Kind, i.Total!))]));
        SealedTravellerData = null;
    }

    // The deadline.

    private void RecomputeDeadline()
    {
        if (!InCheckoutBeforeCommit(Status) || CheckoutStartedAt is not { } started)
        {
            Deadline = null;
            return;
        }

        // An item that has not reported yet is assumed to expire a hold lifetime after the checkout began, which is
        // the earliest it could: a hold is placed after the request, so it lives at least that long.
        DateTimeOffset assumed = started + AssumedHoldLifetime;
        DateTimeOffset shortest = items
            .Where(static i => !i.IsLost)
            .Select(i => i.ExpiresAt ?? assumed)
            .DefaultIfEmpty(assumed)
            .Min();
        Deadline = shortest - DeadlineMargin;
    }

    // Unwinding (BKG-3: every item released or voided, and the payment voided).

    private void BeginUnwinding(FailureReason reason, DateTimeOffset now)
    {
        Failure = reason;
        Deadline = null;
        SetStatus(TripStatus.Unwinding);
        ContinueUnwinding(now);
    }

    /// <summary>
    /// Sends whatever a failing trip still owes its owners and waits for each answer. Called again with every answer
    /// that arrives, so an item that could not be given back yet (it was still being ordered, still being committed)
    /// is dealt with the moment its owner reports. The trip fails when nothing is held, ordered or authorised.
    /// </summary>
    private void ContinueUnwinding(DateTimeOffset now)
    {
        foreach (TripItem item in items)
        {
            UnwindItem(item, now);
        }

        if (Payment == PaymentStatus.Authorized)
        {
            Payment = PaymentStatus.VoidRequested;
            Send(new VoidPayment(PaymentId));
            CompensationSentAt = now;
        }

        // An authorisation we asked for and have no answer to is still outstanding: it may yet be granted, and
        // then it must be voided. So the trip waits for the answer rather than guessing.
        bool paymentSettled = Payment is PaymentStatus.None or PaymentStatus.Declined or PaymentStatus.Voided;
        if (paymentSettled && items.All(static i => i.IsResolved))
        {
            SetStatus(TripStatus.Failed);
            Send(new TripFailedAnnouncement(Failure ?? FailureReason.ItemRejected));
            SealedTravellerData = null;
        }
    }

    private void UnwindItem(TripItem item, DateTimeOffset now)
    {
        if (item.IsStayOrCar)
        {
            switch (item.Status)
            {
                // The hold request is on its way or answered and not read yet. Releases are safe to send for a hold
                // that was never placed, and they follow the request on the same partition, so the owner sees the
                // hold first and the release second.
                case ItemStatus.Pending when item.Awaiting == ItemRequest.Hold:
                case ItemStatus.Held when item.Awaiting is not (ItemRequest.Release or ItemRequest.Commit):
                    item.Awaiting = ItemRequest.Release;
                    Send(new ReleaseHold(item.Kind, item.Id, item.HoldId));
                    CompensationSentAt = now;
                    break;

                // A commit that was asked for is answered by committed or by the sweeper's expiry. Wait: releasing now
                // would race the commit and the owner ignores a release for a committed hold.
                case ItemStatus.Committed when item.Awaiting is not ItemRequest.Cancel:
                    item.Awaiting = ItemRequest.Cancel;
                    Send(new CancelBooking(item.Kind, item.Id, item.HoldId));
                    CompensationSentAt = now;
                    break;
            }

            return;
        }

        // A flight whose order is not known yet cannot be voided by id. Its answer is awaited, and a late
        // FlightOrderCreated is voided on arrival.
        if (item.Status is ItemStatus.Ordered or ItemStatus.Ticketed
            && item.Awaiting is not (ItemRequest.Void or ItemRequest.Ticketing))
        {
            item.Awaiting = ItemRequest.Void;
            Send(new VoidFlightOrder(item.Id, item.OrderId!.Value));
            CompensationSentAt = now;
        }
    }

    // Scheduled work (docs/design.md: "A scheduled check in Booking drives this; it does not depend on any event arriving").

    /// <summary>
    /// What the clock alone can cause: a trip that reached its deadline unwinds, a compensating request nobody
    /// answered goes out again, and a cancellation its owner never answered is marked timed out.
    /// </summary>
    public IReadOnlyList<SagaDirective> RunScheduledChecks(DateTimeOffset now)
    {
        if (InCheckoutBeforeCommit(Status) && Deadline is { } deadline && deadline <= now)
        {
            BeginUnwinding(FailureReason.DeadlineReached, now);
        }

        if (Status is TripStatus.Unwinding or TripStatus.Failed && HasOutstandingCompensation
            && CompensationSentAt is { } sentAt && sentAt + ResendAfter <= now)
        {
            Resend(now);
        }

        foreach (TripItem item in items.Where(i => i.Cancel == CancelState.Requested && i.CancelDeadline <= now))
        {
            item.Cancel = CancelState.TimedOut;
            Announce(new ItemStatusAnnouncement(item.Id, item.Kind, item.Status, CancelState.TimedOut));
        }

        return Complete(now);
    }

    private bool HasOutstandingCompensation =>
        Payment == PaymentStatus.VoidRequested
        || items.Any(static i => i.Awaiting is ItemRequest.Release or ItemRequest.Void or ItemRequest.Cancel);

    private void Resend(DateTimeOffset now)
    {
        foreach (TripItem item in items)
        {
            switch (item.Awaiting)
            {
                case ItemRequest.Release:
                    Send(new ReleaseHold(item.Kind, item.Id, item.HoldId));
                    break;
                case ItemRequest.Void when item.OrderId is { } order:
                    Send(new VoidFlightOrder(item.Id, order));
                    break;
                case ItemRequest.Cancel when item.IsStayOrCar:
                    Send(new CancelBooking(item.Kind, item.Id, item.HoldId));
                    break;
            }
        }

        if (Payment == PaymentStatus.VoidRequested)
        {
            Send(new VoidPayment(PaymentId));
        }

        CompensationSentAt = now;
    }

    private void RecomputeNextCheck()
    {
        DateTimeOffset? next = InCheckoutBeforeCommit(Status) ? Deadline : null;

        if (Status is TripStatus.Unwinding or TripStatus.Failed && HasOutstandingCompensation && CompensationSentAt is { } sentAt)
        {
            next = Earliest(next, sentAt + ResendAfter);
        }

        foreach (TripItem item in items.Where(static i => i.Cancel == CancelState.Requested && i.CancelDeadline is not null))
        {
            next = Earliest(next, item.CancelDeadline!.Value);
        }

        NextCheckAt = next;
    }

    private static DateTimeOffset? Earliest(DateTimeOffset? current, DateTimeOffset candidate) =>
        current is { } c && c <= candidate ? c : candidate;
}
