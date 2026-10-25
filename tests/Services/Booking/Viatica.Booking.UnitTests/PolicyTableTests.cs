using Viatica.Booking.Domain;
using Viatica.Kernel;

namespace Viatica.Booking.UnitTests;

/// <summary>
/// BKG-4, the arithmetic: what a stay or a car gives back, row by row. Flights are not here: the airline's fare rules
/// decide, and the saga takes the refund Flights reports (<see cref="CancellationTests"/>).
/// </summary>
public sealed class PolicyTableTests
{
    private static readonly DateTimeOffset Start = new(2026, 12, 20, 15, 0, 0, TimeSpan.Zero);

    public static TheoryData<string, decimal, int, int?, Penalty?, int, decimal> Stays => new()
    {
        // what, total, nights, free hours, penalty, minutes before start when asked, refund
        { "long before the window", 300m, 3, 48, Penalty.FirstUnit, 60 * 24 * 30, 300m },
        { "a minute before the window closes", 300m, 3, 48, Penalty.FirstUnit, (48 * 60) + 1, 300m },
        { "exactly when the window closes: still free", 300m, 3, 48, Penalty.FirstUnit, 48 * 60, 300m },
        { "a minute after the window closed, first night kept", 300m, 3, 48, Penalty.FirstUnit, (48 * 60) - 1, 200m },
        { "a minute after the window closed, all kept", 300m, 3, 48, Penalty.FullAmount, (48 * 60) - 1, 0m },
        { "the day before, first night kept", 300m, 3, 48, Penalty.FirstUnit, 24 * 60, 200m },
        { "an hour before, all kept", 300m, 3, 48, Penalty.FullAmount, 60, 0m },
        { "at the start, no free window, still free", 300m, 3, 0, Penalty.FullAmount, 0, 300m },
        { "a minute after the start, no free window", 300m, 3, 0, Penalty.FirstUnit, -1, 200m },
        { "after the start, all kept", 300m, 3, 48, Penalty.FullAmount, -600, 0m },
        { "a night that does not divide: 100 over 3 keeps 33.33", 100m, 3, 48, Penalty.FirstUnit, 60, 66.67m },
        { "a night that rounds in the partner's favour: 100 over 6 keeps 16.67", 100m, 6, 48, Penalty.FirstUnit, 60, 83.33m },
        { "one night only: the first night is the stay", 120m, 1, 48, Penalty.FirstUnit, 60, 0m },
        { "no nights on record is read as one", 120m, 0, 48, Penalty.FirstUnit, 60, 0m },
        { "a stay worth less than a cent a night keeps nothing", 0.01m, 3, 48, Penalty.FirstUnit, 60, 0.01m },
        { "no policy on record: nothing was promised", 300m, 3, null, null, 60 * 24 * 30, 0m },
        { "a free window but no penalty on record", 300m, 3, 48, null, 60, 0m },
    };

    [Theory]
    [MemberData(nameof(Stays))]
    public void A_stay_or_car_refunds_what_its_policy_says_for_the_moment_it_was_asked(
        string what, decimal total, int units, int? freeHours, Penalty? penalty, int minutesBeforeStart, decimal expected)
    {
        Money refund = CancellationPolicies.Refund(Money.Eur(total), units, Start, freeHours, penalty, Start.AddMinutes(-minutesBeforeStart));

        refund.ShouldBe(Money.Eur(expected), what);
    }

    public static TheoryData<int, int, int> RentalDayCounts => new()
    {
        // hours, minutes, days
        { 0, 1, 1 },
        { 24, 0, 1 },
        { 24, 59, 1 },
        { 25, 0, 2 },
        { 48, 59, 2 },
        { 49, 0, 3 },
        { 72, 0, 3 },
        { 72, 59, 3 },
        { 73, 0, 4 },
    };

    [Theory]
    [MemberData(nameof(RentalDayCounts))]
    public void Rental_days_are_counted_the_way_cars_counts_them_with_59_minutes_of_grace(int hours, int minutes, int days)
    {
        DateTimeOffset pickUp = new(2026, 12, 20, 10, 0, 0, TimeSpan.FromHours(1));

        CancellationPolicies.RentalDays(pickUp, pickUp.AddHours(hours).AddMinutes(minutes)).ShouldBe(days);
    }

    [Fact]
    public void A_cars_first_day_is_its_total_over_its_days()
    {
        DateTimeOffset pickUp = new(2026, 12, 20, 10, 0, 0, TimeSpan.Zero);
        int days = CancellationPolicies.RentalDays(pickUp, pickUp.AddDays(3));

        CancellationPolicies.KeptForFirstUnit(Money.Eur(90m), days).ShouldBe(Money.Eur(30m));
        CancellationPolicies.Refund(Money.Eur(90m), days, pickUp, 48, Penalty.FirstUnit, pickUp.AddHours(-24)).ShouldBe(Money.Eur(60m));
    }
}
