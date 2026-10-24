using Viatica.Kernel;

namespace Viatica.Stays.Domain;

/// <summary>
/// The Stays error codes. Codes are contract: clients switch on them, and <c>docs/api/errors.md</c> lists every
/// one with its status. The first nine come from <c>docs/design.md</c>; the rest are the cases a catalogue and a
/// rate calendar need that the design did not name.
/// </summary>
public static class StayErrorCode
{
    public const string PropertyNotFound = nameof(PropertyNotFound);
    public const string RoomTypeNotFound = nameof(RoomTypeNotFound);
    public const string PropertyNotPublished = nameof(PropertyNotPublished);
    public const string StayTooLong = nameof(StayTooLong);
    public const string CheckOutBeforeCheckIn = nameof(CheckOutBeforeCheckIn);
    public const string OccupancyExceeded = nameof(OccupancyExceeded);
    public const string SoldOut = nameof(SoldOut);
    public const string QuoteExpired = nameof(QuoteExpired);
    public const string QuoteInvalid = nameof(QuoteInvalid);

    public const string NoRoomTypes = nameof(NoRoomTypes);
    public const string CheckInInPast = nameof(CheckInInPast);
    public const string InvalidDateRange = nameof(InvalidDateRange);
    public const string OutsideRateWindow = nameof(OutsideRateWindow);
    public const string AllotmentBelowBooked = nameof(AllotmentBelowBooked);
    public const string AlreadyExists = nameof(AlreadyExists);
}

/// <summary>Builders for the errors that end a request, so a code and its category are written once.</summary>
public static class StayErrors
{
    // Not found is the answer for a resource another partner owns, too: a 403 would confirm that the id exists.
    public static ApiErrorException PropertyNotFound() =>
        ApiErrorException.Domain(ErrorCategory.NotFound, StayErrorCode.PropertyNotFound, "The property was not found.");

    public static ApiErrorException RoomTypeNotFound() =>
        ApiErrorException.Domain(ErrorCategory.NotFound, StayErrorCode.RoomTypeNotFound, "The room type was not found.");

    public static ApiErrorException PropertyNotPublished() =>
        ApiErrorException.Conflict(StayErrorCode.PropertyNotPublished, "The property is not published.");

    public static ApiErrorException NoRoomTypes() =>
        ApiErrorException.Conflict(StayErrorCode.NoRoomTypes, "Add at least one room type before publishing the property.");

    public static ApiErrorException SoldOut() =>
        ApiErrorException.Conflict(StayErrorCode.SoldOut, "Not enough rooms are left for every night of the stay.");

    public static ApiErrorException AllotmentBelowBooked() =>
        ApiErrorException.Conflict(
            StayErrorCode.AllotmentBelowBooked,
            "The allotment is lower than the rooms already sold or held on at least one night of the range.");

    public static ApiErrorException AlreadyExists(string what) =>
        ApiErrorException.Conflict(
            StayErrorCode.AlreadyExists,
            $"The {what} already exists with different values. Change it with PATCH and If-Match.");

    public static ApiErrorException OccupancyExceeded(int guests, int capacity) =>
        ApiErrorException.Invalid(
            StayErrorCode.OccupancyExceeded,
            $"{guests} guests do not fit: the rooms sleep {capacity} in all.",
            "guests");
}
