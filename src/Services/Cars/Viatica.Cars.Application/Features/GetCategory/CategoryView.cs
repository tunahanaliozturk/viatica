using Viatica.Cars.Domain;

namespace Viatica.Cars.Application;

/// <summary>A car category as its partner sees it.</summary>
public sealed record CategoryView(
    Guid Id,
    string Acriss,
    string Name,
    int Seats,
    Transmission Transmission,
    FuelType Fuel,
    int Bags,
    int MinimumDriverAge,
    Uri? ImageUrl,
    ListingStatus Status,
    DateTimeOffset CreatedAt,
    DateTimeOffset UpdatedAt)
{
    internal static CategoryView From(CarCategory c) =>
        new(c.Id, c.Acriss, c.Name, c.Seats, c.Transmission, c.Fuel, c.Bags, c.MinimumDriverAge, c.ImageUrl, c.Status, c.CreatedAt, c.UpdatedAt);
}
