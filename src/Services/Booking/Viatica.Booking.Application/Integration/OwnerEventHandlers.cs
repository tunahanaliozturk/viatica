using Viatica.Booking.Domain;
using Viatica.Contracts;
using Viatica.Kernel;

namespace Viatica.Booking.Application.Integration;

// What Stays, Cars, Flights and Payments tell Booking. Each handler is a line: name the trip, name the cause, let the
// aggregate decide. The consumer saves what the runner queued together with the inbox row, so the same event id
// twice applies once; the aggregate makes the same content under a new event id apply once as well. The handlers
// share nothing, so a trip's four owners are four consumers working on one trip, ordered by the row lock.

// Stays.

public sealed class StayHoldPlacedHandler(TripRunner runner) : IIntegrationEventHandler<StayHoldPlaced>
{
    public Task HandleAsync(StayHoldPlaced message, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(message);
        return runner.ApplyAsync(
            message.TripId,
            nameof(StayHoldPlaced),
            (trip, now) => trip.OnHoldPlaced(message.ItemId, message.HoldId, DirectivePublisher.ToMoney(message.Total), message.ExpiresAt, Terms.From(message.Terms, message.CheckIn, message.CheckOut), now),
            cancellationToken);
    }
}

public sealed class StayHoldRejectedHandler(TripRunner runner) : IIntegrationEventHandler<StayHoldRejected>
{
    public Task HandleAsync(StayHoldRejected message, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(message);
        return runner.ApplyAsync(message.TripId, nameof(StayHoldRejected), (trip, now) => trip.OnHoldRejected(message.ItemId, message.HoldId, message.Reason.ToString(), now), cancellationToken);
    }
}

public sealed class StayHoldCommittedHandler(TripRunner runner) : IIntegrationEventHandler<StayHoldCommitted>
{
    public Task HandleAsync(StayHoldCommitted message, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(message);
        return runner.ApplyAsync(message.TripId, nameof(StayHoldCommitted), (trip, now) => trip.OnHoldCommitted(message.ItemId, message.HoldId, now), cancellationToken);
    }
}

public sealed class StayHoldReleasedHandler(TripRunner runner) : IIntegrationEventHandler<StayHoldReleased>
{
    public Task HandleAsync(StayHoldReleased message, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(message);
        return runner.ApplyAsync(message.TripId, nameof(StayHoldReleased), (trip, now) => trip.OnHoldReleased(message.ItemId, message.HoldId, now), cancellationToken);
    }
}

public sealed class StayHoldExpiredHandler(TripRunner runner) : IIntegrationEventHandler<StayHoldExpired>
{
    public Task HandleAsync(StayHoldExpired message, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(message);
        return runner.ApplyAsync(message.TripId, nameof(StayHoldExpired), (trip, now) => trip.OnHoldExpired(message.ItemId, message.HoldId, now), cancellationToken);
    }
}

public sealed class StayBookingCancelledHandler(TripRunner runner) : IIntegrationEventHandler<StayBookingCancelled>
{
    public Task HandleAsync(StayBookingCancelled message, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(message);
        return runner.ApplyAsync(message.TripId, nameof(StayBookingCancelled), (trip, now) => trip.OnBookingCancelled(message.ItemId, message.HoldId, now), cancellationToken);
    }
}

// Cars.

public sealed class CarHoldPlacedHandler(TripRunner runner) : IIntegrationEventHandler<CarHoldPlaced>
{
    public Task HandleAsync(CarHoldPlaced message, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(message);
        return runner.ApplyAsync(
            message.TripId,
            nameof(CarHoldPlaced),
            (trip, now) => trip.OnHoldPlaced(
                message.ItemId, message.HoldId, DirectivePublisher.ToMoney(message.Total), message.ExpiresAt,
                Terms.From(message.Terms, message.PickUpAt, message.DropOffAt), now),
            cancellationToken);
    }
}

public sealed class CarHoldRejectedHandler(TripRunner runner) : IIntegrationEventHandler<CarHoldRejected>
{
    public Task HandleAsync(CarHoldRejected message, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(message);
        return runner.ApplyAsync(message.TripId, nameof(CarHoldRejected), (trip, now) => trip.OnHoldRejected(message.ItemId, message.HoldId, message.Reason.ToString(), now), cancellationToken);
    }
}

public sealed class CarHoldCommittedHandler(TripRunner runner) : IIntegrationEventHandler<CarHoldCommitted>
{
    public Task HandleAsync(CarHoldCommitted message, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(message);
        return runner.ApplyAsync(message.TripId, nameof(CarHoldCommitted), (trip, now) => trip.OnHoldCommitted(message.ItemId, message.HoldId, now), cancellationToken);
    }
}

public sealed class CarHoldReleasedHandler(TripRunner runner) : IIntegrationEventHandler<CarHoldReleased>
{
    public Task HandleAsync(CarHoldReleased message, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(message);
        return runner.ApplyAsync(message.TripId, nameof(CarHoldReleased), (trip, now) => trip.OnHoldReleased(message.ItemId, message.HoldId, now), cancellationToken);
    }
}

public sealed class CarHoldExpiredHandler(TripRunner runner) : IIntegrationEventHandler<CarHoldExpired>
{
    public Task HandleAsync(CarHoldExpired message, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(message);
        return runner.ApplyAsync(message.TripId, nameof(CarHoldExpired), (trip, now) => trip.OnHoldExpired(message.ItemId, message.HoldId, now), cancellationToken);
    }
}

public sealed class CarBookingCancelledHandler(TripRunner runner) : IIntegrationEventHandler<CarBookingCancelled>
{
    public Task HandleAsync(CarBookingCancelled message, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(message);
        return runner.ApplyAsync(message.TripId, nameof(CarBookingCancelled), (trip, now) => trip.OnBookingCancelled(message.ItemId, message.HoldId, now), cancellationToken);
    }
}

