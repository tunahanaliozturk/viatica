namespace Viatica.Stays.Domain;

/// <summary>
/// The nights of a stay: the dates from check-in up to, not including, check-out. A guest who checks out on the
/// 4th does not occupy the night of the 4th, so that night is not held, sold or priced.
/// </summary>
public readonly record struct StayDates(DateOnly CheckIn, DateOnly CheckOut)
{
    /// <summary>Longer stays are two bookings; most channels cap at 30 and so does this one.</summary>
    public const int MaxNights = 30;

    /// <summary>How far ahead a partner can set rates and allotments, counted from today.</summary>
    public const int RateWindowDays = 365;

    /// <summary>
    /// The earliest date a stay may start or a rate may be set for: today in UTC, less a day, so a traveller or a
    /// partner east or west of Greenwich is not told that their own "today" is in the past.
    /// </summary>
    public static DateOnly EarliestCheckIn(DateTimeOffset now) => DateOnly.FromDateTime(now.UtcDateTime).AddDays(-1);

    public static DateOnly LatestRateDate(DateTimeOffset now) => DateOnly.FromDateTime(now.UtcDateTime).AddDays(RateWindowDays);

    public int Nights => CheckOut.DayNumber - CheckIn.DayNumber;

    public IEnumerable<DateOnly> EachNight()
    {
        for (DateOnly night = CheckIn; night < CheckOut; night = night.AddDays(1))
        {
            yield return night;
        }
    }
}
