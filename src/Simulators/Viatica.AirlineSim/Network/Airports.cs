namespace Viatica.AirlineSim.Network;

/// <summary>An airport the simulated airlines fly to.</summary>
/// <param name="UtcOffsetMinutes">
/// Standard time only. The sim ignores daylight saving on purpose: schedules stay identical every day of the year,
/// which is what repeatable tests need, and no consumer of the sim reasons about wall clocks abroad.
/// </param>
internal sealed record Airport(
    string Code,
    string City,
    string Country,
    double Latitude,
    double Longitude,
    int UtcOffsetMinutes,
    bool LongHaul);

internal static class Airports
{
    /// <summary>Forty airports: mostly Europe, plus six long haul destinations. Order matters to the seed.</summary>
    public static IReadOnlyList<Airport> All { get; } =
    [
        new("IST", "Istanbul", "TR", 41.275, 28.752, 180, false),
        new("ESB", "Ankara", "TR", 40.128, 32.995, 180, false),
        new("ADB", "Izmir", "TR", 38.292, 27.157, 180, false),
        new("AYT", "Antalya", "TR", 36.899, 30.800, 180, false),
        new("LHR", "London", "GB", 51.470, -0.454, 0, false),
        new("MAN", "Manchester", "GB", 53.354, -2.275, 0, false),
        new("EDI", "Edinburgh", "GB", 55.950, -3.373, 0, false),
        new("DUB", "Dublin", "IE", 53.421, -6.270, 0, false),
        new("LIS", "Lisbon", "PT", 38.774, -9.134, 0, false),
        new("MAD", "Madrid", "ES", 40.472, -3.561, 60, false),
        new("BCN", "Barcelona", "ES", 41.297, 2.078, 60, false),
        new("CDG", "Paris", "FR", 49.010, 2.548, 60, false),
        new("NCE", "Nice", "FR", 43.658, 7.216, 60, false),
        new("BRU", "Brussels", "BE", 50.901, 4.484, 60, false),
        new("AMS", "Amsterdam", "NL", 52.310, 4.768, 60, false),
        new("FRA", "Frankfurt", "DE", 50.033, 8.571, 60, false),
        new("MUC", "Munich", "DE", 48.354, 11.786, 60, false),
        new("BER", "Berlin", "DE", 52.366, 13.503, 60, false),
        new("HAM", "Hamburg", "DE", 53.630, 9.988, 60, false),
        new("ZRH", "Zurich", "CH", 47.465, 8.549, 60, false),
        new("GVA", "Geneva", "CH", 46.238, 6.109, 60, false),
        new("VIE", "Vienna", "AT", 48.110, 16.570, 60, false),
        new("FCO", "Rome", "IT", 41.800, 12.239, 60, false),
        new("MXP", "Milan", "IT", 45.630, 8.723, 60, false),
        new("CPH", "Copenhagen", "DK", 55.618, 12.656, 60, false),
        new("OSL", "Oslo", "NO", 60.194, 11.100, 60, false),
        new("ARN", "Stockholm", "SE", 59.652, 17.919, 60, false),
        new("HEL", "Helsinki", "FI", 60.317, 24.963, 120, false),
        new("WAW", "Warsaw", "PL", 52.166, 20.967, 60, false),
        new("PRG", "Prague", "CZ", 50.101, 14.260, 60, false),
        new("BUD", "Budapest", "HU", 47.437, 19.256, 60, false),
        new("OTP", "Bucharest", "RO", 44.571, 26.085, 120, false),
        new("SOF", "Sofia", "BG", 42.697, 23.411, 120, false),
        new("ATH", "Athens", "GR", 37.936, 23.947, 120, false),
        new("JFK", "New York", "US", 40.641, -73.778, -300, true),
        new("YYZ", "Toronto", "CA", 43.677, -79.624, -300, true),
        new("GRU", "Sao Paulo", "BR", -23.435, -46.473, -180, true),
        new("DXB", "Dubai", "AE", 25.253, 55.365, 240, true),
        new("SIN", "Singapore", "SG", 1.364, 103.991, 480, true),
        new("NRT", "Tokyo", "JP", 35.772, 140.393, 540, true),
    ];

    private static readonly Dictionary<string, Airport> ByCode = All.ToDictionary(static a => a.Code, StringComparer.Ordinal);

    public static Airport? Find(string code) => ByCode.GetValueOrDefault(code);

    /// <summary>Great circle distance in kilometres, which drives block time and the base fare.</summary>
    public static double DistanceKm(Airport from, Airport to)
    {
        const double EarthRadiusKm = 6371;
        double lat1 = double.DegreesToRadians(from.Latitude);
        double lat2 = double.DegreesToRadians(to.Latitude);
        double dLat = lat2 - lat1;
        double dLon = double.DegreesToRadians(to.Longitude - from.Longitude);
        double h = (Math.Sin(dLat / 2) * Math.Sin(dLat / 2)) +
                   (Math.Cos(lat1) * Math.Cos(lat2) * Math.Sin(dLon / 2) * Math.Sin(dLon / 2));
        return 2 * EarthRadiusKm * Math.Asin(Math.Sqrt(h));
    }
}
