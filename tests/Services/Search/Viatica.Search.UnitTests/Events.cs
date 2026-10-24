using Viatica.Contracts;

namespace Viatica.Search.UnitTests;

/// <summary>Builds the events a test needs without every field spelled out.</summary>
internal static class Events
{
    public static readonly DateOnly Today = new(2026, 11, 1);

    public static int FirstDay => Today.DayNumber - 1;

    public static StayListingUpdated Listing(
        Guid propertyId,
        long version,
        string name = "Hotel Lisboa",
        string city = "Lisbon",
        int stars = 4,
        string[]? amenities = null,
        params Guid[] roomTypes) =>
        new(
            propertyId,
            Guid.Parse("c0000000-0000-4000-8000-000000000001"),
            version,
            name,
            "A hotel.",
            new Address("1 Main Street", city, null, "1000", "PT"),
            new GeoPoint(38.72, -9.14),
            stars,
            amenities ?? ["wifi", "pool"],
            new TimeOnly(15, 0),
            new TimeOnly(11, 0),
            new CancellationPolicy(48, CancellationPenalty.FirstUnit),
            [.. roomTypes.Select(static (id, i) => new RoomTypeSnapshot(id, $"Room {i + 1}", 2, "1 double", []))],
            new DateTimeOffset(2026, 10, 1, 0, 0, 0, TimeSpan.Zero));

    public static StayUnpublished Unpublished(Guid propertyId, long version) => new(propertyId, version);

    public static StayAvailabilityChanged Nights(Guid propertyId, Guid roomTypeId, long version, DateOnly first, int count, int available, decimal rate) =>
        new(propertyId, roomTypeId, version, [.. Enumerable.Range(0, count).Select(i => new DateAvailability(first.AddDays(i), available, new Price(rate, "EUR")))]);

    public static CarLocationUpdated Location(Guid locationId, long version, Guid? partnerId = null, params Guid[] categories) =>
        new(
            locationId,
            partnerId ?? Guid.Parse("c0000000-0000-4000-8000-000000000002"),
            version,
            "Lisbon Airport",
            new Address("Airport Road", "Lisbon", null, "1700", "PT"),
            new GeoPoint(38.77, -9.13),
            "LIS",
            [],
            new CancellationPolicy(24, CancellationPenalty.FirstUnit),
            [.. categories.Select(static (id, i) => new CarCategorySnapshot(id, $"ECMR{i}", $"Compact {i}", 5, i % 2 == 0 ? Transmission.Manual : Transmission.Automatic, FuelType.Petrol, 2, 21, null))],
            new DateTimeOffset(2026, 10, 1, 0, 0, 0, TimeSpan.Zero));

    public static CarAvailabilityChanged Days(Guid locationId, Guid categoryId, long version, DateOnly first, int count, int available, decimal rate) =>
        new(locationId, categoryId, version, [.. Enumerable.Range(0, count).Select(i => new DateAvailability(first.AddDays(i), available, new Price(rate, "EUR")))]);
}
