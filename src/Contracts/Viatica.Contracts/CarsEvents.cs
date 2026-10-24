using System.Globalization;
using System.Text.Json.Serialization;

namespace Viatica.Contracts;

// Published by Cars on cars.v1. Same shape as stays: versioned listing snapshots keyed by location, hold events
// keyed by trip.

public enum Transmission
{
    Manual,
    Automatic,
}

public enum FuelType
{
    Petrol,
    Diesel,
    Hybrid,
    Electric,
}

public sealed record OpeningHours(DayOfWeek Day, TimeOnly Opens, TimeOnly Closes);

public sealed record CarCategorySnapshot(
    Guid CategoryId,
    string Acriss,
    string Name,
    int Seats,
    Transmission Transmission,
    FuelType Fuel,
    int Bags,
    int MinimumDriverAge,
    Uri? ImageUrl);

public sealed record CarLocationUpdated(
    Guid LocationId,
    Guid PartnerId,
    long ListingVersion,
    string Name,
    Address Address,
    GeoPoint Location,
    string? AirportCode,
    IReadOnlyList<OpeningHours> OpeningHours,
    CancellationPolicy CancellationPolicy,
    IReadOnlyList<CarCategorySnapshot> Categories,
    DateTimeOffset UpdatedAt) : IIntegrationEvent
{
    [JsonIgnore]
    public string PartitionKey => LocationId.ToString("N", CultureInfo.InvariantCulture);
}

public sealed record CarLocationUnpublished(Guid LocationId, long ListingVersion) : IIntegrationEvent
{
    [JsonIgnore]
    public string PartitionKey => LocationId.ToString("N", CultureInfo.InvariantCulture);
}

/// <summary>Rental days of one category at one location whose availability or rate changed.</summary>
public sealed record CarAvailabilityChanged(
    Guid LocationId,
    Guid CategoryId,
    long AvailabilityVersion,
    IReadOnlyList<DateAvailability> Days) : IIntegrationEvent
{
    [JsonIgnore]
    public string PartitionKey => LocationId.ToString("N", CultureInfo.InvariantCulture);
}

public sealed record CarHoldPlaced(
    Guid HoldId,
    Guid TripId,
    Guid ItemId,
    Guid LocationId,
    Guid CategoryId,
    Guid DropOffLocationId,
    DateTimeOffset PickUpAt,
    DateTimeOffset DropOffAt,
    Price Total,
    DateTimeOffset ExpiresAt) : IIntegrationEvent
{
    [JsonIgnore]
    public string PartitionKey => TripId.ToString("N", CultureInfo.InvariantCulture);
}

public sealed record CarHoldRejected(Guid HoldId, Guid TripId, Guid ItemId, HoldRejectionReason Reason) : IIntegrationEvent
{
    [JsonIgnore]
    public string PartitionKey => TripId.ToString("N", CultureInfo.InvariantCulture);
}

public sealed record CarHoldCommitted(Guid HoldId, Guid TripId, Guid ItemId) : IIntegrationEvent
{
    [JsonIgnore]
    public string PartitionKey => TripId.ToString("N", CultureInfo.InvariantCulture);
}

public sealed record CarHoldReleased(Guid HoldId, Guid TripId, Guid ItemId) : IIntegrationEvent
{
    [JsonIgnore]
    public string PartitionKey => TripId.ToString("N", CultureInfo.InvariantCulture);
}

public sealed record CarHoldExpired(Guid HoldId, Guid TripId, Guid ItemId) : IIntegrationEvent
{
    [JsonIgnore]
    public string PartitionKey => TripId.ToString("N", CultureInfo.InvariantCulture);
}

public sealed record CarBookingCancelled(Guid HoldId, Guid TripId, Guid ItemId) : IIntegrationEvent
{
    [JsonIgnore]
    public string PartitionKey => TripId.ToString("N", CultureInfo.InvariantCulture);
}
