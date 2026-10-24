using Microsoft.Extensions.Logging;
using Viatica.Contracts;
using Viatica.Stays.Application.Inventory;
using Viatica.Stays.Domain;

namespace Viatica.Stays.Application.Features.Holds;

// The four requests that move a hold after it was placed. Every one is idempotent by the hold's state: asking for what
// has already happened answers nothing and changes nothing, so a resend after a restart is safe. Each runs inside
// the consumer's transaction, which commits the state change, the room counts, the outbox rows and the inbox row as
// one.

/// <summary>Booking confirms a hold: its rooms move from held to sold.</summary>
public sealed class CommitHoldHandler(HoldTransitions holds, NightLedger ledger, IStaysDb db, ILogger<CommitHoldHandler> logger)
    : IIntegrationEventHandler<StayHoldCommitRequested>
{
    public async Task HandleAsync(StayHoldCommitRequested message, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(message);

        if (await holds.CommitAsync(message.HoldId, cancellationToken) is not { } hold)
        {
            // Committed already is a redelivery. Expired, released or unknown means the saga lost the race to the
            // sweeper, and it has been told by StayHoldExpired; there is no commit-refused event to send.
            logger.CommitSkipped(message.HoldId, await holds.StatusOfAsync(message.HoldId, cancellationToken));
            return;
        }

        await ledger.MoveHeldToSoldAsync(hold, cancellationToken);

        // No availability event: rooms moving from held to sold leave the number still available unchanged.
        db.Publish(new StayHoldCommitted(hold.Id, hold.TripId, hold.ItemId));
        StaysTelemetry.HoldsCommitted.Add(1);
    }
}

/// <summary>Booking gives a hold back before it expired, usually because another item of the trip failed.</summary>
public sealed class ReleaseHoldHandler(HoldTransitions holds, NightLedger ledger, IStaysDb db, ILogger<ReleaseHoldHandler> logger)
    : IIntegrationEventHandler<StayHoldReleaseRequested>
{
    public async Task HandleAsync(StayHoldReleaseRequested message, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(message);

        if (await holds.ReleaseAsync(message.HoldId, cancellationToken) is { } hold)
        {
            await ledger.ReleaseHeldAsync(hold, cancellationToken);
            await ledger.PublishAvailabilityAsync(hold, cancellationToken);
            db.Publish(new StayHoldReleased(hold.Id, hold.TripId, hold.ItemId));
            StaysTelemetry.HoldsReleased.Add(1);
            return;
        }

        HoldStatus? status = await holds.StatusOfAsync(message.HoldId, cancellationToken);
        if (status is null)
        {
            // Nothing was ever held under this id (the request was parked on the dead letter topic, say). "Nothing is
            // held" is what a release wants to hear, and Booking is waiting for it.
            logger.ReleaseOfUnknownHold(message.HoldId);
            db.Publish(new StayHoldReleased(message.HoldId, message.TripId, message.ItemId));
            return;
        }

        logger.ReleaseSkipped(message.HoldId, status.Value);
    }
}

/// <summary>A committed stay is cancelled: its rooms go back on sale.</summary>
public sealed class CancelBookingHandler(HoldTransitions holds, NightLedger ledger, IStaysDb db, ILogger<CancelBookingHandler> logger)
    : IIntegrationEventHandler<StayBookingCancelRequested>
{
    public async Task HandleAsync(StayBookingCancelRequested message, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(message);

        if (await holds.CancelAsync(message.HoldId, cancellationToken) is not { } hold)
        {
            logger.CancelSkipped(message.HoldId, await holds.StatusOfAsync(message.HoldId, cancellationToken));
            return;
        }

        await ledger.ReleaseSoldAsync(hold, cancellationToken);
        await ledger.PublishAvailabilityAsync(hold, cancellationToken);
        db.Publish(new StayBookingCancelled(hold.Id, hold.TripId, hold.ItemId));
        StaysTelemetry.BookingsCancelled.Add(1);
    }
}
