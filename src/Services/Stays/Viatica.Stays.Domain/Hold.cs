namespace Viatica.Stays.Domain;

public enum HoldStatus
{
    /// <summary>Rooms are reserved on every night and expire at <see cref="Hold.ExpiresAt"/>.</summary>
    Held,

    /// <summary>The hold became a sale: the rooms moved from held to sold.</summary>
    Committed,

    /// <summary>Given back before it expired, by the saga.</summary>
    Released,

    /// <summary>Given back by the sweeper because nobody committed in time.</summary>
    Expired,

    /// <summary>A committed stay was cancelled and its rooms are on sale again.</summary>
    Cancelled,
}

/// <summary>
/// A reservation of <see cref="Quantity"/> rooms on every night of a stay. Its id is minted by Booking, so a request
/// delivered twice finds the row it already created.
/// </summary>
/// <remarks>
/// Moves between states are compare-and-set statements (<c>UPDATE ... WHERE status = 'Held'</c>), not methods on this
/// class: the saga and the expiry sweeper race on the same row, and only the database can say who won.
/// </remarks>
public sealed class Hold
{
    /// <summary>Fifteen minutes to commit, the same as a quote's life, so a traveller who took a quote can still hold it.</summary>
    public static readonly TimeSpan Lifetime = TimeSpan.FromMinutes(15);

    private Hold()
    {
    }

    public Guid Id { get; private set; }

    public Guid TripId { get; private set; }

    public Guid ItemId { get; private set; }

    public Guid PropertyId { get; private set; }

    public Guid RoomTypeId { get; private set; }

    public DateOnly CheckIn { get; private set; }

    public DateOnly CheckOut { get; private set; }

    public int Quantity { get; private set; }

    /// <summary>The total the quote pinned, in euro. It is what Booking charges, whatever the rates say now.</summary>
    public decimal Total { get; private set; }

    public HoldStatus Status { get; private set; }

    public DateTimeOffset ExpiresAt { get; private set; }

    public DateTimeOffset CreatedAt { get; private set; }

    public DateTimeOffset UpdatedAt { get; private set; }

    public StayDates Dates => new(CheckIn, CheckOut);

    public static Hold Place(
        Guid id, Guid tripId, Guid itemId, Guid propertyId, Guid roomTypeId,
        StayDates dates, int quantity, decimal total, DateTimeOffset now, TimeSpan lifetime) =>
        new()
        {
            Id = id,
            TripId = tripId,
            ItemId = itemId,
            PropertyId = propertyId,
            RoomTypeId = roomTypeId,
            CheckIn = dates.CheckIn,
            CheckOut = dates.CheckOut,
            Quantity = quantity,
            Total = total,
            Status = HoldStatus.Held,
            CreatedAt = now,
            UpdatedAt = now,
            ExpiresAt = now + lifetime,
        };
}
