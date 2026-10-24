using Microsoft.Extensions.Time.Testing;
using Moongazing.OrionGuard.Core;
using Viatica.Kernel;
using Viatica.Stays.Application;
using Viatica.Stays.Application.Features.Nights;
using Viatica.Stays.Application.Features.Properties;
using Viatica.Stays.Application.Features.Quotes;
using Viatica.Stays.Application.Features.RoomTypes;
using Viatica.Stays.Domain;

namespace Viatica.Stays.UnitTests;

public sealed class ValidatorTests
{
    private static readonly FakeTimeProvider Clock = new(new DateTimeOffset(2026, 11, 20, 10, 0, 0, TimeSpan.Zero));
    private static readonly DateOnly Today = new(2026, 11, 20);

    private static readonly Guid Property = Guid.CreateVersion7();
    private static readonly Guid Room = Guid.CreateVersion7();

    // A rule with no code of its own reports an empty one; the validation behaviour turns that into InvalidValue.
    private static async Task<IReadOnlyList<(string Code, string Target)>> ErrorsAsync<T>(Moongazing.OrionGuard.DependencyInjection.AbstractValidator<T> validator, T request)
    {
        GuardResult result = await validator.ValidateAsync(request);
        return [.. result.Errors.Select(static e => (e.ErrorCode ?? "", e.ParameterName ?? ""))];
    }

    private static CreateQuote Quote(DateOnly? checkIn = null, DateOnly? checkOut = null, int quantity = 1, int guests = 2) =>
        new(Property, Room, checkIn ?? Today.AddDays(10), checkOut ?? Today.AddDays(13), quantity, guests);

    [Fact]
    public async Task A_sensible_quote_request_passes()
    {
        (await ErrorsAsync(new CreateQuoteValidator(Clock), Quote())).ShouldBeEmpty();
    }

    [Fact]
    public async Task Check_out_on_or_before_check_in_is_CheckOutBeforeCheckIn()
    {
        var validator = new CreateQuoteValidator(Clock);

        (await ErrorsAsync(validator, Quote(checkOut: Today.AddDays(10)))).ShouldContain((StayErrorCode.CheckOutBeforeCheckIn, "CheckOut"));
        (await ErrorsAsync(validator, Quote(checkOut: Today.AddDays(5)))).ShouldContain((StayErrorCode.CheckOutBeforeCheckIn, "CheckOut"));
    }

    [Fact]
    public async Task Thirty_nights_pass_and_thirty_one_is_StayTooLong()
    {
        var validator = new CreateQuoteValidator(Clock);
        DateOnly checkIn = Today.AddDays(10);

        (await ErrorsAsync(validator, Quote(checkIn, checkIn.AddDays(30)))).ShouldBeEmpty();
        (await ErrorsAsync(validator, Quote(checkIn, checkIn.AddDays(31)))).ShouldBe([(StayErrorCode.StayTooLong, "CheckOut")]);
    }

    [Fact]
    public async Task A_reversed_range_is_not_also_reported_as_too_long()
    {
        (await ErrorsAsync(new CreateQuoteValidator(Clock), Quote(checkOut: Today))).Select(static e => e.Code)
            .ShouldBe([StayErrorCode.CheckOutBeforeCheckIn]);
    }

    [Fact]
    public async Task Yesterday_is_still_allowed_for_a_traveller_ahead_of_us_and_two_days_ago_is_not()
    {
        var validator = new CreateQuoteValidator(Clock);

        (await ErrorsAsync(validator, Quote(Today.AddDays(-1), Today.AddDays(2)))).ShouldBeEmpty();
        (await ErrorsAsync(validator, Quote(Today.AddDays(-2), Today.AddDays(2)))).ShouldBe([(StayErrorCode.CheckInInPast, "CheckIn")]);
    }

    [Theory]
    [InlineData(0, 2)]
    [InlineData(10, 2)]
    [InlineData(1, 0)]
    public async Task Quantity_and_guests_have_bounds(int quantity, int guests)
    {
        (await ErrorsAsync(new CreateQuoteValidator(Clock), Quote(quantity: quantity, guests: guests))).ShouldNotBeEmpty();
    }

    [Fact]
    public async Task Every_broken_rule_is_reported_at_once()
    {
        var errors = await ErrorsAsync(new CreateQuoteValidator(Clock), Quote(Today.AddDays(-5), Today.AddDays(-9), quantity: 0));

        errors.Select(static e => e.Target).ShouldBe(["CheckOut", "CheckIn", "Quantity"], ignoreOrder: true);
    }

    private static SetNights Nights(DateOnly from, DateOnly to, int allotment = 5, Money? rate = null) =>
        new(Property, Room, Caller.Anonymous, from, to, allotment, rate);

