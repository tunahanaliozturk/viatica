using Microsoft.Extensions.Time.Testing;
using Moongazing.OrionGuard.Core;
using Viatica.Contracts;
using Viatica.Kernel;
using Viatica.Search.Application.Cars;
using Viatica.Search.Application.Places;
using Viatica.Search.Application.Projection;
using Viatica.Search.Application.Stays;

namespace Viatica.Search.UnitTests;

public sealed class RequestTests
{
    private readonly FakeTimeProvider _clock = new(new DateTimeOffset(Events.Today.ToDateTime(TimeOnly.MinValue), TimeSpan.Zero));

    private static SearchStays Stay(Func<SearchStays, SearchStays>? change = null)
    {
        var request = new SearchStays(
            "lisbon", null, null, Events.Today.AddDays(10), Events.Today.AddDays(13), 2, 1,
            null, null, [], [], StaySort.Price, 20, null);
        return change?.Invoke(request) ?? request;
    }

    private async Task<string[]> CodesAsync(SearchStays request)
    {
        GuardResult result = await new SearchStaysValidator(_clock).ValidateAsync(request, CancellationToken.None);
        return [.. result.Errors.Select(static e => e.ErrorCode ?? "")];
    }

    [Fact]
    public async Task A_plain_search_is_valid()
    {
        (await CodesAsync(Stay())).ShouldBeEmpty();
    }

    [Fact]
    public async Task A_stay_is_at_most_thirty_nights()
    {
        (await CodesAsync(Stay(static r => r with { CheckOut = r.CheckIn.AddDays(30) }))).ShouldBeEmpty();
        (await CodesAsync(Stay(static r => r with { CheckOut = r.CheckIn.AddDays(31) }))).ShouldBe(["StayTooLong"]);
    }

    [Fact]
    public async Task Check_out_must_follow_check_in_and_check_in_cannot_be_in_the_past()
    {
        (await CodesAsync(Stay(static r => r with { CheckOut = r.CheckIn }))).ShouldContain("CheckOutBeforeCheckIn");
        (await CodesAsync(Stay(r => r with { CheckIn = Events.Today.AddDays(-3), CheckOut = Events.Today.AddDays(-1) }))).ShouldContain("CheckInInPast");
    }

    [Fact]
    public async Task Guests_rooms_prices_stars_and_sorting_are_checked()
    {
        (await CodesAsync(Stay(static r => r with { Guests = 0 }))).ShouldBe(["GuestsOutOfRange"]);
        (await CodesAsync(Stay(static r => r with { Rooms = 10 }))).ShouldBe(["RoomsOutOfRange"]);
        (await CodesAsync(Stay(static r => r with { MinPrice = 200, MaxPrice = 100 }))).ShouldBe(["InvalidPriceRange"]);
        (await CodesAsync(Stay(static r => r with { Stars = [0, 6] }))).ShouldBe(["InvalidStars"]);
        (await CodesAsync(Stay(static r => r with { Sort = StaySort.Distance }))).ShouldBe(["DistanceNeedsPoint"]);
        (await CodesAsync(Stay(static r => r with { Sort = StaySort.Distance, Origin = new GeoLocation(38.7, -9.1) }))).ShouldBeEmpty();
    }

    [Fact]
    public async Task A_destination_or_a_box_is_required()
    {
        (await CodesAsync(Stay(static r => r with { Destination = null }))).ShouldBe(["LocationRequired"]);
        (await CodesAsync(Stay(static r => r with { Destination = null, Box = new BoundingBox(-10, 38, -9, 39) }))).ShouldBeEmpty();
        (await CodesAsync(Stay(static r => r with { Destination = null, Box = new BoundingBox(-10, 39, -9, 38) }))).ShouldBe(["InvalidBoundingBox"]);
    }

    [Fact]
    public void Guests_are_spread_over_the_rooms_and_prices_become_cents()
    {
        StaySearchCriteria criteria = StaySearchCriteria.From(Stay(static r => r with
        {
            Guests = 5,
            Rooms = 2,
            MinPrice = 80.5m,
            MaxPrice = 200m,
            Stars = [5, 4, 4],
            Amenities = [" Pool", "WIFI", "pool"],
        }));

        criteria.GuestsPerRoom.ShouldBe(3);
        criteria.MinNightlyCents.ShouldBe(8050);
        criteria.MaxNightlyCents.ShouldBe(20000);
        criteria.Stars.ShouldBe([4, 5]);
        criteria.Amenities.ShouldBe(["pool", "wifi"]);
        criteria.Nights.ShouldBe(3);
    }

