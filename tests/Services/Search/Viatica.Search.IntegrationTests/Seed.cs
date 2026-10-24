using Viatica.Contracts;

namespace Viatica.Search.IntegrationTests;

/// <summary>A room type for a test property. <see cref="Units"/> and <see cref="Rate"/> apply to every night seeded.</summary>
public sealed record RoomSpec(string Name, int MaxOccupancy, int Units, decimal Rate)
{
    public Guid Id { get; } = Guid.CreateVersion7();
}

/// <summary>Builds the events a service would publish for a property or a rental location.</summary>
public static class Seed
{
    public static readonly Guid StayPartner = Guid.Parse("c0000000-0000-4000-8000-000000000001");
    public static readonly Guid CarPartner = Guid.Parse("c0000000-0000-4000-8000-000000000011");
    public static readonly Guid OtherCarPartner = Guid.Parse("c0000000-0000-4000-8000-000000000012");

    public static StayListingUpdated Listing(
        Guid propertyId,
        long version,
        string name,
        string city,
        int stars,
        double lat,
        double lon,
        IReadOnlyList<string> amenities,
        params RoomSpec[] rooms) =>
        new(
            propertyId,
            StayPartner,
            version,
            name,
            $"{name} in {city}.",
            new Address("1 Main Street", city, null, "1000", "PT"),
            new GeoPoint(lat, lon),
            stars,
            amenities,
            new TimeOnly(15, 0),
            new TimeOnly(11, 0),
            new CancellationPolicy(48, CancellationPenalty.FirstUnit),
            [.. rooms.Select(static r => new RoomTypeSnapshot(r.Id, r.Name, r.MaxOccupancy, "1 double", []))],
            DateTimeOffset.UtcNow);

    /// <summary>The nights of one room type from the base date, all at the room's units and rate.</summary>
    public static StayAvailabilityChanged Nights(Guid propertyId, RoomSpec room, long version, int from = 0, int count = 60) =>
        new(propertyId, room.Id, version, Range(from, count, room.Units, room.Rate));

    /// <summary>A property with everything it needs to be found: the listing, and nights for every room type.</summary>
    public static IIntegrationEvent[] Property(
        Guid propertyId,
        string name,
        string city,
        int stars,
        double lat,
        double lon,
        IReadOnlyList<string> amenities,
        params RoomSpec[] rooms) =>
    [
        Listing(propertyId, 1, name, city, stars, lat, lon, amenities, rooms),
        .. rooms.Select(room => (IIntegrationEvent)Nights(propertyId, room, 1)),
    ];

    public static IReadOnlyList<DateAvailability> Range(int from, int count, int units, decimal rate) =>
        [.. Enumerable.Range(from, count).Select(i => new DateAvailability(SearchApp.Base.AddDays(i), units, new Price(rate, "EUR")))];

    public static CarLocationUpdated LocationListing(
        Guid locationId,
        string name,
        string city,
        double lat,
        double lon,
        Guid partnerId,
        string? airport,
        params CarSpec[] categories) =>
        new(
            locationId,
            partnerId,
            1,
            name,
            new Address("Airport Road", city, null, "1700", "PT"),
            new GeoPoint(lat, lon),
            airport,
            [],
            new CancellationPolicy(24, CancellationPenalty.FirstUnit),
            [.. categories.Select(static c => new CarCategorySnapshot(c.Id, c.Acriss, c.Name, c.Seats, c.Transmission, FuelType.Petrol, 2, 21, null))],
            DateTimeOffset.UtcNow);

    public static CarAvailabilityChanged Days(Guid locationId, CarSpec category, long version, int from = 0, int count = 60) =>
        new(locationId, category.Id, version, Range(from, count, category.Units, category.Rate));

    public static IIntegrationEvent[] Location(
        Guid locationId, string name, string city, double lat, double lon, Guid partnerId, string? airport, params CarSpec[] categories) =>
    [
        LocationListing(locationId, name, city, lat, lon, partnerId, airport, categories),
        .. categories.Select(category => (IIntegrationEvent)Days(locationId, category, 1)),
    ];
}

public sealed record CarSpec(string Acriss, string Name, int Seats, Transmission Transmission, int Units, decimal Rate)
{
    public Guid Id { get; } = Guid.CreateVersion7();
}