    [Fact]
    public async Task A_year_of_nights_is_the_most_a_calendar_edit_may_reach()
    {
        var validator = new SetNightsValidator(Clock);

        (await ErrorsAsync(validator, Nights(Today, Today.AddDays(365)))).ShouldBeEmpty();
        (await ErrorsAsync(validator, Nights(Today, Today.AddDays(366)))).ShouldBe([(StayErrorCode.OutsideRateWindow, "To")]);
    }

    [Fact]
    public async Task Nights_in_the_past_and_reversed_ranges_are_refused()
    {
        var validator = new SetNightsValidator(Clock);

        (await ErrorsAsync(validator, Nights(Today.AddDays(-2), Today))).ShouldBe([(StayErrorCode.OutsideRateWindow, "From")]);
        (await ErrorsAsync(validator, Nights(Today.AddDays(5), Today.AddDays(4)))).ShouldContain((StayErrorCode.InvalidDateRange, "To"));
    }

    [Theory]
    [InlineData(-1)]
    [InlineData(10_001)]
    public async Task The_allotment_has_bounds(int allotment)
    {
        (await ErrorsAsync(new SetNightsValidator(Clock), Nights(Today, Today.AddDays(1), allotment))).ShouldBe([("", "Allotment")]);
    }

    [Fact]
    public async Task A_rate_must_be_positive_euro()
    {
        var validator = new SetNightsValidator(Clock);

        (await ErrorsAsync(validator, Nights(Today, Today.AddDays(1), rate: Money.Eur(120m)))).ShouldBeEmpty();
        (await ErrorsAsync(validator, Nights(Today, Today.AddDays(1), rate: Money.Eur(0m)))).ShouldNotBeEmpty();
        (await ErrorsAsync(validator, Nights(Today, Today.AddDays(1), rate: new Money(120m, "USD")))).ShouldNotBeEmpty();
    }

    internal static PropertyDetails Details() => new(
        "Casa do Mar",
        "Rooms above the harbour.",
        new Address("Rua das Flores 12", "Lisbon", "1200-195", "PT", "Lisboa"),
        new GeoPoint(38.71, -9.14),
        4,
        ["wifi", "pool"],
        new TimeOnly(15, 0),
        new TimeOnly(11, 0),
        new CancellationPolicy(48, CancellationPenalty.FirstUnit));

    private static CreateProperty Create(PropertyDetails details) => new(Property, Guid.CreateVersion7(), details);

    [Fact]
    public async Task A_complete_property_passes()
    {
        (await ErrorsAsync(new CreatePropertyValidator(), Create(Details()))).ShouldBeEmpty();
    }

    [Fact]
    public async Task Property_errors_name_the_field_the_client_sent()
    {
        PropertyDetails broken = Details() with
        {
            Name = " ",
            StarRating = 6,
            Address = new Address("", "Lisbon", "1200-195", "Portugal"),
            Location = new GeoPoint(91, -9.14),
            CancellationPolicy = new CancellationPolicy(-1, CancellationPenalty.FullAmount),
        };

        var targets = (await ErrorsAsync(new CreatePropertyValidator(), Create(broken))).Select(static e => e.Target);

        targets.ShouldBe(
            ["Name", "StarRating", "Address.Line1", "Address.CountryCode", "Location.Latitude", "CancellationPolicy.FreeUntilHoursBeforeStart"],
            ignoreOrder: true);
    }

    [Fact]
    public async Task A_patch_checks_only_what_it_carries()
    {
        var validator = new UpdatePropertyValidator();

        (await ErrorsAsync(validator, new UpdateProperty(Property, Caller.Anonymous, new PropertyPatch(), null))).ShouldBeEmpty();
        (await ErrorsAsync(validator, new UpdateProperty(Property, Caller.Anonymous, new PropertyPatch(StarRating: 9), null)))
            .ShouldBe([("", "StarRating")]);
    }

    [Fact]
    public async Task Room_type_rules()
    {
        var validator = new PutRoomTypeValidator();
        var ok = new RoomTypeDetails("Double", 2, "1 double", ["balcony"], Money.Eur(120m));

        (await ErrorsAsync(validator, new PutRoomType(Property, Room, Caller.Anonymous, ok))).ShouldBeEmpty();

        var broken = ok with { Name = "", MaxOccupancy = 0, BaseRate = new Money(10m, "USD") };
        (await ErrorsAsync(validator, new PutRoomType(Property, Room, Caller.Anonymous, broken))).Select(static e => e.Target)
            .ShouldBe(["Name", "MaxOccupancy", "BaseRate"], ignoreOrder: true);
    }
}
