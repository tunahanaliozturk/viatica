namespace Viatica.Booking.Domain;

/// <summary>
/// Where a trip is. The checkout stages follow docs/design.md: <c>Holding -> Authorizing -> Ticketing ->
/// Committing -> Capturing -> Confirmed</c>, with <c>Unwinding -> Failed</c> as the only other way out. A trip
/// whose items were all cancelled afterwards is <c>Cancelled</c>.
/// </summary>
public enum TripStatus
{
    Draft,
    Holding,
    Authorizing,
    Ticketing,
    Committing,
    Capturing,
    Confirmed,
    Unwinding,
    Failed,
    Cancelled,
}

public enum ItemKind
{
    Stay,
    Car,
    Flight,
}

/// <summary>
/// What the owner of the item has done for the trip. Stays and cars use <c>Pending -> Held -> Committed</c>, flights
/// <c>Pending -> Ordered -> Ticketed</c>; the rest are the ways out. See docs/design.md, "Item states".
/// </summary>
public enum ItemStatus
{
    Pending,
    Held,
    Ordered,
    Ticketed,
    Committed,
    Rejected,
    Released,
    Expired,
    Voided,
    Cancelled,
}

/// <summary>
/// The one request to the item's owner that has no answer yet. It is what lets the saga tell "I asked and I am
/// waiting" from "I have not asked", and so send each instruction once and resend only what is still open.
/// </summary>
public enum ItemRequest
{
    None,
    Hold,
    Ticketing,
    Commit,
    Release,
    Void,
    Cancel,
}

public enum PaymentStatus
{
    None,
    AuthorizationRequested,
    Authorized,
    Declined,
    CaptureRequested,
    Captured,
    VoidRequested,
    Voided,
}

public enum FailureReason
{
    ItemRejected,
    PaymentDeclined,
    TicketingRefused,
    DeadlineReached,
}

/// <summary>A traveller's cancellation of one confirmed item, from the request until its owner answers.</summary>
public enum CancelState
{
    None,
    Requested,

    /// <summary>The owner did not answer in time. The request stands, a late answer is still accepted, and the traveller may ask again.</summary>
    TimedOut,
}

public enum RefundStatus
{
    None,
    Requested,
    Refunded,

    /// <summary>Payments refused the refund. The item stays cancelled and the money is owed: a person settles it.</summary>
    Refused,
}

/// <summary>The payment operations Payments can refuse after the payment exists. An authorisation is declined instead.</summary>
public enum PaymentOperationKind
{
    Capture,
    Void,
    Refund,
}

public enum Penalty
{
    /// <summary>The first night of a stay or the first day of a rental is kept.</summary>
    FirstUnit,

    /// <summary>Nothing is refunded.</summary>
    FullAmount,
}

/// <summary>What a repeated or new checkout request came to.</summary>
public enum CheckoutOutcome
{
    Started,

    /// <summary>The same traveller sent the same details for the same trip before. Nothing changed.</summary>
    Repeated,

    /// <summary>The trip was checked out already with other details.</summary>
    Conflict,
}
