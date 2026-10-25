using System.Diagnostics;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Relevox;
using Viatica.Cars.Application.Features.Holds;
using Viatica.Cars.Domain;
using Viatica.Contracts;

namespace Viatica.Cars.Application.Integration;

// Booking's instructions arrive on booking.v1 as events. Each handler is a thin adapter: it maps the event to a use
// case, and the use case's outcome to the event Booking is waiting for. The consumer saves what is queued here
// together with its inbox row, so a redelivered event answers nothing twice.

public sealed class CarHoldRequestedHandler(ISender sender, ICarsDb db, ILogger<CarHoldRequestedHandler> logger)
    : IIntegrationEventHandler<CarHoldRequested>
{
    public async Task HandleAsync(CarHoldRequested message, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(message);

        HoldOutcome outcome = await sender.Send(
            new PlaceHold(message.TripId, message.ItemId, message.HoldId, message.QuoteToken, message.DriverAge),
            cancellationToken);

        switch (outcome)
        {
            case Placed(CarHold hold):
                db.Publish(new CarHoldPlaced(
                    hold.Id, hold.TripId, hold.ItemId, hold.LocationId, hold.CategoryId, hold.DropOffLocationId,
                    hold.PickUpAt, hold.DropOffAt, new Price(hold.Total, "EUR"), hold.ExpiresAt,
                    await TermsAsync(hold, cancellationToken)));
                CarsTelemetry.HoldsPlaced.Add(1);
                break;

            case Refused(HoldRejectionReason reason):
                db.Publish(new CarHoldRejected(message.HoldId, message.TripId, message.ItemId, reason));
                CarsTelemetry.HoldsRejected.Add(1, new KeyValuePair<string, object?>("reason", reason.ToString()));
                CarsLog.HoldRefused(logger, message.HoldId, reason);
                break;

            case Superseded(CarHold hold):
                CarsLog.HoldAlreadyMoved(logger, hold.Id, hold.Status);
                break;

            case null:
                throw new UnreachableException("A union value with no case means the use case returned default.");
        }
    }

    // Booking needs the partner (for its bookings list) and the cancellation policy as it stood at the hold. Read
    // from the location now, which is the same moment for a new hold and a close one for an answer sent again.
    private async Task<HoldTerms> TermsAsync(CarHold hold, CancellationToken cancellationToken)
    {
        var location = await db.Locations.AsNoTracking()
            .Where(l => l.Id == hold.LocationId)
            .Select(static l => new { l.PartnerId, l.Cancellation })
            .SingleAsync(cancellationToken);

        return new HoldTerms(
            location.PartnerId,
            hold.PickUpAt,
            new Contracts.CancellationPolicy(
                location.Cancellation.FreeUntilHoursBeforeStart,
                location.Cancellation.PenaltyAfter switch
                {
                    Domain.CancellationPenalty.FirstDay => Contracts.CancellationPenalty.FirstUnit,
                    Domain.CancellationPenalty.FullAmount => Contracts.CancellationPenalty.FullAmount,
                    _ => throw new InvalidOperationException("Unknown cancellation penalty."),
                }));
    }
}

public sealed class CarHoldCommitRequestedHandler(ISender sender, ICarsDb db, ILogger<CarHoldCommitRequestedHandler> logger)
    : IIntegrationEventHandler<CarHoldCommitRequested>
{
    public async Task HandleAsync(CarHoldCommitRequested message, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(message);

        Transition result = await sender.Send(new CommitHold(message.TripId, message.ItemId, message.HoldId), cancellationToken);
        if (result == Transition.Ignored)
        {
            CarsLog.RequestIgnored(logger, nameof(CarHoldCommitRequested), message.HoldId);
            return;
        }

        db.Publish(new CarHoldCommitted(message.HoldId, message.TripId, message.ItemId));
        if (result == Transition.Applied)
        {
            CarsTelemetry.HoldsCommitted.Add(1);
        }
    }
}

public sealed class CarHoldReleaseRequestedHandler(ISender sender, ICarsDb db, ILogger<CarHoldReleaseRequestedHandler> logger)
    : IIntegrationEventHandler<CarHoldReleaseRequested>
{
    public async Task HandleAsync(CarHoldReleaseRequested message, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(message);

        Transition result = await sender.Send(new ReleaseHold(message.TripId, message.ItemId, message.HoldId), cancellationToken);
        if (result == Transition.Ignored)
        {
            CarsLog.RequestIgnored(logger, nameof(CarHoldReleaseRequested), message.HoldId);
            return;
        }

        db.Publish(new CarHoldReleased(message.HoldId, message.TripId, message.ItemId));
    }
}

public sealed class CarBookingCancelRequestedHandler(ISender sender, ICarsDb db, ILogger<CarBookingCancelRequestedHandler> logger)
    : IIntegrationEventHandler<CarBookingCancelRequested>
{
    public async Task HandleAsync(CarBookingCancelRequested message, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(message);

        Transition result = await sender.Send(new CancelBooking(message.TripId, message.ItemId, message.HoldId), cancellationToken);
        if (result == Transition.Ignored)
        {
            CarsLog.RequestIgnored(logger, nameof(CarBookingCancelRequested), message.HoldId);
            return;
        }

        db.Publish(new CarBookingCancelled(message.HoldId, message.TripId, message.ItemId));
    }
}

internal static partial class CarsLog
{
    [LoggerMessage(1, LogLevel.Information, "Hold {HoldId} refused: {Reason}")]
    public static partial void HoldRefused(ILogger logger, Guid holdId, HoldRejectionReason reason);

    [LoggerMessage(2, LogLevel.Debug, "Hold {HoldId} was requested again but is already {Status}; nothing to answer")]
    public static partial void HoldAlreadyMoved(ILogger logger, Guid holdId, HoldStatus status);

    [LoggerMessage(3, LogLevel.Debug, "{Request} for hold {HoldId} does not apply to the hold's state or trip; ignored")]
    public static partial void RequestIgnored(ILogger logger, string request, Guid holdId);
}
