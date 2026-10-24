using Microsoft.Extensions.Time.Testing;
using Moongazing.OrionGuard.Core;
using Moongazing.OrionGuard.DependencyInjection;
using Viatica.Cars.Application.Features.GetQuote;
using Viatica.Cars.Application.Features.PartnerSettings;
using Viatica.Cars.Application.Features.PutCategory;
using Viatica.Cars.Application.Features.PutLocation;
using Viatica.Cars.Application.Features.SetInventory;
using Viatica.Cars.Domain;
using Viatica.Kernel;

namespace Viatica.Cars.UnitTests;

public sealed class ValidatorTests
{
    private static readonly FakeTimeProvider Clock = new(Fixtures.Now);

    private static readonly PutLocation Location = new(
        Fixtures.Partner,
        Guid.NewGuid(),
        null,
        "Lisbon Airport",
        new Address("Aeroporto de Lisboa", "Lisbon", "1700-008", "PT"),
        new GeoPoint(38.77, -9.13),
        "LIS",
        Fixtures.BusinessHours,
        new CancellationPolicy(48, CancellationPenalty.FirstDay));

    private static readonly PutCategory Category = new(
        Fixtures.Partner, Guid.NewGuid(), null, "ECMN", "Economy", 5, Transmission.Manual, FuelType.Petrol, 2, 21, null);

    private static readonly SetInventory Inventory = new(
        Fixtures.Partner, Guid.NewGuid(), Guid.NewGuid(), new DateOnly(2026, 12, 1), new DateOnly(2026, 12, 31), 5, Money.Eur(40m));

    private static string[] Codes(GuardResult result) => [.. result.Errors.Select(static e => e.ErrorCode ?? "")];

    private static async Task<string[]> Run<T>(AbstractValidator<T> validator, T request) =>
        Codes(await validator.ValidateAsync(request, CancellationToken.None));

    [Fact]
    public async Task A_valid_location_passes() =>
        (await Run(new PutLocationValidator(), Location)).ShouldBeEmpty();

    [Fact]
    public async Task A_location_reports_every_broken_field_at_once()
    {
        PutLocation broken = Location with
        {
            Name = " ",
            AirportCode = "LISB",
            Position = new GeoPoint(120, 0),
            Address = Location.Address with { CountryCode = "PRT" },
            OpeningHours = [Fixtures.BusinessHours[0], Fixtures.BusinessHours[0]],
        };

        (await Run(new PutLocationValidator(), broken)).ShouldBe(
            ["InvalidName", "InvalidCountryCode", "InvalidPosition", "InvalidAirportCode", "InvalidOpeningHours"],
            ignoreOrder: true);
    }

    [Fact]
    public async Task Opening_hours_must_close_after_they_open_in_whole_minutes()
    {
        PutLocation backwards = Location with { OpeningHours = [new OpeningWindow(DayOfWeek.Monday, new TimeOnly(18, 0), new TimeOnly(8, 0))] };
        PutLocation seconds = Location with { OpeningHours = [new OpeningWindow(DayOfWeek.Monday, new TimeOnly(8, 0, 30), new TimeOnly(18, 0))] };

        (await Run(new PutLocationValidator(), backwards)).ShouldBe(["InvalidOpeningHours"]);
        (await Run(new PutLocationValidator(), seconds)).ShouldBe(["InvalidOpeningHours"]);
    }

    [Fact]
    public async Task A_valid_category_passes() =>
        (await Run(new PutCategoryValidator(), Category)).ShouldBeEmpty();

    [Theory]
    [InlineData("EC1N")]
    [InlineData("ECM")]
    public async Task An_acriss_code_that_is_not_four_letters_is_refused(string acriss) =>
        (await Run(new PutCategoryValidator(), Category with { Acriss = acriss })).ShouldBe(["InvalidAcriss"]);

    [Fact]
    public async Task A_category_image_must_be_http_or_https()
    {
        (await Run(new PutCategoryValidator(), Category with { ImageUrl = new Uri("https://cdn.example/ecmn.png") })).ShouldBeEmpty();
        (await Run(new PutCategoryValidator(), Category with { ImageUrl = new Uri("javascript:alert(1)") })).ShouldBe(["InvalidImageUrl"]);
        (await Run(new PutCategoryValidator(), Category with { ImageUrl = new Uri("/relative.png", UriKind.Relative) })).ShouldBe(["InvalidImageUrl"]);
    }

