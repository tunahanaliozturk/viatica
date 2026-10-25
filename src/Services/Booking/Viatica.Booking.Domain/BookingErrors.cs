using Viatica.Kernel;

namespace Viatica.Booking.Domain;

/// <summary>
/// The Booking error codes. Codes are contract (docs/api/errors.md lists each with its status). The first five
/// come from docs/design.md; the rest are cases a trip builder and a checkout need that the design did not name.
/// </summary>
public static class BookingErrorCode
{
    public const string TripNotFound = nameof(TripNotFound);
    public const string TripNotEditable = nameof(TripNotEditable);
    public const string ItemNotFound = nameof(ItemNotFound);
    public const string ItemNotCancellable = nameof(ItemNotCancellable);
    public const string CheckoutInProgress = nameof(CheckoutInProgress);

    public const string TripEmpty = nameof(TripEmpty);
    public const string TripFull = nameof(TripFull);
    public const string CheckoutConflict = nameof(CheckoutConflict);
    public const string ItemConflict = nameof(ItemConflict);
    public const string QuoteExpired = nameof(QuoteExpired);
    public const string QuoteInvalid = nameof(QuoteInvalid);
    public const string TripAlreadyExists = nameof(TripAlreadyExists);
}

/// <summary>Builders for the errors that end a request, so a code and its category are written once.</summary>
public static class BookingErrors
{
    // Not found is also the answer for a trip that belongs to someone else: a 403 would confirm that the id exists.
    public static ApiErrorException TripNotFound() =>
        ApiErrorException.Domain(ErrorCategory.NotFound, BookingErrorCode.TripNotFound, "The trip was not found.");

    public static ApiErrorException ItemNotFound() =>
        ApiErrorException.Domain(ErrorCategory.NotFound, BookingErrorCode.ItemNotFound, "The item was not found on this trip.");

    public static ApiErrorException TripNotEditable(TripStatus status) =>
        ApiErrorException.Conflict(BookingErrorCode.TripNotEditable, $"A trip can only be changed while it is a draft; this one is {status}.");

    public static ApiErrorException TripEmpty() =>
        ApiErrorException.Conflict(BookingErrorCode.TripEmpty, "Add at least one item before checking out.");

    public static ApiErrorException TripFull(int limit) =>
        ApiErrorException.Conflict(BookingErrorCode.TripFull, $"A trip holds at most {limit} items.");

    public static ApiErrorException CheckoutConflict() =>
        ApiErrorException.Conflict(
            BookingErrorCode.CheckoutConflict,
            "This trip was already checked out with different details. A repeated checkout must send exactly the same body.");

    public static ApiErrorException ItemConflict() =>
        ApiErrorException.Conflict(BookingErrorCode.ItemConflict, "An item with this id exists on the trip with different contents.");

    public static ApiErrorException QuoteExpired(Guid itemId) =>
        ApiErrorException.Domain(
            ErrorCategory.Conflict, BookingErrorCode.QuoteExpired, "The quote on this item has expired. Quote again and replace the item.", $"items/{itemId:D}");

    public static ApiErrorException CheckoutInProgress() =>
        ApiErrorException.Conflict(BookingErrorCode.CheckoutInProgress, "The trip is still being checked out. Try again when it has finished.");

    public static ApiErrorException ItemNotCancellable(string reason) =>
        ApiErrorException.Conflict(BookingErrorCode.ItemNotCancellable, reason);
}
