using Viatica.Kernel;

namespace Viatica.Booking.Domain;

/// <summary>What the traveller asked for when adding an item. Two requests with the same values are the same request.</summary>
public sealed record NewItem(
    Guid ItemId,
    ItemKind Kind,
    string Reference,
    string Description,
    Money ExpectedTotal,
    int Quantity,
    int? DriverAge,
    DateTimeOffset? QuoteExpiresAt);

/// <summary>What the owner of a stay or car said about a hold it placed, copied onto the item (BKG-4, BKG-5).</summary>
public sealed record ItemTerms(
    Guid? PartnerId,
    DateTimeOffset? StartsAt,
    int Units,
    int? FreeCancellationHours,
    Penalty? Penalty);

/// <summary>
/// One stay, car or flight on a trip. The trip drives every change (the setters are internal), because whether an
/// answer from an owner means anything depends on the trip's stage as much as on the item's own state.
/// </summary>
public sealed class TripItem
{
    private TripItem()
    {
    }

    public Guid Id { get; private set; }

    public Guid TripId { get; private set; }

    public ItemKind Kind { get; private set; }

    public ItemStatus Status { get; private set; }

    /// <summary>The one request to the owner that is still unanswered, or <see cref="ItemRequest.None"/>.</summary>
    public ItemRequest Awaiting { get; internal set; }

    /// <summary>The quote token (stays and cars) or the re-priced offer id (flights). Opaque to Booking.</summary>
    public string Reference { get; private set; } = "";

    /// <summary>A line for people, built from the quote's own readable fields. Never a passenger or contact detail.</summary>
    public string Description { get; private set; } = "";

    public decimal ExpectedAmount { get; private set; }

    public string Currency { get; private set; } = "";

    public Money ExpectedTotal => new(ExpectedAmount, Currency);

    public int Quantity { get; private set; }

    public int? DriverAge { get; private set; }

    public DateTimeOffset? QuoteExpiresAt { get; private set; }

    /// <summary>The id Booking gives a stay or car hold, so every request about it is repeatable by id.</summary>
    public Guid HoldId { get; private set; }

    /// <summary>The airline order, known once Flights has created it.</summary>
    public Guid? OrderId { get; internal set; }

    /// <summary>The price the owner confirmed. It, and not the quote, is what the traveller pays.</summary>
    public decimal? TotalAmount { get; internal set; }

    public Money? Total => TotalAmount is { } amount ? new Money(amount, Currency) : null;

    /// <summary>When the hold lapses (stays, cars) or the airline's ticketing time limit (flights).</summary>
    public DateTimeOffset? ExpiresAt { get; internal set; }

    public Guid? PartnerId { get; internal set; }

    public DateTimeOffset? StartsAt { get; internal set; }

    /// <summary>Nights of a stay or rental days: what a total is divided by for a first-unit penalty.</summary>
    public int Units { get; internal set; } = 1;

    public int? FreeCancellationHours { get; internal set; }

    public Penalty? Penalty { get; internal set; }

    /// <summary>Why the owner turned the item down, or the airline refused to ticket it. For operators.</summary>
    public string? Refusal { get; internal set; }

    public CancelState Cancel { get; internal set; }

    public DateTimeOffset? CancelRequestedAt { get; internal set; }

    public DateTimeOffset? CancelDeadline { get; internal set; }

    public Guid? RefundId { get; internal set; }

    public decimal? RefundAmount { get; internal set; }

    public RefundStatus Refund { get; internal set; }

    public DateTimeOffset AddedAt { get; private set; }

    public bool IsStayOrCar => Kind != ItemKind.Flight;

    /// <summary>The owner has the item for us: a held room or car, an ordered or ticketed flight, or a confirmed one.</summary>
    internal bool IsSecured => Status is ItemStatus.Held or ItemStatus.Committed or ItemStatus.Ordered or ItemStatus.Ticketed;

    /// <summary>The item cannot be part of this checkout any more: the owner refused, or its hold is gone.</summary>
    internal bool IsLost => Status is ItemStatus.Rejected or ItemStatus.Expired || Refusal is not null && Kind == ItemKind.Flight && Status == ItemStatus.Ordered;

    /// <summary>
    /// Nothing is left to give back and nothing is left to hear: a terminal status and no unanswered request.
    /// While an unwinding trip has an item that is not resolved it stays <c>Unwinding</c>.
    /// </summary>
    internal bool IsResolved =>
        Awaiting == ItemRequest.None && (IsStayOrCar
            ? Status is ItemStatus.Rejected or ItemStatus.Released or ItemStatus.Expired or ItemStatus.Cancelled
            : Status is ItemStatus.Rejected or ItemStatus.Voided or ItemStatus.Cancelled);

    internal static TripItem Create(Guid tripId, NewItem item, DateTimeOffset now) => new()
    {
        Id = item.ItemId,
        TripId = tripId,
        Kind = item.Kind,
        Status = ItemStatus.Pending,
        Reference = item.Reference,
        Description = item.Description,
        ExpectedAmount = item.ExpectedTotal.Amount,
        Currency = item.ExpectedTotal.Currency,
        Quantity = item.Quantity,
        DriverAge = item.DriverAge,
        QuoteExpiresAt = item.QuoteExpiresAt,
        HoldId = Guid.CreateVersion7(),
        AddedAt = now,
    };

    internal bool Matches(NewItem item) =>
        Kind == item.Kind && Reference == item.Reference && Quantity == item.Quantity && DriverAge == item.DriverAge
        && ExpectedAmount == item.ExpectedTotal.Amount && Currency == item.ExpectedTotal.Currency;

    internal void Move(ItemStatus status) => Status = status;

    internal void Record(Money total, DateTimeOffset expiresAt)
    {
        TotalAmount = total.Amount;
        ExpiresAt = expiresAt;
    }

    internal void Record(ItemTerms terms)
    {
        PartnerId = terms.PartnerId;
        StartsAt = terms.StartsAt;
        Units = Math.Max(terms.Units, 1);
        FreeCancellationHours = terms.FreeCancellationHours;
        Penalty = terms.Penalty;
    }
}
