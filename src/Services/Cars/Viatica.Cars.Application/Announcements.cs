using Microsoft.EntityFrameworkCore;
using Viatica.Cars.Domain;
using Viatica.Contracts;

namespace Viatica.Cars.Application;

/// <summary>
/// Builds the events Cars publishes about its listings and its inventory and queues them in the outbox. Everything
/// here runs inside the caller's transaction, so an event exists if and only if the change it describes committed.
/// </summary>
internal static class Announcements
{
    /// <summary>Announces one published location as a full snapshot with the next listing version.</summary>
    public static async Task ListingAsync(ICarsDb db, RentalLocation location, DateTimeOffset now, CancellationToken cancellationToken)
    {
        List<CarCategory> categories = await PublishedCategoriesAsync(db, location.PartnerId, cancellationToken);
        db.Publish(Snapshot(location, location.NextListingVersion(), categories, now));
    }

    /// <summary>
    /// Announces every published location of a partner again. A category is part of each location's snapshot, so
    /// publishing, editing or withdrawing one changes all of them.
    /// </summary>
    public static async Task PartnerListingsAsync(ICarsDb db, Guid partnerId, DateTimeOffset now, CancellationToken cancellationToken)
    {
        List<RentalLocation> published = await db.Locations
            .Where(l => l.PartnerId == partnerId && l.Status == ListingStatus.Published)
            .OrderBy(static l => l.Id)
            .ToListAsync(cancellationToken);
        if (published.Count == 0)
        {
            return;
        }

        List<CarCategory> categories = await PublishedCategoriesAsync(db, partnerId, cancellationToken);
        foreach (RentalLocation location in published)
        {
            db.Publish(Snapshot(location, location.NextListingVersion(), categories, now));
        }
    }

    public static void Withdrawn(ICarsDb db, RentalLocation location) =>
        db.Publish(new CarLocationUnpublished(location.Id, location.NextListingVersion()));

    /// <summary>
    /// Announces the current state of a span of days. The version is taken first and the days are read after it,
    /// both under the version row's lock, so a later version never carries older numbers than an earlier one.
    /// </summary>
    public static async Task<long> AvailabilityAsync(
        ICarsDb db, Guid locationId, Guid categoryId, DateOnly from, DateOnly to, CancellationToken cancellationToken)
    {
        long version = await db.NextAvailabilityVersionAsync(locationId, categoryId, cancellationToken);

        // AsNoTracking reads the database, not the change tracker: after an ExecuteUpdate the tracker is stale.
        var rows = await db.InventoryDays.AsNoTracking()
            .Where(d => d.LocationId == locationId && d.CategoryId == categoryId && d.Date >= from && d.Date <= to)
            .OrderBy(static d => d.Date)
            .Select(static d => new { d.Date, Available = d.Allotment - d.Sold - d.Held, d.Rate })
            .ToListAsync(cancellationToken);

        db.Publish(new CarAvailabilityChanged(
            locationId,
            categoryId,
            version,
            [.. rows.Select(static r => new DateAvailability(r.Date, r.Available, new Price(r.Rate, "EUR")))]));
        return version;
    }

    private static Task<List<CarCategory>> PublishedCategoriesAsync(ICarsDb db, Guid partnerId, CancellationToken cancellationToken) =>
        db.Categories.AsNoTracking()
            .Where(c => c.PartnerId == partnerId && c.Status == ListingStatus.Published)
            .OrderBy(static c => c.Acriss).ThenBy(static c => c.Id)
            .ToListAsync(cancellationToken);

    private static CarLocationUpdated Snapshot(
        RentalLocation location, long listingVersion, List<CarCategory> categories, DateTimeOffset now) =>
        new(
            location.Id,
            location.PartnerId,
            listingVersion,
            location.Name,
            new Viatica.Contracts.Address(
                location.Address.Line1, location.Address.City, location.Address.Region, location.Address.PostalCode,
                location.Address.CountryCode),
            new Viatica.Contracts.GeoPoint(location.Position.Latitude, location.Position.Longitude),
            location.AirportCode,
            [.. location.OpeningHours.Select(static w => new OpeningHours(w.Day, w.Opens, w.Closes))],
            new Viatica.Contracts.CancellationPolicy(
                location.Cancellation.FreeUntilHoursBeforeStart,
                location.Cancellation.PenaltyAfter switch
                {
                    Domain.CancellationPenalty.FirstDay => Viatica.Contracts.CancellationPenalty.FirstUnit,
                    Domain.CancellationPenalty.FullAmount => Viatica.Contracts.CancellationPenalty.FullAmount,
                    _ => throw new InvalidOperationException("Unknown cancellation penalty."),
                }),
            [.. categories.Select(static c => new CarCategorySnapshot(
                c.Id,
                c.Acriss,
                c.Name,
                c.Seats,
                (Viatica.Contracts.Transmission)(int)c.Transmission,
                (Viatica.Contracts.FuelType)(int)c.Fuel,
                c.Bags,
                c.MinimumDriverAge,
                c.ImageUrl))],
            now);
}
