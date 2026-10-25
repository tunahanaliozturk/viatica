using System.Diagnostics;
using Microsoft.Extensions.Logging;
using Viatica.Booking.Domain;
using Viatica.Contracts;
using Viatica.Kernel;

namespace Viatica.Booking.Application;

/// <summary>
/// Turns what the trip decided into integration events and queues them in the outbox, in the order they were decided.
/// Nothing is sent from here: the events leave with the caller's <c>SaveChanges</c>, in the transaction that holds
/// the state change.
/// </summary>
public sealed class DirectivePublisher(IBookingDb db, TravellerVault vault, TimeProvider clock, ILogger<DirectivePublisher> logger)
{
    /// <param name="trip">The trip the directives came from.</param>
    /// <param name="directives">What it decided.</param>
    /// <param name="details">
    /// The traveller's details, when the caller has them in plain text (the checkout). Without them a flight order or a
    /// payment authorisation opens the sealed copy on the trip.
    /// </param>
    public void Publish(Trip trip, IEnumerable<SagaDirective> directives, TravellerDetails? details = null)
    {
        ArgumentNullException.ThrowIfNull(trip);
        ArgumentNullException.ThrowIfNull(directives);

        DateTimeOffset now = clock.GetUtcNow();
        TravellerDetails? plain = details;
        TravellerDetails Details() => plain ??= vault.TryOpen(trip.Id, trip.SealedTravellerData, out TravellerDetails opened)
            ? opened
            : throw new InvalidOperationException($"The traveller data of trip {trip.Id} is missing or does not open.");

        foreach (SagaDirective directive in directives)
        {
            db.Publish(Map(trip, directive, now, Details));
            CountCompensation(directive);
        }
    }

    private IIntegrationEvent Map(Trip trip, SagaDirective directive, DateTimeOffset now, Func<TravellerDetails> details)
    {
        Guid tripId = trip.Id;

        return directive switch
        {
            RequestStayHold d => new StayHoldRequested(tripId, d.ItemId, d.HoldId, d.QuoteToken, d.Quantity),
            RequestCarHold d => new CarHoldRequested(tripId, d.ItemId, d.HoldId, d.QuoteToken, d.DriverAge),
            RequestFlightOrder d => new FlightOrderRequested(
                tripId, d.ItemId, d.OfferId, ToPrice(d.ExpectedTotal), vault.SealPassengersForFlights(tripId, d.ItemId, details())),
            RequestFlightTicketing d => new FlightTicketingRequested(tripId, d.ItemId, d.OrderId),

            CommitHold { Kind: ItemKind.Stay } d => new StayHoldCommitRequested(tripId, d.ItemId, d.HoldId),
            CommitHold d => new CarHoldCommitRequested(tripId, d.ItemId, d.HoldId),
            ReleaseHold { Kind: ItemKind.Stay } d => new StayHoldReleaseRequested(tripId, d.ItemId, d.HoldId),
            ReleaseHold d => new CarHoldReleaseRequested(tripId, d.ItemId, d.HoldId),
            CancelBooking { Kind: ItemKind.Stay } d => new StayBookingCancelRequested(tripId, d.ItemId, d.HoldId),
            CancelBooking d => new CarBookingCancelRequested(tripId, d.ItemId, d.HoldId),
            VoidFlightOrder d => new FlightOrderVoidRequested(tripId, d.ItemId, d.OrderId),
            CancelFlightOrder d => new FlightOrderCancelRequested(tripId, d.ItemId, d.OrderId),

            AuthorizePayment d => new PaymentAuthorizationRequested(tripId, d.PaymentId, ToPrice(d.Amount), details().PaymentMethodToken),
            CapturePayment d => new PaymentCaptureRequested(tripId, d.PaymentId, ToPrice(d.Amount)),
            VoidPayment d => new PaymentVoidRequested(tripId, d.PaymentId),
            RefundPayment d => new PaymentRefundRequested(tripId, d.PaymentId, d.RefundId, ToPrice(d.Amount)),

            TripStatusAnnouncement d => new TripStateChanged(tripId, trip.TravellerId, ToContract(d.Status), now),
            ItemStatusAnnouncement d => new TripItemStateChanged(tripId, d.ItemId, ToContract(d.Kind), ToContract(d.Status, d.Cancel), now),
            ItemRemovedAnnouncement d => new TripItemStateChanged(tripId, d.ItemId, ToContract(d.Kind), TripItemState.Removed, now),
            TripClaimedAnnouncement d => new TripClaimed(tripId, d.TravellerId),
            TripConfirmedAnnouncement d => new TripConfirmed(
                tripId, trip.TravellerId, ToPrice(d.Total), [.. d.Items.Select(static i => new TripItemSummary(i.ItemId, ToContract(i.Kind), ToPrice(i.Total)))]),
            TripFailedAnnouncement d => new TripFailed(tripId, ToContract(d.Reason)),
            ItemCancelledAnnouncement d => new TripItemCancelled(tripId, d.ItemId, ToPrice(d.Refund)),

            _ => throw new UnreachableException($"No event is defined for {directive.GetType().Name}."),
        };
    }

    private void CountCompensation(SagaDirective directive)
    {
        string? kind = directive switch
        {
            ReleaseHold => "release",
            VoidFlightOrder => "void_flight",
            VoidPayment => "void_payment",
            CancelBooking => "cancel_booking",
            _ => null,
        };

        if (kind is not null)
        {
            BookingTelemetry.Compensations.Add(1, new KeyValuePair<string, object?>("kind", kind));
            logger.CompensationQueued(kind);
        }
    }

    public static Price ToPrice(Money money)
    {
        ArgumentNullException.ThrowIfNull(money);
        return new Price(money.Amount, money.Currency);
    }

    public static Money ToMoney(Price price)
    {
        ArgumentNullException.ThrowIfNull(price);
        return new Money(price.Amount, price.Currency);
    }

    private static TripItemKind ToContract(ItemKind kind) => kind switch
    {
        ItemKind.Stay => TripItemKind.Stay,
        ItemKind.Car => TripItemKind.Car,
        ItemKind.Flight => TripItemKind.Flight,
        _ => throw new ArgumentOutOfRangeException(nameof(kind)),
    };

    private static TripFailureReason ToContract(FailureReason reason) => reason switch
    {
        FailureReason.ItemRejected => TripFailureReason.ItemRejected,
        FailureReason.PaymentDeclined => TripFailureReason.PaymentDeclined,
        FailureReason.TicketingRefused => TripFailureReason.TicketingRefused,
        FailureReason.DeadlineReached => TripFailureReason.DeadlineReached,
        _ => throw new ArgumentOutOfRangeException(nameof(reason)),
    };

    private static TripState ToContract(TripStatus status) => Enum.Parse<TripState>(status.ToString());

    private static TripItemState ToContract(ItemStatus status, CancelState cancel) =>
        cancel == CancelState.Requested ? TripItemState.CancelRequested : Enum.Parse<TripItemState>(status.ToString());
}

internal static partial class DirectivePublisherLog
{
    public static void CompensationQueued(this ILogger logger, string kind) => LogCompensation(logger, kind);

    [LoggerMessage(1, LogLevel.Information, "Queued a compensating request: {Kind}")]
    private static partial void LogCompensation(ILogger logger, string kind);
}
