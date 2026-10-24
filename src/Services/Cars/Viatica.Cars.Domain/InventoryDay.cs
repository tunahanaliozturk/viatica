namespace Viatica.Cars.Domain;

/// <summary>
/// The cars of one category at one location on one calendar day: how many exist, how many are rented out
/// (<see cref="Sold"/>), how many are promised to a checkout in progress (<see cref="Held"/>), and the day's rate.
/// </summary>
/// <remarks>
/// <see cref="Sold"/> and <see cref="Held"/> are never written from here. They move only through conditional
/// <c>UPDATE</c> statements in the use cases, which is what keeps <c>Sold + Held &lt;= Allotment</c> true under
/// contention; the database also holds the line with a check constraint.
/// </remarks>
public sealed class InventoryDay
{
    private InventoryDay()
    {
    }

    public Guid LocationId { get; private set; }

    public Guid CategoryId { get; private set; }

    public DateOnly Date { get; private set; }

    public int Allotment { get; private set; }

    public int Sold { get; private set; }

    public int Held { get; private set; }

    /// <summary>The rate for the day in euros. Every price on the platform is EUR in v1.</summary>
    public decimal Rate { get; private set; }

    public int Available => Allotment - Sold - Held;

    public static InventoryDay Open(Guid locationId, Guid categoryId, DateOnly date, int allotment, decimal rate)
    {
        var day = new InventoryDay { LocationId = locationId, CategoryId = categoryId, Date = date };
        day.Set(allotment, rate);
        return day;
    }

    /// <summary>
    /// Changes the allotment and rate. An allotment below what is already rented or promised is refused: those
    /// bookings exist, and the partner cancels them or keeps the cars.
    /// </summary>
    public void Set(int allotment, decimal rate)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(allotment);
        ArgumentOutOfRangeException.ThrowIfNegative(rate);

        if (allotment < Sold + Held)
        {
            throw CarErrors.AllotmentBelowCommitments(Date);
        }

        Allotment = allotment;
        Rate = rate;
    }
}
