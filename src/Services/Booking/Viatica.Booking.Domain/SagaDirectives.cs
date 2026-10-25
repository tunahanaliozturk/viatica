using Viatica.Kernel;

namespace Viatica.Booking.Domain;

/// <summary>
/// What the trip wants done next, said as data. The aggregate decides, the Application layer turns each directive
/// into an integration event and queues it in the outbox in the same transaction as the state change. Keeping the
/// decision in a pure function of the trip makes every failure branch testable without a broker.
/// </summary>
/// <remarks>
/// Requests go to the owner of an item or to Payments. Announcements tell everyone else what happened to the trip;
/// they carry no instruction. Both are kept in one ordered list so the outbox preserves the order they were decided in.
/// </remarks>
public abstract record SagaDirective;

// Instructions to Stays, Cars, Flights and Payments.

public sealed record RequestStayHold(Guid ItemId, Guid HoldId, string QuoteToken, int Quantity) : SagaDirective;

public sealed record RequestCarHold(Guid ItemId, Guid HoldId, string QuoteToken, int DriverAge) : SagaDirective;

/// <summary>The passenger list is not here: only the Application layer, at checkout, holds it in plain text.</summary>
public sealed record RequestFlightOrder(Guid ItemId, string OfferId, Money ExpectedTotal) : SagaDirective;

public sealed record RequestFlightTicketing(Guid ItemId, Guid OrderId) : SagaDirective;

public sealed record CommitHold(ItemKind Kind, Guid ItemId, Guid HoldId) : SagaDirective;

public sealed record ReleaseHold(ItemKind Kind, Guid ItemId, Guid HoldId) : SagaDirective;

/// <summary>Give back a committed stay or car: a failed checkout, or a traveller's cancellation.</summary>
public sealed record CancelBooking(ItemKind Kind, Guid ItemId, Guid HoldId) : SagaDirective;

public sealed record VoidFlightOrder(Guid ItemId, Guid OrderId) : SagaDirective;

public sealed record CancelFlightOrder(Guid ItemId, Guid OrderId) : SagaDirective;

public sealed record AuthorizePayment(Guid PaymentId, Money Amount) : SagaDirective;

public sealed record CapturePayment(Guid PaymentId, Money Amount) : SagaDirective;

public sealed record VoidPayment(Guid PaymentId) : SagaDirective;

public sealed record RefundPayment(Guid PaymentId, Guid RefundId, Money Amount) : SagaDirective;

// Announcements.

public sealed record TripStatusAnnouncement(TripStatus Status) : SagaDirective;

public sealed record ItemStatusAnnouncement(Guid ItemId, ItemKind Kind, ItemStatus Status, CancelState Cancel) : SagaDirective;

public sealed record TripConfirmedAnnouncement(Money Total, IReadOnlyList<ConfirmedItem> Items) : SagaDirective;

public sealed record ConfirmedItem(Guid ItemId, ItemKind Kind, Money Total);

public sealed record TripFailedAnnouncement(FailureReason Reason) : SagaDirective;

public sealed record ItemCancelledAnnouncement(Guid ItemId, Money Refund) : SagaDirective;

public sealed record ItemRemovedAnnouncement(Guid ItemId, ItemKind Kind) : SagaDirective;

public sealed record TripClaimedAnnouncement(Guid TravellerId) : SagaDirective;
