using Viatica.Kernel.Querying;

namespace Viatica.Kernel.UnitTests;

public enum StayKind
{
    Hotel,
    Apartment,
    Hostel,
}

/// <summary>The shape a stays search endpoint pages over: one row per property.</summary>
public sealed class StayRow
{
    public int Id { get; set; }

    public string City { get; set; } = "";

    public decimal FromPrice { get; set; }

    public int Stars { get; set; }

    public bool Refundable { get; set; }

    public DateOnly AvailableFrom { get; set; }

    public DateTimeOffset UpdatedAt { get; set; }

    public Guid PartnerId { get; set; }

    public StayKind Kind { get; set; }

    public decimal? Discount { get; set; }
}

/// <summary>A fixed data set with deliberate ties, so paging has to lean on the key to stay correct.</summary>
public static class Stays
{
    public static readonly Guid Partner = Guid.Parse("0b5e3c2a-4f1d-4c8e-9a3b-2d7f6e1c0a94");

    public static readonly QueryFields<StayRow> Fields = new QueryFields<StayRow>()
        .Key("id", static s => s.Id)
        .Field("city", static s => s.City, sortable: true)
        .Field("fromPrice", static s => s.FromPrice, sortable: true)
        .Field("stars", static s => s.Stars, sortable: true)
        .Field("refundable", static s => s.Refundable)
        .Field("availableFrom", static s => s.AvailableFrom, sortable: true)
        .Field("updatedAt", static s => s.UpdatedAt, sortable: true)
        .Field("partnerId", static s => s.PartnerId)
        .Field("kind", static s => s.Kind)
        .Field("discount", static s => s.Discount);

    private static readonly string[] Cities = ["lisbon", "porto", "faro", "O'Brien Town"];

    public static readonly StayRow[] Rows =
    [
        .. Enumerable.Range(1, 57).Select(static i => new StayRow
        {
            Id = i,
            City = Cities[i % Cities.Length],
            FromPrice = 50m + (i % 7 * 10m),              // seven distinct prices, many ties
            Stars = 1 + (i % 5),
            Refundable = i % 2 == 0,
            AvailableFrom = new DateOnly(2026, 12, 1).AddDays(i % 10),
            UpdatedAt = new DateTimeOffset(2026, 11, 1, 0, 0, 0, TimeSpan.Zero).AddHours(i),
            PartnerId = i % 3 == 0 ? Partner : Guid.Empty,
            Kind = (StayKind)(i % 3),
            Discount = i % 4 == 0 ? 5m : null,
        }),
    ];
}
