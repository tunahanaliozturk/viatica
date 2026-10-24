using Moongazing.OrionGuard.Core;

namespace Viatica.Stays.Domain;

/// <summary>
/// A postal address. The region is the one part a country may not have, so it is last and a body may leave it out;
/// every other member is required.
/// </summary>
public sealed record Address(string Line1, string City, string PostalCode, string CountryCode, string? Region = null);

public sealed record GeoPoint(double Latitude, double Longitude);

public enum CancellationPenalty
{
    /// <summary>The first night is kept.</summary>
    FirstUnit,

    /// <summary>Nothing is refunded.</summary>
    FullAmount,
}

public sealed record CancellationPolicy(int FreeUntilHoursBeforeStart, CancellationPenalty PenaltyAfter);

/// <summary>
/// What a partner writes about a property. It is the request body of a create, so the shape a client sends is the
/// shape the domain keeps; the validator in Application is what stands between the two.
/// </summary>
public sealed record PropertyDetails(
    string Name,
    string Description,
    Address Address,
    GeoPoint Location,
    int StarRating,
    IReadOnlyList<string> Amenities,
    TimeOnly CheckInFrom,
    TimeOnly CheckOutUntil,
    CancellationPolicy CancellationPolicy) : IEquatable<PropertyDetails>
{
    // A record compares a list by reference, so two identical amenity lists would make two different details and a
    // retried PUT would look like a conflicting one.
    public bool Equals(PropertyDetails? other) =>
        other is not null
        && Name == other.Name
        && Description == other.Description
        && Address == other.Address
        && Location == other.Location
        && StarRating == other.StarRating
        && Amenities.SequenceEqual(other.Amenities, StringComparer.Ordinal)
        && CheckInFrom == other.CheckInFrom
        && CheckOutUntil == other.CheckOutUntil
        && CancellationPolicy == other.CancellationPolicy;

    public override int GetHashCode() => HashCode.Combine(Name, Address, Location, StarRating, Amenities.Count, CheckInFrom, CheckOutUntil);
}

public enum PropertyStatus
{
    Draft,
    Published,
    Unpublished,
}

/// <summary>
/// A hotel, apartment block or hostel that one partner owns. Catalogue editing is plain CRUD; what the class adds
/// is the publish rule and the <see cref="ListingVersion"/> that lets the search projection drop stale snapshots.
/// </summary>
public sealed class StayProperty
{
    private StayProperty()
    {
    }

    public Guid Id { get; private set; }

    public Guid PartnerId { get; private set; }

    public string Name { get; private set; } = "";

    public string Description { get; private set; } = "";

    public string AddressLine1 { get; private set; } = "";

    public string City { get; private set; } = "";

    public string? Region { get; private set; }

    public string PostalCode { get; private set; } = "";

    public string CountryCode { get; private set; } = "";

    public double Latitude { get; private set; }

    public double Longitude { get; private set; }

    public int StarRating { get; private set; }

    public IReadOnlyList<string> Amenities { get; private set; } = [];

    public TimeOnly CheckInFrom { get; private set; }

    public TimeOnly CheckOutUntil { get; private set; }

    public int FreeCancellationHours { get; private set; }

    public CancellationPenalty PenaltyAfterFreeCancellation { get; private set; }

    public PropertyStatus Status { get; private set; }

    /// <summary>
    /// Goes up by one with every change to the property or to any of its room types, published or not, so the
    /// numbers a consumer sees never repeat or fall back. The row's concurrency token makes two writers that read
    /// the same version lose one of them instead of both claiming the next number.
    /// </summary>
    public long ListingVersion { get; private set; }

    public DateTimeOffset CreatedAt { get; private set; }

    public DateTimeOffset UpdatedAt { get; private set; }

    /// <summary>Postgres <c>xmin</c>: the row version behind the ETag.</summary>
    public uint Version { get; private set; }

    public ICollection<RoomType> RoomTypes { get; private set; } = [];

    public bool IsPublished => Status == PropertyStatus.Published;

    public PropertyDetails Details => new(
        Name,
        Description,
        new Address(AddressLine1, City, PostalCode, CountryCode, Region),
        new GeoPoint(Latitude, Longitude),
        StarRating,
        Amenities,
        CheckInFrom,
        CheckOutUntil,
        new CancellationPolicy(FreeCancellationHours, PenaltyAfterFreeCancellation));

    public static StayProperty Create(Guid id, Guid partnerId, PropertyDetails details, DateTimeOffset now)
    {
        Ensure.That(id, nameof(id)).NotDefault();
        Ensure.That(partnerId, nameof(partnerId)).NotDefault();

        var property = new StayProperty
        {
            Id = id,
            PartnerId = partnerId,
            Status = PropertyStatus.Draft,
            ListingVersion = 1,
            CreatedAt = now,
        };
        property.Apply(details, now);
        return property;
    }

    /// <summary>Replaces what the partner wrote. The version moves even while the property is a draft.</summary>
    public void Revise(PropertyDetails details, DateTimeOffset now)
    {
        Apply(details, now);
        ListingVersion++;
    }

    /// <summary>A room type was added or changed: the listing is a new version of itself.</summary>
    public void RoomTypesChanged(DateTimeOffset now)
    {
        ListingVersion++;
        UpdatedAt = now;
    }

    /// <summary>Publishes a draft or an unpublished property. Returns false when it already was published.</summary>
    /// <remarks>A property with no room type has nothing to sell, so it cannot reach search.</remarks>
    public bool Publish(DateTimeOffset now)
    {
        if (IsPublished)
        {
            return false;
        }

        if (RoomTypes.Count == 0)
        {
            throw StayErrors.NoRoomTypes();
        }

        Status = PropertyStatus.Published;
        ListingVersion++;
        UpdatedAt = now;
        return true;
    }

    /// <summary>Takes a published property off sale. Returns false when it was not published.</summary>
    public bool Unpublish(DateTimeOffset now)
    {
        if (!IsPublished)
        {
            return false;
        }

        Status = PropertyStatus.Unpublished;
        ListingVersion++;
        UpdatedAt = now;
        return true;
    }

    private void Apply(PropertyDetails details, DateTimeOffset now)
    {
        ArgumentNullException.ThrowIfNull(details);
        Ensure.That(details.Name, nameof(details.Name)).NotEmpty();
        Ensure.That(details.StarRating, nameof(details.StarRating)).InRange(0, 5);

        Name = details.Name;
        Description = details.Description;
        AddressLine1 = details.Address.Line1;
        City = details.Address.City;
        Region = details.Address.Region;
        PostalCode = details.Address.PostalCode;
        CountryCode = details.Address.CountryCode;
        Latitude = details.Location.Latitude;
        Longitude = details.Location.Longitude;
        StarRating = details.StarRating;
        Amenities = [.. details.Amenities];
        CheckInFrom = details.CheckInFrom;
        CheckOutUntil = details.CheckOutUntil;
        FreeCancellationHours = details.CancellationPolicy.FreeUntilHoursBeforeStart;
        PenaltyAfterFreeCancellation = details.CancellationPolicy.PenaltyAfter;
        UpdatedAt = now;
    }
}
