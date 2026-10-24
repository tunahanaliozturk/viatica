using Moongazing.OrionGuard.DependencyInjection;

namespace Viatica.Search.Application.Stays;

/// <summary>
/// The rules a stay search can break before it reaches the index. Codes follow the Stays service's error list
/// (<c>StayTooLong</c>, <c>CheckOutBeforeCheckIn</c>), so a client handles both services the same way.
/// </summary>
public sealed class SearchStaysValidator : AbstractValidator<SearchStays>
{
    public const int MaxDestinationLength = 100;
    public const int MaxGuests = 40;
    public const int MaxAmenities = 20;

    public SearchStaysValidator(TimeProvider clock)
    {
        // One day of grace for the properties that sit ahead of UTC: their "today" is already our tomorrow.
        DateOnly Today() => DateOnly.FromDateTime(clock.GetUtcNow().UtcDateTime).AddDays(-1);

        RuleFor(static r => !string.IsNullOrWhiteSpace(r.Destination) || r.Box is not null,
                "Give a destination or a bounding box.", "destination")
            .WithErrorCode("LocationRequired");
        RuleFor(static r => r.Destination is null || r.Destination.Length <= MaxDestinationLength,
                $"destination can be at most {MaxDestinationLength} characters.", "destination")
            .WithErrorCode("DestinationTooLong");
        RuleFor(static r => r.Box is null || IsValid(r.Box),
                "bbox must be west,south,east,north in degrees, with south below north.", "bbox")
            .WithErrorCode("InvalidBoundingBox");
        RuleFor(static r => r.Origin is null || (r.Origin.Lat is >= -90 and <= 90 && r.Origin.Lon is >= -180 and <= 180),
                "lat must be within -90 and 90, lon within -180 and 180.", "lat")
            .WithErrorCode("InvalidPoint");

        RuleFor(r => r.CheckIn >= Today(), "checkIn is in the past.", "checkIn")
            .WithErrorCode("CheckInInPast");
        RuleFor(static r => r.CheckOut > r.CheckIn, "checkOut must be after checkIn.", "checkOut")
            .WithErrorCode("CheckOutBeforeCheckIn");
        RuleFor(static r => r.Nights <= SearchStays.MaxNights, $"A stay can be at most {SearchStays.MaxNights} nights.", "checkOut")
            .WithErrorCode("StayTooLong");
        RuleFor(static r => r.Guests is >= 1 and <= MaxGuests, $"guests must be between 1 and {MaxGuests}.", "guests")
            .WithErrorCode("GuestsOutOfRange");
        RuleFor(static r => r.Rooms is >= 1 and <= SearchStays.MaxRooms, $"rooms must be between 1 and {SearchStays.MaxRooms}.", "rooms")
            .WithErrorCode("RoomsOutOfRange");

        RuleFor(static r => r.MinPrice is null or >= 0 && r.MaxPrice is null or >= 0, "Prices cannot be negative.", "minPrice")
            .WithErrorCode("InvalidPriceRange");
        RuleFor(static r => r.MinPrice is not { } min || r.MaxPrice is not { } max || min <= max,
                "minPrice cannot be above maxPrice.", "minPrice")
            .WithErrorCode("InvalidPriceRange");
        RuleFor(static r => r.Stars.All(static s => s is >= 1 and <= 5), "stars are whole numbers from 1 to 5.", "stars")
            .WithErrorCode("InvalidStars");
        RuleFor(static r => r.Amenities.Count <= MaxAmenities && r.Amenities.All(static a => a.Length <= 50),
                $"Give at most {MaxAmenities} amenities of 50 characters each.", "amenities")
            .WithErrorCode("InvalidAmenities");
        RuleFor(static r => r.Sort != StaySort.Distance || r.Origin is not null || r.Box is not null,
                "Sorting by distance needs a point: give lat and lon, or a bbox.", "sort")
            .WithErrorCode("DistanceNeedsPoint");
    }

    private static bool IsValid(BoundingBox box) =>
        box.South is >= -90 and <= 90 && box.North is >= -90 and <= 90 && box.South < box.North
        && box.West is >= -180 and <= 180 && box.East is >= -180 and <= 180;
}
