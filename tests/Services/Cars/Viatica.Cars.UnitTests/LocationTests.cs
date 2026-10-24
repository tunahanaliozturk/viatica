using Viatica.Cars.Domain;
using Viatica.Kernel;

namespace Viatica.Cars.UnitTests;

public sealed class LocationTests
{
    // 2026-12-07 is a Monday, 12-12 a Saturday, 12-13 a Sunday.
    private static DateTimeOffset At(int day, int hour, int minute, int second = 0) =>
        new(2026, 12, day, hour, minute, second, TimeSpan.FromHours(1));

    [Theory]
    [InlineData(7, 7, 59, false)]   // a minute before it opens
    [InlineData(7, 8, 0, true)]     // the opening minute
    [InlineData(7, 12, 30, true)]
    [InlineData(7, 18, 0, true)]    // the closing minute still counts
    [InlineData(7, 18, 1, false)]
    [InlineData(12, 9, 0, true)]    // Saturday has shorter hours
    [InlineData(12, 13, 0, true)]
    [InlineData(12, 13, 1, false)]
    [InlineData(12, 8, 59, false)]
    [InlineData(13, 12, 0, false)]  // closed all day Sunday
    public void A_desk_is_open_between_its_hours_on_the_days_it_has_hours(int day, int hour, int minute, bool open) =>
        Fixtures.Location().IsOpenAt(At(day, hour, minute)).ShouldBe(open);

    [Fact]
    public void Seconds_inside_the_closing_minute_do_not_close_the_desk_early()
    {
        // Desks work in whole minutes, so 18:00:45 is still the 18:00 minute.
        Fixtures.Location().IsOpenAt(At(7, 18, 0, 45)).ShouldBeTrue();
    }

    [Fact]
    public void Opening_hours_are_read_on_the_wall_clock_of_the_offset_the_client_sent()
    {
        // 07:30 UTC is 08:30 in Lisbon summer time offset +01:00 and the desk has opened; at +00:00 it has not.
        var local = new DateTimeOffset(2026, 12, 7, 7, 30, 0, TimeSpan.Zero);

        Fixtures.Location().IsOpenAt(local).ShouldBeFalse();
        Fixtures.Location().IsOpenAt(local.ToOffset(TimeSpan.FromHours(1))).ShouldBeTrue();
    }

    [Fact]
    public void A_location_around_the_clock_says_midnight_to_one_minute_to()
    {
        RentalLocation location = Fixtures.Location([new OpeningWindow(DayOfWeek.Monday, new TimeOnly(0, 0), new TimeOnly(23, 59))]);

        location.IsOpenAt(At(7, 0, 0)).ShouldBeTrue();
        location.IsOpenAt(At(7, 23, 59, 30)).ShouldBeTrue();
    }

    [Fact]
    public void A_location_without_opening_hours_cannot_be_published()
    {
        RentalLocation location = Fixtures.Location([]);

        ApiErrorException error = Should.Throw<ApiErrorException>(() => location.Publish(Fixtures.Now));

        error.Error.Code.ShouldBe(CarErrorCode.NotPublishable);
        location.Status.ShouldBe(ListingStatus.Draft);
    }

    [Fact]
    public void Publishing_and_unpublishing_report_whether_anything_changed()
    {
        RentalLocation location = Fixtures.Location();

        location.Unpublish(Fixtures.Now).ShouldBeFalse();      // a draft is not live
        location.Publish(Fixtures.Now).ShouldBeTrue();
        location.Publish(Fixtures.Now).ShouldBeFalse();        // repeating is safe and announces nothing
        location.Unpublish(Fixtures.Now).ShouldBeTrue();
        location.Status.ShouldBe(ListingStatus.Unpublished);
        location.Publish(Fixtures.Now).ShouldBeTrue();         // and it can go live again
    }

    [Fact]
    public void The_listing_version_only_goes_up()
    {
        RentalLocation location = Fixtures.Location();

        long first = location.NextListingVersion();
        long second = location.NextListingVersion();

        second.ShouldBeGreaterThan(first);
    }

    [Theory]
    [InlineData("LIS", true)]
    [InlineData("lis", true)]
    [InlineData("LISB", false)]
    [InlineData("L1S", false)]
    [InlineData("", false)]
    public void An_airport_code_is_three_letters(string code, bool valid) =>
        RentalLocation.IsIataCode(code).ShouldBe(valid);

    [Theory]
    [InlineData("ECMN", true)]
    [InlineData("ecmn", true)]
    [InlineData("ECM", false)]
    [InlineData("ECMNX", false)]
    [InlineData("EC1N", false)]
    public void An_acriss_code_is_four_letters(string code, bool valid) =>
        CarCategory.IsAcriss(code).ShouldBe(valid);

    [Fact]
    public void An_acriss_code_is_stored_in_capitals()
    {
        Fixtures.Category(acriss: "ecmn").Acriss.ShouldBe("ECMN");
    }

    [Fact]
    public void Two_windows_on_one_weekday_are_refused()
    {
        OpeningWindow[] twice =
        [
            new(DayOfWeek.Monday, new TimeOnly(8, 0), new TimeOnly(12, 0)),
            new(DayOfWeek.Monday, new TimeOnly(14, 0), new TimeOnly(18, 0)),
        ];

        Should.Throw<ArgumentException>(() => Fixtures.Location(twice));
    }
}
