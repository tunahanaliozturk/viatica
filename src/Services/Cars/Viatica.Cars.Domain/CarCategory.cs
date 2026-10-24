using System.Text.RegularExpressions;
using Moongazing.OrionGuard.Core;

namespace Viatica.Cars.Domain;

/// <summary>
/// A class of car a partner rents out, named by its ACRISS code. ACRISS is the industry's four-letter
/// classification (size, doors, transmission and fuel, air conditioning); the service checks the shape, not the
/// meaning of each letter.
/// </summary>
public sealed partial class CarCategory
{
    private CarCategory()
    {
    }

    public Guid Id { get; private set; }

    public Guid PartnerId { get; private set; }

    public string Acriss { get; private set; } = "";

    public string Name { get; private set; } = "";

    public int Seats { get; private set; }

    public Transmission Transmission { get; private set; }

    public FuelType Fuel { get; private set; }

    public int Bags { get; private set; }

    public int MinimumDriverAge { get; private set; }

    public Uri? ImageUrl { get; private set; }

    public ListingStatus Status { get; private set; }

    public DateTimeOffset CreatedAt { get; private set; }

    public DateTimeOffset UpdatedAt { get; private set; }

    /// <summary>Postgres <c>xmin</c>: the row version behind the ETag.</summary>
    public uint Version { get; private set; }

    public static bool IsAcriss(string? code) => code is not null && AcrissPattern().IsMatch(code);

    public static CarCategory Create(
        Guid id, Guid partnerId, string acriss, string name, int seats, Transmission transmission, FuelType fuel,
        int bags, int minimumDriverAge, Uri? imageUrl, DateTimeOffset now)
    {
        var category = new CarCategory { Id = id, PartnerId = partnerId, Status = ListingStatus.Draft, CreatedAt = now };
        category.Revise(acriss, name, seats, transmission, fuel, bags, minimumDriverAge, imageUrl, now);
        return category;
    }

    public void Revise(
        string acriss, string name, int seats, Transmission transmission, FuelType fuel, int bags,
        int minimumDriverAge, Uri? imageUrl, DateTimeOffset now)
    {
        Ensure.That(acriss, nameof(acriss)).NotNull();
        Ensure.That(name, nameof(name)).NotEmpty();
        Ensure.That(seats, nameof(seats)).InRange(1, 20);
        Ensure.That(bags, nameof(bags)).InRange(0, 20);
        Ensure.That(minimumDriverAge, nameof(minimumDriverAge)).InRange(18, 99);
        if (!IsAcriss(acriss))
        {
            throw new ArgumentException("An ACRISS code is four letters.", nameof(acriss));
        }

        Acriss = acriss.ToUpperInvariant();
        Name = name.Trim();
        Seats = seats;
        Transmission = transmission;
        Fuel = fuel;
        Bags = bags;
        MinimumDriverAge = minimumDriverAge;
        ImageUrl = imageUrl;
        UpdatedAt = now;
    }

    /// <summary>True when the status changed, so the caller knows whether listings must be announced again.</summary>
    public bool Publish(DateTimeOffset now)
    {
        if (Status == ListingStatus.Published)
        {
            return false;
        }

        Status = ListingStatus.Published;
        UpdatedAt = now;
        return true;
    }

    public bool Unpublish(DateTimeOffset now)
    {
        if (Status != ListingStatus.Published)
        {
            return false;
        }

        Status = ListingStatus.Unpublished;
        UpdatedAt = now;
        return true;
    }

    [GeneratedRegex("^[A-Za-z]{4}$", RegexOptions.CultureInvariant)]
    private static partial Regex AcrissPattern();
}
