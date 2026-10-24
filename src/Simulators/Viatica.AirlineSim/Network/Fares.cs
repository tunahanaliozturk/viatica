using System.Text.Json.Serialization;

namespace Viatica.AirlineSim.Network;

[JsonConverter(typeof(JsonStringEnumConverter<Cabin>))]
internal enum Cabin
{
    Economy,
    Business,
}

/// <summary>A booking class: its cabin, how its fare relates to the cheapest bucket, and whether it refunds.</summary>
/// <remarks>Seat counts per flight are drawn from the seed between the two bounds.</remarks>
internal sealed record FareClass(char Code, Cabin Cabin, decimal Multiplier, bool Refundable, int MinSeats, int MaxSeats)
{
    /// <summary>Taken off a refundable fare per seated passenger when the ticket is cancelled.</summary>
    public const decimal CancellationFeePerPassenger = 75m;

    /// <summary>Cheapest first within each cabin, which is also the order a re-price climbs when a bucket sells out.</summary>
    public static IReadOnlyList<FareClass> All { get; } =
    [
        new('K', Cabin.Economy, 1.00m, Refundable: false, 15, 40),
        new('M', Cabin.Economy, 1.45m, Refundable: false, 10, 25),
        new('Y', Cabin.Economy, 2.20m, Refundable: true, 6, 15),
        new('J', Cabin.Business, 3.80m, Refundable: true, 4, 12),
    ];

    public static FareClass? Find(char code) => All.FirstOrDefault(fareClass => fareClass.Code == code);

    public static IEnumerable<FareClass> In(Cabin cabin) => All.Where(fareClass => fareClass.Cabin == cabin);

    /// <summary>
    /// Refundable fares give back the whole price less a fee per seated passenger; the rest give back the taxes only,
    /// which is what most real non-refundable fares do because the taxes were never the airline's money.
    /// </summary>
    public decimal Refund(decimal total, decimal taxes, int seatedPassengers) =>
        Refundable ? Math.Max(0m, total - (CancellationFeePerPassenger * seatedPassengers)) : taxes;
}

/// <summary>The passenger mix of a search or an offer. Infants travel on a lap and take no seat.</summary>
internal sealed record PassengerMix(int Adults, int Children, int Infants)
{
    public const int MaxSeated = 9;

    public int Seats => Adults + Children;

    public string? Problem() => this switch
    {
        { Adults: < 1 } => "At least one adult must travel.",
        { Children: < 0 } or { Infants: < 0 } => "Passenger counts cannot be negative.",
        { Seats: > MaxSeated } => $"At most {MaxSeated} seated passengers per booking.",
        _ when Infants > Adults => "Each infant needs an adult lap.",
        _ => null,
    };
}

/// <summary>A price in EUR, split the way supplier invoices split it.</summary>
internal readonly record struct Price(decimal Base, decimal Taxes)
{
    public decimal Total => Base + Taxes;
}