    [Fact]
    public void The_distance_origin_is_the_box_centre_unless_a_point_is_given()
    {
        var box = new BoundingBox(-10, 38, -8, 40);

        StaySearchCriteria fromBox = StaySearchCriteria.From(Stay(r => r with { Box = box }));
        StaySearchCriteria fromPoint = StaySearchCriteria.From(Stay(r => r with { Box = box, Origin = new GeoLocation(1, 2) }));

        fromBox.DistanceOrigin.ShouldBe(new GeoLocation(39, -9));
        fromPoint.DistanceOrigin.ShouldBe(new GeoLocation(1, 2));
        new BoundingBox(170, -10, -170, 10).Center.Lon.ShouldBe(180);
    }

    [Fact]
    public void Prices_shown_are_the_average_night_and_the_total_for_all_rooms()
    {
        // Three nights at 100.00, 100.00 and 100.01 for one room, two rooms wanted.
        (Money perNight, Money total) = StayPricing.Offer(30001, nights: 3, rooms: 2);

        perNight.ToString().ShouldBe("100.00 EUR");
        total.ToString().ShouldBe("600.02 EUR");
    }

    [Fact]
    public void A_car_rental_is_counted_in_started_days_from_the_pick_up_date()
    {
        var pickUp = new DateTimeOffset(2026, 12, 1, 10, 0, 0, TimeSpan.FromHours(1));
        var request = new SearchCars(Guid.NewGuid(), null, null, null, pickUp, pickUp.AddHours(24), null, null, null, null, null, 20, null);

        request.Days.ShouldBe(1);
        (request with { DropOffAt = pickUp.AddHours(24.5) }).Days.ShouldBe(2);
        request.FirstDay.ShouldBe(new DateOnly(2026, 12, 1).DayNumber);
        (request with { PickUpAt = new DateTimeOffset(2026, 12, 1, 23, 30, 0, TimeSpan.FromHours(-5)) }).FirstDay.ShouldBe(new DateOnly(2026, 12, 1).DayNumber);
    }

    [Fact]
    public async Task A_car_search_needs_a_place_and_a_sensible_period()
    {
        var pickUp = new DateTimeOffset(Events.Today.AddDays(10).ToDateTime(new TimeOnly(10, 0)), TimeSpan.Zero);
        var valid = new SearchCars(Guid.NewGuid(), null, null, null, pickUp, pickUp.AddDays(3), null, null, null, null, null, 20, null);
        var validator = new SearchCarsValidator(_clock);

        (await validator.ValidateAsync(valid, CancellationToken.None)).IsInvalid.ShouldBeFalse();
        (await Codes(validator, valid with { PickUpLocationId = null })).ShouldBe(["LocationRequired"]);
        (await Codes(validator, valid with { Origin = new GeoLocation(1, 1) })).ShouldBe(["AmbiguousLocation"]);
        (await Codes(validator, valid with { DropOffAt = pickUp })).ShouldBe(["DropOffBeforePickUp"]);
        (await Codes(validator, valid with { DropOffAt = pickUp.AddDays(31) })).ShouldBe(["RentalTooLong"]);
        (await Codes(validator, valid with { Category = "E1" })).ShouldBe(["InvalidCategory"]);
    }

    [Fact]
    public async Task Autocomplete_needs_two_characters_and_a_kind()
    {
        var validator = new AutocompleteValidator();

        (await Codes(validator, new Autocomplete("l", new HashSet<PlaceKind> { PlaceKind.City }, 8))).ShouldBe(["QueryTooShort"]);
        (await Codes(validator, new Autocomplete("lis", new HashSet<PlaceKind>(), 8))).ShouldBe(["InvalidTypes"]);
        (await Codes(validator, new Autocomplete("lis", new HashSet<PlaceKind> { PlaceKind.City }, 8))).ShouldBeEmpty();
    }

    private static async Task<string[]> Codes<T>(Moongazing.OrionGuard.DependencyInjection.IValidator<T> validator, T request)
    {
        GuardResult result = await validator.ValidateAsync(request, CancellationToken.None);
        return [.. result.Errors.Select(static e => e.ErrorCode ?? "")];
    }
}
