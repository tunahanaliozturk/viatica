namespace Viatica.Cars.Domain;

public enum HoldStatus
{
    /// <summary>One car of the category is promised on every rental day until <see cref="CarHold.ExpiresAt"/>.</summary>
    Held,

    /// <summary>The booking is confirmed: the car counts as sold.</summary>
    Committed,

    /// <summary>Booking gave the car back before confirming.</summary>
    Released,

    /// <summary>Nobody confirmed in time; the sweeper gave the car back.</summary>
    Expired,

    /// <summary>A confirmed booking was cancelled and the car is available again.</summary>
    Cancelled,
}

/// <summary>
/// One car of one category held, then sold, for a rental. Its identity is the <c>HoldId</c> Booking generated, so a
/// request that arrives twice finds the hold it already made.
/// </summary>
public sealed class CarHold
{
    public static readonly TimeSpan Lifetime = TimeSpan.FromMinutes(15);

    private CarHold()
    {
    }

    public Guid Id { get; private set; }

    public Guid TripId { get; private set; }

    public Guid ItemId { get; private set; }

    public Guid LocationId { get; private set; }

    public Guid CategoryId { get; private set; }

    public Guid DropOffLocationId { get; private set; }

    public DateTimeOffset PickUpAt { get; private set; }

    public DateTimeOffset DropOffAt { get; private set; }

    /// <summary>First calendar day the car is out, at the pick-up location; the inventory counts from here.</summary>
    public DateOnly FirstDay { get; private set; }

    public int Days { get; private set; }

    /// <summary>The price Booking was quoted and will charge, in euros.</summary>
    public decimal Total { get; private set; }

    public HoldStatus Status { get; private set; }

    public DateTimeOffset ExpiresAt { get; private set; }

    public DateTimeOffset CreatedAt { get; private set; }

    public DateTimeOffset UpdatedAt { get; private set; }

    /// <summary>Postgres <c>xmin</c>. Two writers moving one hold at once cannot both succeed.</summary>
    public uint Version { get; private set; }

    public DateOnly LastDay => FirstDay.AddDays(Days - 1);

    public static CarHold Place(
        Guid id, Guid tripId, Guid itemId, Guid locationId, Guid categoryId, Guid dropOffLocationId,
        RentalPeriod period, decimal total, DateTimeOffset now)
    {
        ArgumentNullException.ThrowIfNull(period);

        return new CarHold
        {
            Id = id,
            TripId = tripId,
            ItemId = itemId,
            LocationId = locationId,
            CategoryId = categoryId,
            DropOffLocationId = dropOffLocationId,
            PickUpAt = period.PickUpAt,
            DropOffAt = period.DropOffAt,
            FirstDay = period.FirstDay,
            Days = period.Days,
            Total = total,
            Status = HoldStatus.Held,
            ExpiresAt = now + Lifetime,
            CreatedAt = now,
            UpdatedAt = now,
        };
    }

    public bool Commit(DateTimeOffset now) => Move(HoldStatus.Held, HoldStatus.Committed, now);

    public bool Release(DateTimeOffset now) => Move(HoldStatus.Held, HoldStatus.Released, now);

    public bool Expire(DateTimeOffset now) => Move(HoldStatus.Held, HoldStatus.Expired, now);

    public bool Cancel(DateTimeOffset now) => Move(HoldStatus.Committed, HoldStatus.Cancelled, now);

    // False means "already past that point or never there": the caller decides whether that is a repeat to answer
    // again or an event to ignore. The aggregate never throws for a state it has left, because redelivery is normal.
    private bool Move(HoldStatus from, HoldStatus to, DateTimeOffset now)
    {
        if (Status != from)
        {
            return false;
        }

        Status = to;
        UpdatedAt = now;
        return true;
    }
}
