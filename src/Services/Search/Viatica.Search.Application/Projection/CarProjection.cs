using Viatica.Contracts;

namespace Viatica.Search.Application.Projection;

/// <summary>
/// <c>CarLocationUpdated</c>, <c>CarLocationUnpublished</c> and <c>CarAvailabilityChanged</c> into a
/// <see cref="CarLocationDocument"/>, by the same rules as <see cref="StayProjection"/>: the highest listing
/// version wins, availability merges night by night.
/// </summary>
public sealed class CarProjection : IProjection<CarLocationDocument>
{
    public bool Reads(IIntegrationEvent message) =>
        message is CarLocationUpdated or CarLocationUnpublished or CarAvailabilityChanged;

    public string KeyOf(IIntegrationEvent message) => message switch
    {
        CarLocationUpdated e => e.LocationId.ToString(),
        CarLocationUnpublished e => e.LocationId.ToString(),
        CarAvailabilityChanged e => e.LocationId.ToString(),
        _ => throw new ArgumentException($"{message.GetType().Name} is not a car listing or availability event.", nameof(message)),
    };

    public CarLocationDocument? Apply(CarLocationDocument? current, IIntegrationEvent message, int firstDay, out ProjectionOutcome outcome) =>
        message switch
        {
            CarLocationUpdated e => Listing(current, e, firstDay, out outcome),
            CarLocationUnpublished e => Unpublish(current, e, out outcome),
            CarAvailabilityChanged e => Availability(current, e, firstDay, out outcome),
            _ => throw new ArgumentException($"{message.GetType().Name} is not a car listing or availability event.", nameof(message)),
        };

    private static CarLocationDocument Listing(CarLocationDocument? current, CarLocationUpdated e, int firstDay, out ProjectionOutcome outcome)
    {
        if (current is not null && current.ListingVersion >= e.ListingVersion)
        {
            outcome = ProjectionOutcome.Dropped;
            return current;
        }

        CarLocationDocument doc = current ?? new CarLocationDocument { LocationId = e.LocationId };
        doc.PartnerId = e.PartnerId;
        doc.ListingVersion = e.ListingVersion;
        doc.Published = true;
        doc.Name = e.Name;
        doc.City = e.Address.City;
        doc.Region = e.Address.Region;
        doc.CountryCode = e.Address.CountryCode;
        doc.AirportCode = e.AirportCode?.Trim().ToUpperInvariant();
        doc.Location = new GeoLocation(e.Location.Latitude, e.Location.Longitude);
        doc.UpdatedAt = e.UpdatedAt;

        Dictionary<Guid, CarCategoryDocument> known = doc.Categories.ToDictionary(static c => c.Id);
        List<CarCategoryDocument> categories = [];
        foreach (CarCategorySnapshot snapshot in e.Categories)
        {
            CarCategoryDocument category = known.GetValueOrDefault(snapshot.CategoryId) ?? new CarCategoryDocument { Id = snapshot.CategoryId };
            category.Listed = true;
            category.Acriss = snapshot.Acriss.ToUpperInvariant();
            category.Name = snapshot.Name;
            category.Seats = snapshot.Seats;
            category.Transmission = snapshot.Transmission;
            category.Fuel = snapshot.Fuel;
            category.Bags = snapshot.Bags;
            category.MinimumDriverAge = snapshot.MinimumDriverAge;
            category.ImageUrl = snapshot.ImageUrl;
            known.Remove(snapshot.CategoryId);
            categories.Add(category);
        }

        foreach (CarCategoryDocument gone in known.Values)
        {
            gone.Listed = false;
            categories.Add(gone);
        }

        foreach (CarCategoryDocument category in categories)
        {
            category.State = category.State.Trim(firstDay);
        }

        doc.Categories = categories;
        outcome = ProjectionOutcome.Applied;
        return doc;
    }

    private static CarLocationDocument Unpublish(CarLocationDocument? current, CarLocationUnpublished e, out ProjectionOutcome outcome)
    {
        if (current is not null && current.ListingVersion >= e.ListingVersion)
        {
            outcome = ProjectionOutcome.Dropped;
            return current;
        }

        CarLocationDocument doc = current ?? new CarLocationDocument { LocationId = e.LocationId };
        doc.ListingVersion = e.ListingVersion;
        doc.Published = false;
        outcome = ProjectionOutcome.Applied;
        return doc;
    }

    private static CarLocationDocument? Availability(CarLocationDocument? current, CarAvailabilityChanged e, int firstDay, out ProjectionOutcome outcome)
    {
        CarCategoryDocument? category = current?.Categories.Find(c => c.Id == e.CategoryId);
        NightLedger ledger = (category?.State ?? new NightLedger()).Apply(
            e.AvailabilityVersion, ProjectionSupport.Changes(e.Days), firstDay, out int applied);

        if (applied == 0)
        {
            outcome = ProjectionOutcome.Dropped;
            return current;
        }

        CarLocationDocument doc = current ?? new CarLocationDocument { LocationId = e.LocationId };
        if (category is null)
        {
            category = new CarCategoryDocument { Id = e.CategoryId };
            doc.Categories.Add(category);
        }

        category.State = ledger;
        outcome = ProjectionOutcome.Applied;
        return doc;
    }
}
