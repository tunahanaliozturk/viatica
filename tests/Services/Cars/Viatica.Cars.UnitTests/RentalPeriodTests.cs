using Viatica.Cars.Domain;
using Viatica.Kernel;

namespace Viatica.Cars.UnitTests;

public sealed class RentalPeriodTests
{
    private static readonly DateTimeOffset PickUp = new(2026, 12, 1, 10, 0, 0, TimeSpan.FromHours(1));

    [Theory]
    [InlineData(0, 1, 0, 1)]       // an hour is one day: a rental is never free
    [InlineData(23, 59, 0, 1)]
    [InlineData(24, 0, 0, 1)]      // exactly one 24-hour period
    [InlineData(24, 58, 59, 1)]
    [InlineData(24, 59, 0, 1)]     // the grace period is 59 minutes, inclusive
    [InlineData(24, 59, 1, 2)]     // one second past it starts a second day
    [InlineData(25, 0, 0, 2)]
    [InlineData(48, 0, 0, 2)]
    [InlineData(48, 59, 0, 2)]
    [InlineData(48, 59, 1, 3)]
    [InlineData(72, 0, 0, 3)]
    [InlineData(719, 59, 0, 30)]   // 29 days 23 h 59 min: the 30th day is started
    [InlineData(720, 0, 0, 30)]    // exactly 30 days
    [InlineData(720, 59, 0, 30)]   // 30 days and the grace period is still 30 days
    public void Rental_days_are_started_24_hour_periods_with_59_minutes_of_grace(int hours, int minutes, int seconds, int days)
    {
        DateTimeOffset dropOff = PickUp.AddHours(hours).AddMinutes(minutes).AddSeconds(seconds);

        RentalPeriod.DaysBetween(PickUp, dropOff).ShouldBe(days);
    }

    [Fact]
    public void A_rental_of_30_days_and_one_second_past_the_grace_is_too_long()
    {
        DateTimeOffset dropOff = PickUp.AddDays(30).AddMinutes(59).AddSeconds(1);

        ApiErrorException error = Should.Throw<ApiErrorException>(() => RentalPeriod.Create(PickUp, dropOff));

        error.Error.Code.ShouldBe(CarErrorCode.RentalTooLong);
    }

    [Fact]
    public void A_rental_of_exactly_the_maximum_is_allowed()
    {
        RentalPeriod period = RentalPeriod.Create(PickUp, PickUp.AddDays(30).AddMinutes(59));

        period.Days.ShouldBe(RentalPeriod.MaxDays);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-5)]
    public void A_drop_off_at_or_before_the_pick_up_is_refused(int minutesAfter)
    {
        ApiErrorException error = Should.Throw<ApiErrorException>(
            () => RentalPeriod.Create(PickUp, PickUp.AddMinutes(minutesAfter)));

        error.Error.Code.ShouldBe(CarErrorCode.DropOffBeforePickUp);
    }

    [Fact]
    public void Inventory_is_counted_from_the_pick_up_day_for_each_rental_day()
    {
        // Out on Dec 1 at 10:00, back on Dec 3 at 12:00: 50 hours, three days started.
        RentalPeriod period = RentalPeriod.Create(PickUp, PickUp.AddHours(50));

        period.Days.ShouldBe(3);
        period.InventoryDays().ShouldBe([new DateOnly(2026, 12, 1), new DateOnly(2026, 12, 2), new DateOnly(2026, 12, 3)]);
        period.LastDay.ShouldBe(new DateOnly(2026, 12, 3));
    }

    [Fact]
    public void The_pick_up_day_is_the_locations_calendar_day_not_the_utc_day()
    {
        // 00:30 on Dec 1 in UTC+2 is still Nov 30 in UTC. The car leaves the lot on Dec 1.
        var earlyMorning = new DateTimeOffset(2026, 12, 1, 0, 30, 0, TimeSpan.FromHours(2));

        RentalPeriod period = RentalPeriod.Create(earlyMorning, earlyMorning.AddHours(2));

        earlyMorning.UtcDateTime.Date.ShouldBe(new DateTime(2026, 11, 30));
        period.FirstDay.ShouldBe(new DateOnly(2026, 12, 1));
    }

    [Fact]
    public void Elapsed_time_is_real_time_across_a_clock_change()
    {
        // Lisbon clocks go back on 25 Oct 2026 at 02:00 (+01:00 to +00:00). Same wall clock 24 hours apart is
        // 25 real hours, which is two days; the grace covers up to 24 h 59 min only.
        var before = new DateTimeOffset(2026, 10, 24, 12, 0, 0, TimeSpan.FromHours(1));
        var after = new DateTimeOffset(2026, 10, 25, 12, 0, 0, TimeSpan.Zero);

        (after - before).ShouldBe(TimeSpan.FromHours(25));
        RentalPeriod.DaysBetween(before, after).ShouldBe(2);
    }
}
