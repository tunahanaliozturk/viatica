using Viatica.Cars.Domain;
using Viatica.Kernel;

namespace Viatica.Cars.UnitTests;

public sealed class PricingTests
{
    private static readonly DateTimeOffset PickUp = new(2026, 12, 7, 9, 0, 0, TimeSpan.Zero);

    private static PartnerSettings Settings(decimal oneWayFee = 0m, decimal surcharge = 0m)
    {
        PartnerSettings settings = PartnerSettings.Defaults(Fixtures.Partner);
        settings.Change(Money.Eur(oneWayFee), Money.Eur(surcharge));
        return settings;
    }

    // A three day rental with a different rate on each day, the way weekend rates differ.
    private static (RentalPeriod Period, DailyRate[] Rates) ThreeDays()
    {
        RentalPeriod period = RentalPeriod.Create(PickUp, PickUp.AddHours(50));
        DailyRate[] rates = [.. period.InventoryDays().Zip<DateOnly, decimal, DailyRate>([40m, 40m, 55m], static (d, r) => new(d, Money.Eur(r)))];
        return (period, rates);
    }

    [Fact]
    public void The_total_is_the_sum_of_the_daily_rates()
    {
        (RentalPeriod period, DailyRate[] rates) = ThreeDays();

        PriceBreakdown price = Pricing.Quote(period, rates, oneWay: false, driverAge: 40, Fixtures.Category(), Settings());

        price.Rental.ShouldBe(Money.Eur(135m));
        price.OneWayFee.ShouldBe(Money.Eur(0m));
        price.YoungDriverSurcharge.ShouldBe(Money.Eur(0m));
        price.Total.ShouldBe(Money.Eur(135m));
    }

    [Fact]
    public void A_one_way_rental_adds_the_partners_flat_fee_once_whatever_the_length()
    {
        (RentalPeriod period, DailyRate[] rates) = ThreeDays();

        PriceBreakdown oneWay = Pricing.Quote(period, rates, oneWay: true, driverAge: 40, Fixtures.Category(), Settings(oneWayFee: 25m));
        PriceBreakdown roundTrip = Pricing.Quote(period, rates, oneWay: false, driverAge: 40, Fixtures.Category(), Settings(oneWayFee: 25m));

        oneWay.OneWayFee.ShouldBe(Money.Eur(25m));
        oneWay.Total.ShouldBe(Money.Eur(160m));
        roundTrip.OneWayFee.ShouldBe(Money.Eur(0m));
        roundTrip.Total.ShouldBe(Money.Eur(135m));
    }

    [Fact]
    public void A_partner_that_never_set_a_fee_charges_nothing_for_one_way()
    {
        (RentalPeriod period, DailyRate[] rates) = ThreeDays();

        PriceBreakdown price = Pricing.Quote(
            period, rates, oneWay: true, driverAge: 40, Fixtures.Category(), PartnerSettings.Defaults(Fixtures.Partner));

        price.OneWayFee.ShouldBe(Money.Eur(0m));
    }

    [Theory]
    [InlineData(21, true)]   // at the category minimum
    [InlineData(24, true)]
    [InlineData(25, false)]  // 25 is no longer young
    [InlineData(60, false)]
    public void Drivers_between_the_minimum_age_and_25_pay_the_surcharge_for_every_day(int age, bool surcharged)
    {
        (RentalPeriod period, DailyRate[] rates) = ThreeDays();

        PriceBreakdown price = Pricing.Quote(period, rates, oneWay: false, age, Fixtures.Category(minimumAge: 21), Settings(surcharge: 12m));

        price.YoungDriverSurcharge.ShouldBe(Money.Eur(surcharged ? 36m : 0m));
        price.Total.ShouldBe(Money.Eur(surcharged ? 171m : 135m));
    }

    [Theory]
    [InlineData(20)]
    [InlineData(18)]
    public void A_driver_under_the_category_minimum_is_too_young_and_no_surcharge_changes_that(int age)
    {
        (RentalPeriod period, DailyRate[] rates) = ThreeDays();

        ApiErrorException error = Should.Throw<ApiErrorException>(
            () => Pricing.Quote(period, rates, oneWay: false, age, Fixtures.Category(minimumAge: 21), Settings(surcharge: 12m)));

        error.Error.Code.ShouldBe(CarErrorCode.DriverTooYoung);
        error.Error.Target.ShouldBe("driverAge");
    }

    [Fact]
    public void A_category_with_a_higher_minimum_age_keeps_out_drivers_a_cheaper_one_lets_in()
    {
        (RentalPeriod period, DailyRate[] rates) = ThreeDays();

        Pricing.Quote(period, rates, oneWay: false, driverAge: 23, Fixtures.Category(minimumAge: 21), Settings());
        Should.Throw<ApiErrorException>(
            () => Pricing.Quote(period, rates, oneWay: false, driverAge: 23, Fixtures.Category(minimumAge: 25), Settings()));
    }

    [Fact]
    public void Surcharge_and_one_way_fee_stack_with_the_rental()
    {
        (RentalPeriod period, DailyRate[] rates) = ThreeDays();

        PriceBreakdown price = Pricing.Quote(period, rates, oneWay: true, driverAge: 22, Fixtures.Category(), Settings(oneWayFee: 25m, surcharge: 12m));

        price.Total.ShouldBe(price.Rental + price.OneWayFee + price.YoungDriverSurcharge);
        price.Total.ShouldBe(Money.Eur(196m));
    }
}
