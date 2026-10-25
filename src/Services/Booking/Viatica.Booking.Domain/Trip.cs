using System.Security.Cryptography;
using Viatica.Kernel;

namespace Viatica.Booking.Domain;

public sealed record CheckoutResult(CheckoutOutcome Outcome, IReadOnlyList<SagaDirective> Directives);

public enum AddItemOutcome
{
    Added,

    /// <summary>The same item was added before. Nothing changed.</summary>
    AlreadyThere,
}

/// <summary>
/// A traveller's basket and, once it is checked out, the checkout saga. Saga state lives here and on the items, as
/// docs/requirements.md section 6 says: there is no engine, only a state machine in which every transition has
/// exactly one cause and replaying that cause is a no-op.
/// </summary>
/// <remarks>
/// Every public method that changes the trip returns the directives the saga must send, in the order they were
/// decided. A method called twice with the same cause returns an empty list the second time and leaves the trip as
/// it was, which is what makes a redelivered or reordered event harmless. The stages are in <c>Trip.Saga.cs</c>,
/// what each owner's answer means in <c>Trip.Events.cs</c>, and a traveller's cancellation in <c>Trip.Cancellation.cs</c>.
/// </remarks>
public sealed partial class Trip
{
    public const int MaxItems = 10;

    private readonly List<TripItem> items = [];
    private readonly List<SagaDirective> outgoing = [];
    private bool changed;

    private Trip()
    {
    }

    public Guid Id { get; private set; }

    /// <summary>The signed-in owner. Null while the trip is anonymous.</summary>
    public Guid? TravellerId { get; private set; }

    /// <summary>
    /// SHA-256 of the secret that proves a holder is the creator of an anonymous trip. The id alone is not enough:
    /// a client chooses it, and a client may choose badly. Cleared when the trip is claimed.
    /// </summary>
    public byte[]? AccessKeyHash { get; private set; }

    public TripStatus Status { get; private set; }

    public IReadOnlyList<TripItem> Items => items;

    public DateTimeOffset CreatedAt { get; private set; }

    public DateTimeOffset UpdatedAt { get; private set; }

    /// <summary>Counts every change, so a stream of progress can send only what is newer than what it sent.</summary>
    public int Revision { get; private set; }

    /// <summary>Postgres <c>xmin</c>: the row version, and the ETag.</summary>
    public uint Version { get; private set; }

    // Checkout.

    /// <summary>
    /// A keyed hash of the checkout request, so a repeat with the same body changes nothing and a repeat with another
    /// body is a conflict. Keyed, because the body is made of names and a plain hash of a name can be guessed.
    /// </summary>
    public string? CheckoutFingerprint { get; private set; }

    public DateTimeOffset? CheckoutStartedAt { get; private set; }

    public Guid PaymentId { get; private set; }

    public PaymentStatus Payment { get; private set; }

    public decimal? PaymentAmount { get; private set; }

    public string Currency { get; private set; } = Money.Euro;

    /// <summary>
    /// The contact details, the passenger list and the payment token, sealed (AES-256-GCM, key held by Booking only).
    /// Present while the saga runs; erased when the trip is confirmed or failed, because nothing after that needs
    /// them. A draft has none.
    /// </summary>
    public string? SealedTravellerData { get; private set; }

    public FailureReason? Failure { get; private set; }

    /// <summary>The instant a trip before <c>Committing</c> unwinds on its own: the shortest expiry known, less the margin.</summary>
    public DateTimeOffset? Deadline { get; private set; }

    public TimeSpan DeadlineMargin { get; private set; }

    public TimeSpan AssumedHoldLifetime { get; private set; }

    public TimeSpan ResendAfter { get; private set; }

    /// <summary>When a compensating request last went out, so an unanswered one is sent again after <see cref="ResendAfter"/>.</summary>
    public DateTimeOffset? CompensationSentAt { get; private set; }

