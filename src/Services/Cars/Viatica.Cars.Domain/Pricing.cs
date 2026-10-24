using Viatica.Kernel;

namespace Viatica.Cars.Domain;

public sealed record DailyRate(DateOnly Date, Money Rate);

/// <summary>What a rental costs and why, line by line. The total is the sum of the other three.</summary>
public sealed record PriceBreakdown(
    IReadOnlyList<DailyRate> Days,
    Money Rental,
    Money OneWayFee,
    Money YoungDriverSurcharge,
    Money Total);

public static class Pricing
{
    /// <summary>Drivers under this age, and at or over the category's minimum, pay the young driver surcharge.</summary>
    public const int YoungDriverAge = 25;

    public static PriceBreakdown Quote(
        RentalPeriod period, IReadOnlyList<DailyRate> days, bool oneWay, int driverAge, CarCategory category, PartnerSettings settings)
    {
        ArgumentNullException.ThrowIfNull(period);
        ArgumentNullException.ThrowIfNull(days);
        ArgumentNullException.ThrowIfNull(category);
        ArgumentNullException.ThrowIfNull(settings);

        if (driverAge < category.MinimumDriverAge)
        {
            throw CarErrors.DriverTooYoung(category.MinimumDriverAge);
        }

        if (days.Count != period.Days)
        {
            throw new ArgumentException("There must be one rate for each rental day.", nameof(days));
        }

        Money rental = days.Aggregate(Money.Zero(Money.Euro), static (sum, day) => sum + day.Rate);
        Money oneWayFee = oneWay ? settings.OneWayFee : Money.Zero(Money.Euro);
        Money surcharge = driverAge < YoungDriverAge
            ? settings.YoungDriverSurchargePerDay * period.Days
            : Money.Zero(Money.Euro);

        return new PriceBreakdown(days, rental, oneWayFee, surcharge, rental + oneWayFee + surcharge);
    }
}
