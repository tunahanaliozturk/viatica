using Viatica.Contracts;

namespace Viatica.Search.Application.Projection;

/// <summary>A point as OpenSearch's <c>geo_point</c> object form reads it.</summary>
public sealed record GeoLocation(double Lat, double Lon);

/// <summary>
/// A room type or car category with its nights. The searchable fields (<see cref="Open"/>, <see cref="Rates"/>) are
/// derived from <see cref="State"/> every time the document is written, so they can never disagree with it.
/// </summary>
public abstract class StockedUnit
{
    public required Guid Id { get; set; }

    /// <summary>False for a unit the last listing no longer names. Its nights are kept in case it comes back.</summary>
    public bool Listed { get; set; }

    /// <summary>Source of truth for the nights. Stored in the document but not indexed.</summary>
    public NightLedger State { get; set; } = new();

    /// <summary>Largest "at least N units left" threshold this kind of unit is searched with.</summary>
    protected abstract int Thresholds { get; }

    /// <summary>For each N, the runs of nights with at least N units left. Keys are "1", "2", ...</summary>
    public IReadOnlyDictionary<string, IReadOnlyList<DayRange>> Open =>
        Enumerable.Range(1, Thresholds).ToDictionary(
            static n => n.ToString(System.Globalization.CultureInfo.InvariantCulture),
            n => State.Runs(n));

    public long[] Rates => State.PackedRates();
}

public sealed class RoomDocument : StockedUnit
{
    public string Name { get; set; } = "";

    public int MaxOccupancy { get; set; }

    public string Beds { get; set; } = "";

    protected override int Thresholds => NightLedger.MaxThreshold;
}

/// <summary>
/// A property as search sees it: the latest listing snapshot, plus the nights of each room type merged in from
/// availability events. One document per property, rooms nested, so every filter on the property and the rooms
/// is a single query and a result is a property, not a room.
/// </summary>
public sealed class StayDocument
{
    public required Guid PropertyId { get; set; }

    public Guid PartnerId { get; set; }

    /// <summary>Highest listing or unpublish version seen. Zero while only availability has arrived.</summary>
    public long ListingVersion { get; set; }

    public bool Published { get; set; }

    public string Name { get; set; } = "";

    public string City { get; set; } = "";

    public string? Region { get; set; }

    public string CountryCode { get; set; } = "";

    public GeoLocation Location { get; set; } = new(0, 0);

    public int StarRating { get; set; }

    public List<string> Amenities { get; set; } = [];

    public DateTimeOffset? UpdatedAt { get; set; }

    public List<RoomDocument> Rooms { get; set; } = [];
}

public sealed class CarCategoryDocument : StockedUnit
{
    public string Acriss { get; set; } = "";

    public string Name { get; set; } = "";

    public int Seats { get; set; }

    public Transmission Transmission { get; set; }

    public FuelType Fuel { get; set; }

    public int Bags { get; set; }

    public int MinimumDriverAge { get; set; }

    public Uri? ImageUrl { get; set; }

    // A car is rented one at a time, so "at least 1 left" is the only threshold a search asks for.
    protected override int Thresholds => 1;
}

public sealed class CarLocationDocument
{
    public required Guid LocationId { get; set; }

    public Guid PartnerId { get; set; }

    public long ListingVersion { get; set; }

    public bool Published { get; set; }

    public string Name { get; set; } = "";

    public string City { get; set; } = "";

    public string? Region { get; set; }

    public string CountryCode { get; set; } = "";

    public string? AirportCode { get; set; }

    public GeoLocation Location { get; set; } = new(0, 0);

    public DateTimeOffset? UpdatedAt { get; set; }

    public List<CarCategoryDocument> Categories { get; set; } = [];
}

/// <summary>An event with what the projection needs to know about its delivery.</summary>
public sealed record ProjectionEvent(Guid Id, DateTimeOffset OccurredAt, IIntegrationEvent Event);

/// <summary>What became of one event.</summary>
public enum ProjectionOutcome
{
    /// <summary>It changed the document.</summary>
    Applied,

    /// <summary>It carried nothing newer than the document already held: a duplicate, or one that arrived late.</summary>
    Dropped,
}