    /// <summary>The earliest instant the scheduled check has anything to do for this trip. Indexed.</summary>
    public DateTimeOffset? NextCheckAt { get; private set; }

    public static Trip Create(Guid id, Guid? travellerId, byte[]? accessKeyHash, DateTimeOffset now)
    {
        var trip = new Trip
        {
            Id = id,
            TravellerId = travellerId,
            AccessKeyHash = travellerId is null ? accessKeyHash : null,
            Status = TripStatus.Draft,
            CreatedAt = now,
            UpdatedAt = now,
        };

        trip.Announce(new TripStatusAnnouncement(TripStatus.Draft));
        return trip;
    }

    public bool IsOwnedBy(Guid travellerId) => TravellerId == travellerId;

    /// <summary>True when <paramref name="hash"/> is the access key of this anonymous trip. Constant time.</summary>
    public bool AccessKeyMatches(byte[] hash)
    {
        ArgumentNullException.ThrowIfNull(hash);
        return AccessKeyHash is { } own && CryptographicOperations.FixedTimeEquals(own, hash);
    }

    /// <summary>What the trip costs now: what is confirmed or asked for, less the items cancelled since.</summary>
    public Money CurrentTotal => Sum(items.Where(static i => i.Status is not (ItemStatus.Cancelled or ItemStatus.Rejected)), static i => i.Total ?? i.ExpectedTotal);

    public Money RefundedTotal => Sum(items.Where(static i => i.RefundAmount is not null), i => new Money(i.RefundAmount!.Value, i.Currency));

    /// <summary>
    /// What the last draft change (creating the trip, adding or removing an item, claiming it) decided, for the caller
    /// to queue. The saga's own methods return their directives instead.
    /// </summary>
    public IReadOnlyList<SagaDirective> TakeDirectives() => Flush();

    // Building the basket (BKG-1).

    public AddItemOutcome AddItem(NewItem item, DateTimeOffset now)
    {
        ArgumentNullException.ThrowIfNull(item);
        EnsureDraft();

        if (items.Find(i => i.Id == item.ItemId) is { } existing)
        {
            return existing.Matches(item) ? AddItemOutcome.AlreadyThere : throw BookingErrors.ItemConflict();
        }

        if (items.Count >= MaxItems)
        {
            throw BookingErrors.TripFull(MaxItems);
        }

        TripItem added = TripItem.Create(Id, item, now);
        items.Add(added);
        Announce(new ItemStatusAnnouncement(added.Id, added.Kind, ItemStatus.Pending, CancelState.None));
        Stamp(now);
        return AddItemOutcome.Added;
    }

    /// <summary>Removes an item from a draft. Removing one that is not there is not an error: the caller wanted it gone.</summary>
    public bool RemoveItem(Guid itemId, DateTimeOffset now)
    {
        EnsureDraft();

        if (items.Find(i => i.Id == itemId) is not { } item)
        {
            return false;
        }

        items.Remove(item);
        Announce(new ItemRemovedAnnouncement(item.Id, item.Kind));
        Stamp(now);
        return true;
    }

    /// <summary>A traveller who signs in takes the trip they built anonymously. Claiming your own trip again is a no-op.</summary>
    public void Claim(Guid travellerId, DateTimeOffset now)
    {
        if (TravellerId == travellerId)
        {
            return;
        }

        if (TravellerId is not null)
        {
            throw BookingErrors.TripAlreadyClaimed();
        }

        TravellerId = travellerId;
        AccessKeyHash = null;
        Announce(new TripClaimedAnnouncement(travellerId));
        Stamp(now);
    }

    // Checkout (BKG-2, BKG-3).

