using Viatica.Kernel;

namespace Viatica.Booking.Domain;

/// <summary>
/// What a stay or a car gives back when the traveller cancels (BKG-4). The partner's policy was copied onto the item
/// when the hold was placed, so a partner who tightens the policy later does not change what an earlier booking
/// can get back. Flights are not here: the airline's fare rules decide, and Flights reports the refund.
/// </summary>
public static class CancellationPolicies
{
    /// <summary>
    /// The refund for cancelling at <paramref name="requestedAt"/>. Up to <c>FreeCancellationHours</c> before
    /// <paramref name="startsAt"/> (inclusive) it is the whole total. After that the penalty applies: one unit kept,
    /// or everything. An item with no policy on record keeps everything, because a refund nobody promised is not
    /// ours to invent.
    /// </summary>
    public static Money Refund(Money total, int units, DateTimeOffset startsAt, int? freeCancellationHours, Penalty? penalty, DateTimeOffset requestedAt)
    {
        ArgumentNullException.ThrowIfNull(total);

        if (freeCancellationHours is { } hours && requestedAt <= startsAt - TimeSpan.FromHours(hours))
        {
            return total;
        }

        if (penalty != Penalty.FirstUnit)
        {
            return Money.Zero(total.Currency);
        }

        Money kept = KeptForFirstUnit(total, units);
        return kept < total ? total - kept : Money.Zero(total.Currency);
    }

    /// <summary>
    /// One unit's share of the total, rounded to the cent in the partner's favour. Rates differ from night to night
    /// and the hold only carries the total, so this is the average night, not the actual first night. That is a
    /// stated simplification (docs/services/booking.md).
    /// </summary>
    public static Money KeptForFirstUnit(Money total, int units)
    {
        ArgumentNullException.ThrowIfNull(total);

        decimal share = total.Amount / Math.Max(units, 1);
        return new Money(decimal.Round(share, total.MinorUnits, MidpointRounding.AwayFromZero), total.Currency);
    }

    /// <summary>
    /// Rental days the way Cars counts them: a started 24 hours with 59 minutes of grace. Booking needs the number
    /// to split a rental's total per day, and has no way to ask Cars.
    /// </summary>
    public static int RentalDays(DateTimeOffset pickUpAt, DateTimeOffset dropOffAt)
    {
        TimeSpan beyondGrace = dropOffAt - pickUpAt - TimeSpan.FromMinutes(59);
        return beyondGrace <= TimeSpan.Zero
            ? 1
            : (int)((beyondGrace.Ticks + TimeSpan.TicksPerDay - 1) / TimeSpan.TicksPerDay);
    }
}
