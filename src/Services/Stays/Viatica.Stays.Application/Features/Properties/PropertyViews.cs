using Viatica.Kernel;
using Viatica.Stays.Domain;

namespace Viatica.Stays.Application.Features.Properties;

/// <summary>A property as the API returns it, room types included. The partner's id stays inside the service.</summary>
public sealed record PropertyView(
    Guid Id,
    PropertyStatus Status,
    long ListingVersion,
    string Name,
    string Description,
    Address Address,
    GeoPoint Location,
    int StarRating,
    IReadOnlyList<string> Amenities,
    TimeOnly CheckInFrom,
    TimeOnly CheckOutUntil,
    CancellationPolicy CancellationPolicy,
    IReadOnlyList<RoomTypeView> RoomTypes,
    DateTimeOffset CreatedAt,
    DateTimeOffset UpdatedAt)
{
    public static PropertyView From(StayProperty property)
    {
        ArgumentNullException.ThrowIfNull(property);
        PropertyDetails details = property.Details;

        return new PropertyView(
            property.Id,
            property.Status,
            property.ListingVersion,
            details.Name,
            details.Description,
            details.Address,
            details.Location,
            details.StarRating,
            details.Amenities,
            details.CheckInFrom,
            details.CheckOutUntil,
            details.CancellationPolicy,
            [.. property.RoomTypes.OrderBy(static r => r.CreatedAt).ThenBy(static r => r.Id).Select(RoomTypeView.From)],
            property.CreatedAt,
            property.UpdatedAt);
    }
}

public sealed record RoomTypeView(
    Guid Id,
    Guid PropertyId,
    string Name,
    int MaxOccupancy,
    string Beds,
    IReadOnlyList<string> Amenities,
    Money BaseRate,
    DateTimeOffset CreatedAt,
    DateTimeOffset UpdatedAt)
{
    public static RoomTypeView From(RoomType roomType)
    {
        ArgumentNullException.ThrowIfNull(roomType);
        return new RoomTypeView(
            roomType.Id,
            roomType.PropertyId,
            roomType.Name,
            roomType.MaxOccupancy,
            roomType.Beds,
            roomType.Amenities,
            Money.Eur(roomType.BaseRate),
            roomType.CreatedAt,
            roomType.UpdatedAt);
    }
}

/// <summary>One line of a property list.</summary>
public sealed record PropertySummary(
    Guid Id,
    string Name,
    string City,
    string CountryCode,
    int StarRating,
    PropertyStatus Status,
    DateTimeOffset UpdatedAt);

/// <summary>
/// The columns a property list filters and sorts on. A projection with settable members rather than the entity, so
/// a page of forty properties does not read forty descriptions and amenity arrays.
/// </summary>
public sealed class PropertyRow
{
    public Guid Id { get; set; }

    public string Name { get; set; } = "";

    public string City { get; set; } = "";

    public string CountryCode { get; set; } = "";

    public int StarRating { get; set; }

    public PropertyStatus Status { get; set; }

    public DateTimeOffset UpdatedAt { get; set; }
}