    public CheckoutResult Checkout(
        string fingerprint, string sealedTravellerData, Guid paymentId, DateTimeOffset now, SagaTimings timings)
    {
        ArgumentException.ThrowIfNullOrEmpty(fingerprint);
        ArgumentException.ThrowIfNullOrEmpty(sealedTravellerData);
        ArgumentNullException.ThrowIfNull(timings);

        if (Status != TripStatus.Draft)
        {
            return new CheckoutResult(
                CheckoutFingerprint is { } own && CryptographicOperations.FixedTimeEquals(
                    System.Text.Encoding.UTF8.GetBytes(own), System.Text.Encoding.UTF8.GetBytes(fingerprint))
                    ? CheckoutOutcome.Repeated
                    : CheckoutOutcome.Conflict,
                []);
        }

        if (TravellerId is null)
        {
            throw BookingErrors.TripNotFound();
        }

        if (items.Count == 0)
        {
            throw BookingErrors.TripEmpty();
        }

        if (items.Find(i => i.QuoteExpiresAt is { } expires && expires <= now) is { } stale)
        {
            throw BookingErrors.QuoteExpired(stale.Id);
        }

        CheckoutFingerprint = fingerprint;
        SealedTravellerData = sealedTravellerData;
        PaymentId = paymentId;
        CheckoutStartedAt = now;
        DeadlineMargin = timings.DeadlineMargin;
        AssumedHoldLifetime = timings.AssumedHoldLifetime;
        ResendAfter = timings.ResendAfter;
        Currency = items[0].Currency;
        SetStatus(TripStatus.Holding);

        foreach (TripItem item in items)
        {
            item.Awaiting = ItemRequest.Hold;
            Send(item.Kind switch
            {
                ItemKind.Stay => new RequestStayHold(item.Id, item.HoldId, item.Reference, item.Quantity),
                ItemKind.Car => new RequestCarHold(item.Id, item.HoldId, item.Reference, item.DriverAge ?? 0),
                _ => new RequestFlightOrder(item.Id, item.Reference, item.ExpectedTotal),
            });
        }

        return new CheckoutResult(CheckoutOutcome.Started, Complete(now));
    }

    // Plumbing shared by the partial files.

    private void EnsureDraft()
    {
        if (Status != TripStatus.Draft)
        {
            throw BookingErrors.TripNotEditable(Status);
        }
    }

    private TripItem? Find(Guid itemId) => items.Find(i => i.Id == itemId);

    // Requests and announcements share one ordered list on purpose: the outbox keeps the order they were decided in.
    private void Send(SagaDirective directive) => Announce(directive);

    private void Announce(SagaDirective directive)
    {
        outgoing.Add(directive);
        changed = true;
    }

    private void SetStatus(TripStatus status)
    {
        if (Status == status)
        {
            return;
        }

        Status = status;
        Announce(new TripStatusAnnouncement(status));
    }

    private void Move(TripItem item, ItemStatus status)
    {
        if (item.Status == status)
        {
            return;
        }

        item.Move(status);
        Announce(new ItemStatusAnnouncement(item.Id, item.Kind, status, item.Cancel));
    }

    /// <summary>Stamps the trip as changed if anything changed, and hands out what was decided.</summary>
    private SagaDirective[] Complete(DateTimeOffset now)
    {
        Stamp(now);
        return Flush();
    }

    /// <summary>Counts the change and refreshes the clocks, leaving the directives for <see cref="TakeDirectives"/>.</summary>
    private void Stamp(DateTimeOffset now)
    {
        if (!changed)
        {
            return;
        }

        Revision++;
        UpdatedAt = now;
        RecomputeDeadline();
        RecomputeNextCheck();
        changed = false;
    }

    private SagaDirective[] Flush()
    {
        if (outgoing.Count == 0)
        {
            return [];
        }

        SagaDirective[] decided = [.. outgoing];
        outgoing.Clear();
        return decided;
    }

    private Money Sum(IEnumerable<TripItem> source, Func<TripItem, Money> amount)
    {
        List<Money> parts = [.. source.Select(amount)];
        return parts.Count == 0 ? Money.Zero(Currency) : parts.Aggregate(static (a, b) => a + b);
    }
}
