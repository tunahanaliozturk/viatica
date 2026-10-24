using Viatica.Kernel;

namespace Viatica.Cars.Domain;

/// <summary>
/// The Cars error codes. The first eight are the ones <c>docs/design.md</c> promises; the rest name rules the
/// service enforces on top of them. Every code is part of the API contract (<c>docs/api/errors.md</c>).
/// </summary>
public static class CarErrorCode
{
    public const string LocationNotFound = nameof(LocationNotFound);
    public const string CategoryNotFound = nameof(CategoryNotFound);
    public const string OneWayNotAllowed = nameof(OneWayNotAllowed);
    public const string DriverTooYoung = nameof(DriverTooYoung);
    public const string LocationClosed = nameof(LocationClosed);
    public const string SoldOut = nameof(SoldOut);
    public const string QuoteExpired = nameof(QuoteExpired);
    public const string QuoteInvalid = nameof(QuoteInvalid);

    public const string DropOffBeforePickUp = nameof(DropOffBeforePickUp);
    public const string RentalTooLong = nameof(RentalTooLong);
    public const string PickUpInThePast = nameof(PickUpInThePast);
    public const string NotPublishable = nameof(NotPublishable);
    public const string AllotmentBelowCommitments = nameof(AllotmentBelowCommitments);
}

/// <summary>The exceptions the domain and the use cases throw for refusals a client can act on.</summary>
public static class CarErrors
{
    public static ApiErrorException LocationNotFound() =>
        ApiErrorException.Domain(ErrorCategory.NotFound, CarErrorCode.LocationNotFound, "No such rental location.");

    public static ApiErrorException CategoryNotFound() =>
        ApiErrorException.Domain(ErrorCategory.NotFound, CarErrorCode.CategoryNotFound, "No such car category.");

    public static ApiErrorException OneWayNotAllowed() =>
        ApiErrorException.Domain(
            ErrorCategory.InvalidRequest, CarErrorCode.OneWayNotAllowed,
            "A car can only be dropped off at another location of the partner it was rented from.", "dropOffLocationId");

    public static ApiErrorException DriverTooYoung(int minimumAge) =>
        ApiErrorException.Domain(
            ErrorCategory.InvalidRequest, CarErrorCode.DriverTooYoung,
            $"This category needs a driver of at least {minimumAge}.", "driverAge");

    public static ApiErrorException LocationClosed(string target) =>
        ApiErrorException.Domain(
            ErrorCategory.InvalidRequest, CarErrorCode.LocationClosed,
            "The location is closed at that time.", target);

    public static ApiErrorException SoldOut() =>
        ApiErrorException.Conflict(CarErrorCode.SoldOut, "No car of this category is left on every day of the rental.");

    public static ApiErrorException DropOffBeforePickUp() =>
        ApiErrorException.Domain(
            ErrorCategory.InvalidRequest, CarErrorCode.DropOffBeforePickUp,
            "The drop-off must be after the pick-up.", "dropOffAt");

    public static ApiErrorException RentalTooLong() =>
        ApiErrorException.Domain(
            ErrorCategory.InvalidRequest, CarErrorCode.RentalTooLong,
            $"A rental can be at most {RentalPeriod.MaxDays} days.", "dropOffAt");

    public static ApiErrorException PickUpInThePast() =>
        ApiErrorException.Domain(
            ErrorCategory.InvalidRequest, CarErrorCode.PickUpInThePast,
            "The pick-up is in the past.", "pickUpAt");

    public static ApiErrorException NotPublishable(string reason) =>
        ApiErrorException.Conflict(CarErrorCode.NotPublishable, reason);

    public static ApiErrorException AllotmentBelowCommitments(DateOnly date) =>
        ApiErrorException.Conflict(
            CarErrorCode.AllotmentBelowCommitments,
            $"More cars are already sold or held on {date:yyyy-MM-dd} than the new allotment allows.");
}
