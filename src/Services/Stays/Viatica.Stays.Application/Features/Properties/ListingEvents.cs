using Viatica.Contracts;
using Viatica.Stays.Domain;

namespace Viatica.Stays.Application.Features.Properties;

/// <summary>
/// Turns a property into the events search consumes. A change to a published property is announced as a full
/// snapshot, not as what changed (ADR 0002), so the projection needs no history to apply it.
/// </summary>
internal static class ListingEvents
{
    /// <summary>Queues the snapshot when the property is on sale; a draft or unpublished one is nobody's business yet.</summary>
    public static void PublishIfListed(IStaysDb db, StayProperty property)
    {
        if (property.IsPublished)
        {
            db.Publish(Snapshot(property));
        }
    }

    public static StayListingUpdated Snapshot(StayProperty property)
    {
        PropertyDetails details = property.Details;

        return new StayListingUpdated(
            property.Id,
            property.PartnerId,
            property.ListingVersion,
            details.Name,
            details.Description,
            new Contracts.Address(details.Address.Line1, details.Address.City, details.Address.Region, details.Address.PostalCode, details.Address.CountryCode),
            new Contracts.GeoPoint(details.Location.Latitude, details.Location.Longitude),
            details.StarRating,
            details.Amenities,
            details.CheckInFrom,
            details.CheckOutUntil,
            new Contracts.CancellationPolicy(
                details.CancellationPolicy.FreeUntilHoursBeforeStart,
                details.CancellationPolicy.PenaltyAfter switch
                {
                    Domain.CancellationPenalty.FirstUnit => Contracts.CancellationPenalty.FirstUnit,
                    Domain.CancellationPenalty.FullAmount => Contracts.CancellationPenalty.FullAmount,
                    _ => throw new ArgumentOutOfRangeException(nameof(property), "Unknown cancellation penalty."),
                }),
            [.. property.RoomTypes
                .OrderBy(static r => r.CreatedAt).ThenBy(static r => r.Id)
                .Select(static r => new RoomTypeSnapshot(r.Id, r.Name, r.MaxOccupancy, r.Beds, r.Amenities))],
            property.UpdatedAt);
    }

    public static StayUnpublished Unpublished(StayProperty property) =>
        new(property.Id, property.ListingVersion);
}
