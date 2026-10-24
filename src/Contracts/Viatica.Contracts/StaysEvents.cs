using System.Globalization;
using System.Text.Json.Serialization;

namespace Viatica.Contracts;

// Published by Stays on stays.v1.
//
// Listing events are full snapshots with a version, not deltas: the search projection can apply them in any
// order by keeping the highest version, and a rebuild is a replay of the latest snapshot per property.
// Hold events are keyed by trip, because Booking is the consumer that needs them in order.

public sealed record RoomTypeSnapshot(Guid RoomTypeId, string Name, int MaxOccupancy, string Beds, IReadOnlyList<string> Amenities);

public sealed record StayListingUpdated(
    Guid PropertyId,
    Guid PartnerId,
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
    IReadOnlyList<RoomTypeSnapshot> RoomTypes,
    DateTimeOffset UpdatedAt) : IIntegrationEvent
{
    [JsonIgnore]
    public string PartitionKey => PropertyId.ToString("N", CultureInfo.InvariantCulture);
}

public sealed record StayUnpublished(Guid PropertyId, long ListingVersion) : IIntegrationEvent
{
    [JsonIgnore]
    public string PartitionKey => PropertyId.ToString("N", CultureInfo.InvariantCulture);
}

/// <summary>
/// The nights of one room type whose availability or rate changed, after any rate edit, hold, commit, release
/// or expiry. <see cref="AvailabilityVersion"/> increases per room type so a projection can drop stale updates.
/// </summary>
public sealed record StayAvailabilityChanged(
    Guid PropertyId,
    Guid RoomTypeId,
    long AvailabilityVersion,
    IReadOnlyList<DateAvailability> Nights) : IIntegrationEvent
{
    [JsonIgnore]
    public string PartitionKey => PropertyId.ToString("N", CultureInfo.InvariantCulture);
}

public sealed record StayHoldPlaced(
    Guid HoldId,
    Guid TripId,
    Guid ItemId,
    Guid PropertyId,
    Guid RoomTypeId,
    DateOnly CheckIn,
    DateOnly CheckOut,
    int Quantity,
    Price Total,
    DateTimeOffset ExpiresAt) : IIntegrationEvent
{
    [JsonIgnore]
    public string PartitionKey => TripId.ToString("N", CultureInfo.InvariantCulture);
}

public sealed record StayHoldRejected(Guid HoldId, Guid TripId, Guid ItemId, HoldRejectionReason Reason) : IIntegrationEvent
{
    [JsonIgnore]
    public string PartitionKey => TripId.ToString("N", CultureInfo.InvariantCulture);
}

public sealed record StayHoldCommitted(Guid HoldId, Guid TripId, Guid ItemId) : IIntegrationEvent
{
    [JsonIgnore]
    public string PartitionKey => TripId.ToString("N", CultureInfo.InvariantCulture);
}

public sealed record StayHoldReleased(Guid HoldId, Guid TripId, Guid ItemId) : IIntegrationEvent
{
    [JsonIgnore]
    public string PartitionKey => TripId.ToString("N", CultureInfo.InvariantCulture);
}

public sealed record StayHoldExpired(Guid HoldId, Guid TripId, Guid ItemId) : IIntegrationEvent
{
    [JsonIgnore]
    public string PartitionKey => TripId.ToString("N", CultureInfo.InvariantCulture);
}

/// <summary>A committed stay was cancelled by the traveller; its nights are back on sale.</summary>
public sealed record StayBookingCancelled(Guid HoldId, Guid TripId, Guid ItemId) : IIntegrationEvent
{
    [JsonIgnore]
    public string PartitionKey => TripId.ToString("N", CultureInfo.InvariantCulture);
}
