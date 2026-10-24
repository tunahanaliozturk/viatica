using Viatica.AirlineSim.Api;
using Viatica.AirlineSim.Orders;

namespace Viatica.AirlineSim;

/// <summary>
/// An error in the airline's own style: an <c>Errors</c> array of code and text, like NDC, rather than our problem
/// details. The Flights adapter has to translate it, which is exactly the work a real supplier would give it.
/// </summary>
internal sealed record SupplierError(int Status, string Code, string Text)
{
    public static readonly SupplierError UnknownAirline =
        new(StatusCodes.Status404NotFound, "UNKNOWN_AIRLINE", "No airline with that code is served here.");

    public static readonly SupplierError ServiceUnavailable =
        new(StatusCodes.Status503ServiceUnavailable, "SERVICE_UNAVAILABLE", "The airline system is not available.");

    public static readonly SupplierError UpstreamTimeout =
        new(StatusCodes.Status504GatewayTimeout, "UPSTREAM_TIMEOUT", "The reservation system did not answer in time.");

    public static readonly SupplierError OfferNotFound =
        new(StatusCodes.Status404NotFound, "OFFER_NOT_FOUND", "The offer id is not one this airline issued.");

    public static readonly SupplierError OfferExpired =
        new(StatusCodes.Status410Gone, "OFFER_EXPIRED", "The offer has expired. Search again.");

    public static readonly SupplierError NoAvailability =
        new(StatusCodes.Status409Conflict, "NO_AVAILABILITY", "Not enough seats left in the requested cabin.");

    public static readonly SupplierError OrderNotFound =
        new(StatusCodes.Status404NotFound, "ORDER_NOT_FOUND", "No order matches.");

    public static readonly SupplierError IdempotencyKeyReused =
        new(StatusCodes.Status409Conflict, "IDEMPOTENCY_KEY_REUSED", "That idempotency key was used for a different offer.");

    public static readonly SupplierError TicketingRefused =
        new(StatusCodes.Status422UnprocessableEntity, "TICKETING_REFUSED", "The airline declined to issue tickets for this order.");

    public static readonly SupplierError TicketingTimeLimitExpired =
        new(StatusCodes.Status409Conflict, "TICKETING_TIME_LIMIT_EXPIRED", "The ticketing time limit has passed.");

    public static readonly SupplierError VoidWindowClosed =
        new(StatusCodes.Status409Conflict, "VOID_WINDOW_CLOSED", "The void window has closed. Cancel the order instead.");

    public static readonly SupplierError OrderNotTicketed =
        new(StatusCodes.Status409Conflict, "ORDER_NOT_TICKETED", "The order has no tickets to cancel. Void it instead.");

    public static SupplierError Invalid(string text) => new(StatusCodes.Status400BadRequest, "INVALID_REQUEST", text);

    public static SupplierError OrderClosed(OrderStatus status) =>
        new(StatusCodes.Status409Conflict, "ORDER_CLOSED", $"The order is already {status.ToString().ToUpperInvariant()}.");

    public IResult ToResult() =>
        TypedResults.Json(new ErrorResponse([new ErrorItem(Code, Text)]), SimJson.Default.ErrorResponse, statusCode: Status);
}