// Flights.

public sealed class FlightOrderCreatedHandler(TripRunner runner) : IIntegrationEventHandler<FlightOrderCreated>
{
    public Task HandleAsync(FlightOrderCreated message, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(message);
        return runner.ApplyAsync(
            message.TripId,
            nameof(FlightOrderCreated),
            (trip, now) => trip.OnFlightOrderCreated(message.ItemId, message.OrderId, DirectivePublisher.ToMoney(message.Total), message.TicketingTimeLimit, now),
            cancellationToken);
    }
}

public sealed class FlightOrderRejectedHandler(TripRunner runner) : IIntegrationEventHandler<FlightOrderRejected>
{
    public Task HandleAsync(FlightOrderRejected message, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(message);
        return runner.ApplyAsync(message.TripId, nameof(FlightOrderRejected), (trip, now) => trip.OnFlightOrderRejected(message.ItemId, message.Reason.ToString(), now), cancellationToken);
    }
}

public sealed class FlightTicketedHandler(TripRunner runner) : IIntegrationEventHandler<FlightTicketed>
{
    public Task HandleAsync(FlightTicketed message, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(message);
        return runner.ApplyAsync(message.TripId, nameof(FlightTicketed), (trip, now) => trip.OnFlightTicketed(message.ItemId, message.OrderId, now), cancellationToken);
    }
}

public sealed class FlightTicketingRefusedHandler(TripRunner runner) : IIntegrationEventHandler<FlightTicketingRefused>
{
    public Task HandleAsync(FlightTicketingRefused message, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(message);
        return runner.ApplyAsync(message.TripId, nameof(FlightTicketingRefused), (trip, now) => trip.OnFlightTicketingRefused(message.ItemId, message.OrderId, message.Reason, now), cancellationToken);
    }
}

public sealed class FlightOrderVoidedHandler(TripRunner runner) : IIntegrationEventHandler<FlightOrderVoided>
{
    public Task HandleAsync(FlightOrderVoided message, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(message);
        return runner.ApplyAsync(message.TripId, nameof(FlightOrderVoided), (trip, now) => trip.OnFlightOrderVoided(message.ItemId, message.OrderId, now), cancellationToken);
    }
}

public sealed class FlightOrderCancelledHandler(TripRunner runner) : IIntegrationEventHandler<FlightOrderCancelled>
{
    public Task HandleAsync(FlightOrderCancelled message, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(message);
        return runner.ApplyAsync(
            message.TripId,
            nameof(FlightOrderCancelled),
            (trip, now) => trip.OnFlightOrderCancelled(message.ItemId, message.OrderId, DirectivePublisher.ToMoney(message.Refund), now),
            cancellationToken);
    }
}

// Payments.

public sealed class PaymentAuthorizedHandler(TripRunner runner) : IIntegrationEventHandler<PaymentAuthorized>
{
    public Task HandleAsync(PaymentAuthorized message, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(message);
        return runner.ApplyAsync(message.TripId, nameof(PaymentAuthorized), (trip, now) => trip.OnPaymentAuthorized(message.PaymentId, now), cancellationToken);
    }
}

public sealed class PaymentDeclinedHandler(TripRunner runner) : IIntegrationEventHandler<PaymentDeclined>
{
    public Task HandleAsync(PaymentDeclined message, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(message);
        return runner.ApplyAsync(message.TripId, nameof(PaymentDeclined), (trip, now) => trip.OnPaymentDeclined(message.PaymentId, now), cancellationToken);
    }
}

public sealed class PaymentCapturedHandler(TripRunner runner) : IIntegrationEventHandler<PaymentCaptured>
{
    public Task HandleAsync(PaymentCaptured message, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(message);
        return runner.ApplyAsync(message.TripId, nameof(PaymentCaptured), (trip, now) => trip.OnPaymentCaptured(message.PaymentId, now), cancellationToken);
    }
}

public sealed class PaymentVoidedHandler(TripRunner runner) : IIntegrationEventHandler<PaymentVoided>
{
    public Task HandleAsync(PaymentVoided message, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(message);
        return runner.ApplyAsync(message.TripId, nameof(PaymentVoided), (trip, now) => trip.OnPaymentVoided(message.PaymentId, now), cancellationToken);
    }
}

public sealed class PaymentRefundedHandler(TripRunner runner) : IIntegrationEventHandler<PaymentRefunded>
{
    public Task HandleAsync(PaymentRefunded message, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(message);
        return runner.ApplyAsync(message.TripId, nameof(PaymentRefunded), (trip, now) => trip.OnPaymentRefunded(message.PaymentId, message.RefundId, now), cancellationToken);
    }
}

/// <summary>Reads the terms an owner attached to a hold. Without them (an older owner) the item is kept but cannot refund or be listed.</summary>
internal static class Terms
{
    public static ItemTerms From(HoldTerms? terms, DateOnly checkIn, DateOnly checkOut) =>
        From(terms, Math.Max(checkOut.DayNumber - checkIn.DayNumber, 1));

    public static ItemTerms From(HoldTerms? terms, DateTimeOffset pickUpAt, DateTimeOffset dropOffAt) =>
        From(terms, CancellationPolicies.RentalDays(pickUpAt, dropOffAt));

    private static ItemTerms From(HoldTerms? terms, int units) => terms is null
        ? new ItemTerms(null, null, units, null, null)
        : new ItemTerms(
            terms.PartnerId,
            terms.StartsAt,
            units,
            terms.CancellationPolicy.FreeUntilHoursBeforeStart,
            terms.CancellationPolicy.PenaltyAfter == CancellationPenalty.FirstUnit ? Penalty.FirstUnit : Penalty.FullAmount);
}
