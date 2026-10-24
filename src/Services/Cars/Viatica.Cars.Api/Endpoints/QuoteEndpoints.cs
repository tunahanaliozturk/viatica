using Relevox;
using Viatica.Cars.Application.Features.GetQuote;

namespace Viatica.Cars.Api.Endpoints;

/// <summary>
/// A rental to price. <c>DropOffLocationId</c> is left out for a return to the pick-up location.
/// </summary>
public sealed record QuoteBody(
    Guid LocationId,
    Guid CategoryId,
    DateTimeOffset PickUpAt,
    DateTimeOffset DropOffAt,
    int DriverAge,
    Guid? DropOffLocationId = null);

internal static class QuoteEndpoints
{
    public static void MapQuoteEndpoints(this IEndpointRouteBuilder app) =>
        // Anonymous on purpose: a trip can be built before the traveller signs in (BKG-1). Abuse is the gateway's
        // rate limit, not this service's job.
        app.MapPost("/cars/quotes", async (QuoteBody body, ISender sender, CancellationToken ct) =>
            TypedResults.Ok(await sender.Send(
                new GetQuote(body.LocationId, body.CategoryId, body.DropOffLocationId, body.PickUpAt, body.DropOffAt, body.DriverAge), ct)))
            .AllowAnonymous();
}