    [Theory]
    [InlineData(0, 21, "InvalidSeats")]
    [InlineData(21, 21, "InvalidSeats")]
    [InlineData(5, 17, "InvalidMinimumDriverAge")]
    [InlineData(5, 100, "InvalidMinimumDriverAge")]
    public async Task Seats_and_minimum_age_have_limits(int seats, int minimumAge, string code) =>
        (await Run(new PutCategoryValidator(), Category with { Seats = seats, MinimumDriverAge = minimumAge })).ShouldContain(code);

    [Fact]
    public async Task A_valid_inventory_range_passes() =>
        (await Run(new SetInventoryValidator(Clock), Inventory)).ShouldBeEmpty();

    [Fact]
    public async Task An_inventory_range_is_at_most_366_days_and_runs_forwards()
    {
        SetInventory longRange = Inventory with { From = new DateOnly(2026, 11, 2), To = new DateOnly(2027, 11, 2) };   // 366 days
        SetInventory tooLong = Inventory with { From = new DateOnly(2026, 11, 2), To = new DateOnly(2027, 11, 3) };     // 367
        SetInventory backwards = Inventory with { From = new DateOnly(2026, 12, 5), To = new DateOnly(2026, 12, 1) };

        (await Run(new SetInventoryValidator(Clock), longRange)).ShouldBeEmpty();
        (await Run(new SetInventoryValidator(Clock), tooLong)).ShouldContain("InvalidDateRange");
        (await Run(new SetInventoryValidator(Clock), backwards)).ShouldContain("InvalidDateRange");
    }

    [Fact]
    public async Task Inventory_cannot_be_opened_in_the_past_or_beyond_a_year_ahead()
    {
        // The clock reads 2 Nov 2026. One day of slack is allowed either side for the partner's local date.
        (await Run(new SetInventoryValidator(Clock), Inventory with { From = new DateOnly(2026, 11, 1), To = new DateOnly(2026, 11, 5) })).ShouldBeEmpty();
        (await Run(new SetInventoryValidator(Clock), Inventory with { From = new DateOnly(2026, 10, 31), To = new DateOnly(2026, 11, 5) })).ShouldBe(["DateInThePast"]);
        (await Run(new SetInventoryValidator(Clock), Inventory with { From = new DateOnly(2027, 10, 1), To = new DateOnly(2027, 11, 2) })).ShouldBeEmpty();
        (await Run(new SetInventoryValidator(Clock), Inventory with { From = new DateOnly(2027, 10, 1), To = new DateOnly(2027, 11, 3) })).ShouldBe(["DateTooFarAhead"]);
    }

    [Theory]
    [InlineData(-1, "InvalidAllotment")]
    [InlineData(1001, "InvalidAllotment")]
    public async Task An_allotment_is_0_to_1000(int allotment, string code) =>
        (await Run(new SetInventoryValidator(Clock), Inventory with { Allotment = allotment })).ShouldBe([code]);

    [Fact]
    public async Task A_rate_is_euros_and_not_negative()
    {
        (await Run(new SetInventoryValidator(Clock), Inventory with { Rate = new Money(40m, "USD") })).ShouldBe(["InvalidRate"]);
        (await Run(new SetInventoryValidator(Clock), Inventory with { Rate = Money.Eur(-1m) })).ShouldBe(["InvalidRate"]);
        (await Run(new SetInventoryValidator(Clock), Inventory with { Rate = Money.Eur(10_000.01m) })).ShouldBe(["InvalidRate"]);
    }

    [Fact]
    public async Task Fees_are_euros_between_zero_and_ten_thousand()
    {
        var valid = new PutPartnerSettings(Fixtures.Partner, null, Money.Eur(25m), Money.Eur(12m));

        (await Run(new PutPartnerSettingsValidator(), valid)).ShouldBeEmpty();
        (await Run(new PutPartnerSettingsValidator(), valid with { OneWayFee = Money.Eur(-1m) })).ShouldBe(["InvalidFee"]);
        (await Run(new PutPartnerSettingsValidator(), valid with { YoungDriverSurchargePerDay = new Money(12m, "USD") })).ShouldBe(["InvalidFee"]);
    }

    [Theory]
    [InlineData(17, false)]
    [InlineData(18, true)]
    [InlineData(99, true)]
    [InlineData(100, false)]
    public async Task A_quote_driver_is_18_to_99(int age, bool valid)
    {
        var quote = new GetQuote(Guid.NewGuid(), Guid.NewGuid(), null, Fixtures.Now.AddDays(10), Fixtures.Now.AddDays(12), age);

        (await Run(new GetQuoteValidator(), quote)).ShouldBe(valid ? [] : ["InvalidDriverAge"]);
    }
}
