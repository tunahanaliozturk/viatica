namespace Viatica.Cars.Domain;

/// <summary>
/// From pick-up to drop-off, and how many rental days that is.
/// </summary>
/// <remarks>
/// <para>
/// A rental day is a started 24-hour period, with 59 minutes of grace: 24 hours 59 minutes is one day, one second
/// more is two. Rental desks work this way because a car back an hour late on the same evening is a nuisance, not a
/// second day of income, and 59 is the longest delay that is still "less than an hour".
/// </para>
/// <para>
/// The instants carry the location's own offset (the client sends local time), so the wall clock in
/// <see cref="PickUpAt"/> is the location's. The inventory is counted on the calendar day of the pick-up and the
/// days after it, one for each rental day. Elapsed time between the two instants is absolute, so a rental across a
/// clock change counts real hours.
/// </para>
/// </remarks>
public sealed record RentalPeriod
{
    public const int MaxDays = 30;

    public static readonly TimeSpan Grace = TimeSpan.FromMinutes(59);

    private RentalPeriod(DateTimeOffset pickUpAt, DateTimeOffset dropOffAt, int days)
    {
        PickUpAt = pickUpAt;
        DropOffAt = dropOffAt;
        Days = days;
    }

    public DateTimeOffset PickUpAt { get; }

    public DateTimeOffset DropOffAt { get; }

    public int Days { get; }

    /// <summary>The calendar day of the pick-up, in the pick-up location's own time.</summary>
    public DateOnly FirstDay => DateOnly.FromDateTime(PickUpAt.DateTime);

    public DateOnly LastDay => FirstDay.AddDays(Days - 1);

    public IEnumerable<DateOnly> InventoryDays()
    {
        DateOnly day = FirstDay;
        for (int i = 0; i < Days; i++)
        {
            yield return day;
            day = day.AddDays(1);
        }
    }

    public static RentalPeriod Create(DateTimeOffset pickUpAt, DateTimeOffset dropOffAt)
    {
        if (dropOffAt <= pickUpAt)
        {
            throw CarErrors.DropOffBeforePickUp();
        }

        int days = DaysBetween(pickUpAt, dropOffAt);
        return days > MaxDays ? throw CarErrors.RentalTooLong() : new RentalPeriod(pickUpAt, dropOffAt, days);
    }

    public static int DaysBetween(DateTimeOffset pickUpAt, DateTimeOffset dropOffAt)
    {
        TimeSpan beyondGrace = dropOffAt - pickUpAt - Grace;
        if (beyondGrace <= TimeSpan.Zero)
        {
            return 1;
        }

        // Integer ceiling on ticks. A double would be off by a tick at the boundary, and the boundary is the point.
        return (int)((beyondGrace.Ticks + TimeSpan.TicksPerDay - 1) / TimeSpan.TicksPerDay);
    }
}
