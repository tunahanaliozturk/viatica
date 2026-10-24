using System.Text.RegularExpressions;
using Moongazing.OrionGuard.Core;

namespace Viatica.Cars.Domain;

/// <summary>
/// A branch where cars are picked up and dropped off, owned by one partner. A listing: it goes from
/// <see cref="ListingStatus.Draft"/> to published and back, and every announcement of it carries a
/// <see cref="ListingVersion"/> that only goes up.
/// </summary>
public sealed partial class RentalLocation
{
    private List<OpeningWindow> _openingHours = [];

    private RentalLocation()
    {
    }

    public Guid Id { get; private set; }

    public Guid PartnerId { get; private set; }

    public string Name { get; private set; } = "";

    public Address Address { get; private set; } = null!;

    public GeoPoint Position { get; private set; } = null!;

    public string? AirportCode { get; private set; }

    public IReadOnlyList<OpeningWindow> OpeningHours => _openingHours;

    public CancellationPolicy Cancellation { get; private set; } = null!;

    public ListingStatus Status { get; private set; }

    /// <summary>
    /// The version of the last snapshot or unpublish announcement. Search keeps the highest it has seen, so a
    /// replayed or reordered message cannot bring an old listing back.
    /// </summary>
    public long ListingVersion { get; private set; }

    public DateTimeOffset CreatedAt { get; private set; }

    public DateTimeOffset UpdatedAt { get; private set; }

    /// <summary>Postgres <c>xmin</c>: the row version behind the ETag.</summary>
    public uint Version { get; private set; }

    public static bool IsIataCode(string? code) => code is not null && IataPattern().IsMatch(code);

    public static RentalLocation Create(
        Guid id, Guid partnerId, string name, Address address, GeoPoint position, string? airportCode,
        IEnumerable<OpeningWindow> openingHours, CancellationPolicy cancellation, DateTimeOffset now)
    {
        var location = new RentalLocation { Id = id, PartnerId = partnerId, Status = ListingStatus.Draft, CreatedAt = now };
        location.Revise(name, address, position, airportCode, openingHours, cancellation, now);
        return location;
    }

    /// <summary>
    /// Replaces everything a partner edits. A published location stays published, so the caller announces it
    /// again with <see cref="NextListingVersion"/>.
    /// </summary>
    public void Revise(
        string name, Address address, GeoPoint position, string? airportCode, IEnumerable<OpeningWindow> openingHours,
        CancellationPolicy cancellation, DateTimeOffset now)
    {
        Ensure.That(name, nameof(name)).NotEmpty();
        ArgumentNullException.ThrowIfNull(address);
        ArgumentNullException.ThrowIfNull(position);
        if (!position.IsInRange())
        {
            throw new ArgumentException("Latitude is -90 to 90 and longitude -180 to 180.", nameof(position));
        }

        ArgumentNullException.ThrowIfNull(cancellation);
        ArgumentNullException.ThrowIfNull(openingHours);
        if (airportCode is not null && !IsIataCode(airportCode))
        {
            throw new ArgumentException("An airport code is three letters.", nameof(airportCode));
        }

        OpeningWindow[] windows = [.. openingHours];
        if (windows.Select(static w => w.Day).Distinct().Count() != windows.Length)
        {
            throw new ArgumentException("A weekday can have one opening window.", nameof(openingHours));
        }

        if (windows.Any(static w => w.Opens >= w.Closes || w.Opens.Second != 0 || w.Closes.Second != 0 || w.Opens.Millisecond != 0 || w.Closes.Millisecond != 0))
        {
            throw new ArgumentException("Opening hours are whole minutes, and a window closes after it opens.", nameof(openingHours));
        }

        Name = name.Trim();
        Address = address;
        Position = position;
        AirportCode = airportCode?.ToUpperInvariant();
        Cancellation = cancellation;
        UpdatedAt = now;

        _openingHours = [.. windows.OrderBy(static w => w.Day)];
    }

    /// <summary>
    /// Whether the desk is open at this wall-clock moment. Both ends count: a car handed over in the closing
    /// minute is still handed over by staff who are there.
    /// </summary>
    public bool IsOpenAt(DateTimeOffset at)
    {
        OpeningWindow? window = _openingHours.Find(w => w.Day == at.DayOfWeek);
        if (window is null)
        {
            return false;
        }

        var time = new TimeOnly(at.Hour, at.Minute);
        return time >= window.Opens && time <= window.Closes;
    }

    /// <summary>True when the location went live; false when it already was.</summary>
    public bool Publish(DateTimeOffset now)
    {
        if (Status == ListingStatus.Published)
        {
            return false;
        }

        if (_openingHours.Count == 0)
        {
            throw CarErrors.NotPublishable("A location needs opening hours before it can be published.");
        }

        Status = ListingStatus.Published;
        UpdatedAt = now;
        return true;
    }

    /// <summary>True when the location went offline; false when it was not live.</summary>
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

    public long NextListingVersion()
    {
        return ++ListingVersion;
    }

    [GeneratedRegex("^[A-Za-z]{3}$", RegexOptions.CultureInvariant)]
    private static partial Regex IataPattern();
}
