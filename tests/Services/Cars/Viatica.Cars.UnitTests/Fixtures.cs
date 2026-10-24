using Viatica.Cars.Domain;

namespace Viatica.Cars.UnitTests;

/// <summary>Small valid objects, so each test names only what it is about.</summary>
internal static class Fixtures
{
    public static readonly Guid Partner = Guid.Parse("c0000000-0000-4000-8000-000000000002");

    public static readonly DateTimeOffset Now = new(2026, 11, 2, 9, 0, 0, TimeSpan.Zero);

    /// <summary>Open 08:00 to 18:00 on weekdays, 09:00 to 13:00 on Saturday, closed on Sunday.</summary>
    public static IReadOnlyList<OpeningWindow> BusinessHours { get; } =
    [
        .. new[] { DayOfWeek.Monday, DayOfWeek.Tuesday, DayOfWeek.Wednesday, DayOfWeek.Thursday, DayOfWeek.Friday }
            .Select(static day => new OpeningWindow(day, new TimeOnly(8, 0), new TimeOnly(18, 0))),
        new OpeningWindow(DayOfWeek.Saturday, new TimeOnly(9, 0), new TimeOnly(13, 0)),
    ];

    public static RentalLocation Location(IEnumerable<OpeningWindow>? hours = null, Guid? partner = null) =>
        RentalLocation.Create(
            Guid.CreateVersion7(),
            partner ?? Partner,
            "Lisbon Airport",
            new Address("Aeroporto de Lisboa", "Lisbon", "1700-008", "PT"),
            new GeoPoint(38.7742, -9.1342),
            "LIS",
            hours ?? BusinessHours,
            new CancellationPolicy(48, CancellationPenalty.FirstDay),
            Now);

    public static CarCategory Category(int minimumAge = 21, string acriss = "ECMN") =>
        CarCategory.Create(
            Guid.CreateVersion7(), Partner, acriss, "Economy", 5, Transmission.Manual, FuelType.Petrol, 2, minimumAge, null, Now);
}
