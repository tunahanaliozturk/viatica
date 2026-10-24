using Viatica.Contracts;

namespace Viatica.Search.Application.Projection;

/// <summary>
/// <c>StayListingUpdated</c>, <c>StayUnpublished</c> and <c>StayAvailabilityChanged</c> into a <see cref="StayDocument"/>.
/// Listing events are whole snapshots and the highest <c>ListingVersion</c> wins; availability events are deltas and
/// are merged night by night (see <see cref="NightLedger"/>).
/// </summary>
public sealed class StayProjection : IProjection<StayDocument>
{
    public bool Reads(IIntegrationEvent message) =>
        message is StayListingUpdated or StayUnpublished or StayAvailabilityChanged;

    public string KeyOf(IIntegrationEvent message) => message switch
    {
        StayListingUpdated e => e.PropertyId.ToString(),
        StayUnpublished e => e.PropertyId.ToString(),
        StayAvailabilityChanged e => e.PropertyId.ToString(),
        _ => throw new ArgumentException($"{message.GetType().Name} is not a stay listing or availability event.", nameof(message)),
    };

    public StayDocument? Apply(StayDocument? current, IIntegrationEvent message, int firstDay, out ProjectionOutcome outcome) =>
        message switch
        {
            StayListingUpdated e => Listing(current, e, firstDay, out outcome),
            StayUnpublished e => Unpublish(current, e, out outcome),
            StayAvailabilityChanged e => Availability(current, e, firstDay, out outcome),
            _ => throw new ArgumentException($"{message.GetType().Name} is not a stay listing or availability event.", nameof(message)),
        };

    private static StayDocument Listing(StayDocument? current, StayListingUpdated e, int firstDay, out ProjectionOutcome outcome)
    {
        if (current is not null && current.ListingVersion >= e.ListingVersion)
        {
            outcome = ProjectionOutcome.Dropped;
            return current;
        }

        StayDocument doc = current ?? new StayDocument { PropertyId = e.PropertyId };
        doc.PartnerId = e.PartnerId;
        doc.ListingVersion = e.ListingVersion;
        doc.Published = true;
        doc.Name = e.Name;
        doc.City = e.Address.City;
        doc.Region = e.Address.Region;
        doc.CountryCode = e.Address.CountryCode;
        doc.Location = new GeoLocation(e.Location.Latitude, e.Location.Longitude);
        doc.StarRating = e.StarRating;
        doc.Amenities = ProjectionSupport.Normalize(e.Amenities);
        doc.UpdatedAt = e.UpdatedAt;

        // The snapshot is the whole truth about which room types exist. A room type it no longer names stays in the
        // document, unlisted, with its nights: if a later listing names it again its availability is still there.
        Dictionary<Guid, RoomDocument> known = doc.Rooms.ToDictionary(static r => r.Id);
        List<RoomDocument> rooms = [];
        foreach (RoomTypeSnapshot snapshot in e.RoomTypes)
        {
            RoomDocument room = known.GetValueOrDefault(snapshot.RoomTypeId) ?? new RoomDocument { Id = snapshot.RoomTypeId };
            room.Listed = true;
            room.Name = snapshot.Name;
            room.MaxOccupancy = snapshot.MaxOccupancy;
            room.Beds = snapshot.Beds;
            known.Remove(snapshot.RoomTypeId);
            rooms.Add(room);
        }

        foreach (RoomDocument gone in known.Values)
        {
            gone.Listed = false;
            rooms.Add(gone);
        }

        foreach (RoomDocument room in rooms)
        {
            room.State = room.State.Trim(firstDay);
        }

        doc.Rooms = rooms;
        outcome = ProjectionOutcome.Applied;
        return doc;
    }

    private static StayDocument Unpublish(StayDocument? current, StayUnpublished e, out ProjectionOutcome outcome)
    {
        if (current is not null && current.ListingVersion >= e.ListingVersion)
        {
            outcome = ProjectionOutcome.Dropped;
            return current;
        }

        // Kept as a document with Published = false rather than deleted: it remembers the version, so a listing
        // snapshot that was overtaken by this unpublish cannot bring the property back when it arrives late.
        StayDocument doc = current ?? new StayDocument { PropertyId = e.PropertyId };
        doc.ListingVersion = e.ListingVersion;
        doc.Published = false;
        outcome = ProjectionOutcome.Applied;
        return doc;
    }

    private static StayDocument? Availability(StayDocument? current, StayAvailabilityChanged e, int firstDay, out ProjectionOutcome outcome)
    {
        RoomDocument? room = current?.Rooms.Find(r => r.Id == e.RoomTypeId);
        NightLedger ledger = (room?.State ?? new NightLedger()).Apply(
            e.AvailabilityVersion, ProjectionSupport.Changes(e.Nights), firstDay, out int applied);

        if (applied == 0)
        {
            outcome = ProjectionOutcome.Dropped;
            return current;
        }

        // Availability can arrive before the listing that names the room type, or for one it never names. Either way
        // the nights are kept; the room is unlisted, so no search offers it, until a listing says it exists.
        StayDocument doc = current ?? new StayDocument { PropertyId = e.PropertyId };
        if (room is null)
        {
            room = new RoomDocument { Id = e.RoomTypeId };
            doc.Rooms.Add(room);
        }

        room.State = ledger;
        outcome = ProjectionOutcome.Applied;
        return doc;
    }
}
