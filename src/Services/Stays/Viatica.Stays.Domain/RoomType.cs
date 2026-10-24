using Moongazing.OrionGuard.Core;
using Viatica.Kernel;

namespace Viatica.Stays.Domain;

/// <summary>
/// What a partner writes about a room type. <see cref="BaseRate"/> is the nightly rate used when a bulk update
/// does not name one; the rate a traveller pays for a night is always the one on that night's row.
/// </summary>
public sealed record RoomTypeDetails(
    string Name,
    int MaxOccupancy,
    string Beds,
    IReadOnlyList<string> Amenities,
    Money BaseRate) : IEquatable<RoomTypeDetails>
{
    public bool Equals(RoomTypeDetails? other) =>
        other is not null
        && Name == other.Name
        && MaxOccupancy == other.MaxOccupancy
        && Beds == other.Beds
        && Amenities.SequenceEqual(other.Amenities, StringComparer.Ordinal)
        && BaseRate == other.BaseRate;

    public override int GetHashCode() => HashCode.Combine(Name, MaxOccupancy, Beds, Amenities.Count, BaseRate);
}

/// <summary>A kind of room a property sells, counted per night rather than per physical room.</summary>
public sealed class RoomType
{
    private RoomType()
    {
    }

    public Guid Id { get; private set; }

    public Guid PropertyId { get; private set; }

    public string Name { get; private set; } = "";

    public int MaxOccupancy { get; private set; }

    public string Beds { get; private set; } = "";

    public IReadOnlyList<string> Amenities { get; private set; } = [];

    /// <summary>Euro, like every price on the platform in v1.</summary>
    public decimal BaseRate { get; private set; }

    public DateTimeOffset CreatedAt { get; private set; }

    public DateTimeOffset UpdatedAt { get; private set; }

    /// <summary>Postgres <c>xmin</c>: the row version behind the ETag.</summary>
    public uint Version { get; private set; }

    public StayProperty? Property { get; private set; }

    public RoomTypeDetails Details => new(Name, MaxOccupancy, Beds, Amenities, Money.Eur(BaseRate));

    public static RoomType Create(Guid id, Guid propertyId, RoomTypeDetails details, DateTimeOffset now)
    {
        Ensure.That(id, nameof(id)).NotDefault();
        Ensure.That(propertyId, nameof(propertyId)).NotDefault();

        var roomType = new RoomType { Id = id, PropertyId = propertyId, CreatedAt = now };
        roomType.Apply(details, now);
        return roomType;
    }

    public void Revise(RoomTypeDetails details, DateTimeOffset now) => Apply(details, now);

    /// <summary>Whether <paramref name="guests"/> can sleep in <paramref name="rooms"/> rooms of this type.</summary>
    public void EnsureFits(int guests, int rooms)
    {
        int capacity = MaxOccupancy * rooms;
        if (guests > capacity)
        {
            throw StayErrors.OccupancyExceeded(guests, capacity);
        }
    }

    private void Apply(RoomTypeDetails details, DateTimeOffset now)
    {
        ArgumentNullException.ThrowIfNull(details);
        Ensure.That(details.Name, nameof(details.Name)).NotEmpty();
        Ensure.That(details.MaxOccupancy, nameof(details.MaxOccupancy)).Positive();

        if (details.BaseRate.Currency != Money.Euro)
        {
            throw new ArgumentException("Rates are in EUR.", nameof(details));
        }

        Name = details.Name;
        MaxOccupancy = details.MaxOccupancy;
        Beds = details.Beds;
        Amenities = [.. details.Amenities];
        BaseRate = details.BaseRate.Amount;
        UpdatedAt = now;
    }
}
