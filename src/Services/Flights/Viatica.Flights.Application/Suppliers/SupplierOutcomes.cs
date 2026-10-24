using Viatica.Flights.Application.Offers;
using Viatica.Flights.Domain;
using Viatica.Kernel;
using Viatica.Kernel.Pii;

namespace Viatica.Flights.Application.Suppliers;

// What an airline can answer, as values. A supplier call has several normal endings (the airline refuses to ticket,
// the offer is gone, the call timed out and may or may not have worked), and each one is a different next step for
// the caller, so each is a case of a union and the caller's switch is checked for completeness (ADR 0001).
// None of these ever goes on the wire: the API and the events map them explicitly.

/// <summary>An offer as the airline returned it, mapped into the platform's model. <see cref="Offer"/> has no id yet.</summary>
public sealed record SupplierOffer(string SupplierOfferId, FlightOffer Offer);

public enum FailureKind
{
    Timeout,
    Error,
    CircuitOpen,
}

/// <summary>
/// The airline could not be asked, or did not answer usefully.
/// </summary>
/// <param name="Kind">What the caller names in <c>incompleteSuppliers</c>.</param>
/// <param name="MayHaveReachedAirline">
/// True when the request may have been processed even though no answer came back: a timeout, a dropped connection,
/// a 500. False when it certainly was not: the circuit was open and nothing was sent, or the airline said it was
/// unavailable (503) before doing anything. Creating an order must look the order up when this is true.
/// </param>
public sealed record SupplierFailure(FailureKind Kind, bool MayHaveReachedAirline);

public sealed record SupplierOffers(IReadOnlyList<SupplierOffer> Offers);

public union SearchOutcome(SupplierOffers, SupplierFailure);

public sealed record OfferRepriced(SupplierOffer Offer, decimal PreviousTotal);

/// <summary>The offer expired, was never the airline's, or the cabin sold out.</summary>
public sealed record OfferGone;

public union RepriceOutcome(OfferRepriced, OfferGone, SupplierFailure);

/// <summary>Everything the airline needs to create an order. The key makes a repeat the same order, not a second.</summary>
public sealed record NewSupplierOrder(string SupplierOfferId, string IdempotencyKey, IReadOnlyList<PassengerDetails> Passengers);

public enum SupplierOrderStatus
{
    Booked,
    Ticketed,
    Voided,
    Cancelled,
}

public sealed record SupplierOrder(
    string BookingReference,
    string IdempotencyKey,
    SupplierOrderStatus Status,
    Money Total,
    DateTimeOffset TicketingTimeLimit,
    IReadOnlyList<string> TicketNumbers,
    Money? Refund);

public sealed record OrderCreated(SupplierOrder Order);

public enum RefusalReason
{
    SoldOut,
    OfferExpired,
    InvalidPassengers,

    /// <summary>The airline is up but will not take this order for a reason of its own: the key was used for another offer.</summary>
    Other,
}

/// <summary>The airline answered and said no. Nothing was created.</summary>
public sealed record OrderRefused(RefusalReason Reason);

public union CreateOrderOutcome(OrderCreated, OrderRefused, SupplierFailure);

public sealed record OrderFound(SupplierOrder Order);

/// <summary>The airline has no order under that key.</summary>
public sealed record NoSuchOrder;

public union FindOrderOutcome(OrderFound, NoSuchOrder, SupplierFailure);

public sealed record TicketsIssued(IReadOnlyList<string> TicketNumbers);

/// <summary>The airline will not issue tickets. <see cref="Reason"/> is its own text, for operators.</summary>
public sealed record TicketsRefused(string Reason);

public union TicketingOutcome(TicketsIssued, TicketsRefused, SupplierFailure);

public sealed record OrderVoided;

/// <summary>The airline's void window is over: the order can only be cancelled, with the fare's refund rules.</summary>
public sealed record VoidWindowClosed;

/// <summary>The airline has no live order of that reference: unknown, or already closed some other way.</summary>
public sealed record OrderGone;

public union VoidOutcome(OrderVoided, VoidWindowClosed, OrderGone, SupplierFailure);

public sealed record OrderCancelled(Money Refund);

/// <summary>The order was never ticketed, so there is nothing to cancel; voiding it is the right call.</summary>
public sealed record NotTicketed;

public union CancelOutcome(OrderCancelled, NotTicketed, OrderGone, SupplierFailure);

/// <summary>What a search asks every airline.</summary>
public sealed record SearchCriteria(
    IataCode Origin,
    IataCode Destination,
    DateOnly DepartureDate,
    DateOnly? ReturnDate,
    PassengerMix Passengers,
    Cabin Cabin);
