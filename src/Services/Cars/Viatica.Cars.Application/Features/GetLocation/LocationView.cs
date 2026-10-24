using Viatica.Cars.Domain;

namespace Viatica.Cars.Application;

/// <summary>A rental location as its partner sees it.</summary>
public sealed record LocationView(
    Guid Id,
    string Name,
    Address Address,
    GeoPoint Position,
    string? AirportCode,
    IReadOnlyList<OpeningWindow> OpeningHours,
    CancellationPolicy CancellationPolicy,
    ListingStatus Status,
    long ListingVersion,
    DateTimeOffset CreatedAt,
    DateTimeOffset UpdatedAt)
{
    internal static LocationView From(RentalLocation l) =>
        new(l.Id, l.Name, l.Address, l.Position, l.AirportCode, [.. l.OpeningHours], l.Cancellation, l.Status, l.ListingVersion, l.CreatedAt, l.UpdatedAt);
}
