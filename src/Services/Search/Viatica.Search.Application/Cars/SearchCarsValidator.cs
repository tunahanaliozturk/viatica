using Moongazing.OrionGuard.DependencyInjection;

namespace Viatica.Search.Application.Cars;

public sealed class SearchCarsValidator : AbstractValidator<SearchCars>
{
    public SearchCarsValidator(TimeProvider clock)
    {
        RuleFor(static r => r.PickUpLocationId is not null || r.Origin is not null,
                "Give a pickUpLocationId, or lat and lon.", "pickUpLocationId")
            .WithErrorCode("LocationRequired");
        RuleFor(static r => r.PickUpLocationId is null || r.Origin is null,
                "Give a pickUpLocationId or a point, not both.", "pickUpLocationId")
            .WithErrorCode("AmbiguousLocation");
        RuleFor(static r => r.Origin is null || (r.Origin.Lat is >= -90 and <= 90 && r.Origin.Lon is >= -180 and <= 180),
                "lat must be within -90 and 90, lon within -180 and 180.", "lat")
            .WithErrorCode("InvalidPoint");
        RuleFor(static r => r.RadiusKm is null || (r.RadiusKm > 0 && r.Origin is not null),
                "radiusKm must be above zero and goes with a point.", "radiusKm")
            .WithErrorCode("InvalidRadius");

        RuleFor(r => r.PickUpAt >= clock.GetUtcNow().AddHours(-24), "pickUpAt is in the past.", "pickUpAt")
            .WithErrorCode("PickUpInPast");
        RuleFor(static r => r.DropOffAt > r.PickUpAt, "dropOffAt must be after pickUpAt.", "dropOffAt")
            .WithErrorCode("DropOffBeforePickUp");
        RuleFor(static r => r.Days <= SearchCars.MaxRentalDays, $"A rental can be at most {SearchCars.MaxRentalDays} days.", "dropOffAt")
            .WithErrorCode("RentalTooLong");

        RuleFor(static r => r.MinSeats is null or (>= 1 and <= 20), "minSeats must be between 1 and 20.", "minSeats")
            .WithErrorCode("InvalidSeats");
        RuleFor(static r => r.Category is null || (r.Category.Length is >= 1 and <= 4 && r.Category.All(char.IsAsciiLetter)),
                "category is an ACRISS code or its first letters, up to four letters.", "category")
            .WithErrorCode("InvalidCategory");
        RuleFor(static r => r.MinPrice is null or >= 0 && r.MaxPrice is null or >= 0, "Prices cannot be negative.", "minPrice")
            .WithErrorCode("InvalidPriceRange");
        RuleFor(static r => r.MinPrice is not { } min || r.MaxPrice is not { } max || min <= max,
                "minPrice cannot be above maxPrice.", "minPrice")
            .WithErrorCode("InvalidPriceRange");
    }
}
