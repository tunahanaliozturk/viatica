namespace Viatica.Cars.Domain;

public enum ListingStatus
{
    Draft,
    Published,
    Unpublished,
}

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

public enum CancellationPenalty
{
    /// <summary>The first rental day is kept.</summary>
    FirstDay,

    /// <summary>Nothing is refunded.</summary>
    FullAmount,
}

public sealed record Address(string Line1, string City, string PostalCode, string CountryCode, string? Region = null);

public sealed record GeoPoint(double Latitude, double Longitude)
{
    public bool IsInRange() => Latitude is >= -90 and <= 90 && Longitude is >= -180 and <= 180;
}

public sealed record CancellationPolicy(int FreeUntilHoursBeforeStart, CancellationPenalty PenaltyAfter);

/// <summary>
/// One day's opening hours. A window never crosses midnight; a location open round the clock says 00:00 to 23:59.
/// Times are whole minutes, because that is the precision a desk opens and closes at.
/// </summary>
public sealed record OpeningWindow(DayOfWeek Day, TimeOnly Opens, TimeOnly Closes);
