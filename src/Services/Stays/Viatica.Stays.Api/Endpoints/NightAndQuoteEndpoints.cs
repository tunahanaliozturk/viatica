using System.Security.Claims;
using Microsoft.AspNetCore.Mvc;
using Relevox;
using Viatica.Kernel.Web;
using Viatica.Stays.Application.Features.Nights;
using Viatica.Stays.Application.Features.Quotes;

namespace Viatica.Stays.Api.Endpoints;

internal static class NightAndQuoteEndpoints
{
    public static void MapNightAndQuoteEndpoints(this IEndpointRouteBuilder app)
    {
        // A calendar edit has no natural id, so it takes Repeatability-Request-ID: a retry gets the first answer and
        // the range is not written twice. "inventory:set" is a custom method on the collection, as the Microsoft REST
        // guidelines spell it.
        app.MapPost("/properties/{propertyId:guid}/room-types/{roomTypeId:guid}/inventory:set",
            async (Guid propertyId, Guid roomTypeId, SetNightsRequest body, ClaimsPrincipal user, ISender sender, CancellationToken cancellationToken) =>
                TypedResults.Ok(await sender.Send(
                    new SetNights(propertyId, roomTypeId, user.ToCaller(), body.From, body.To, body.Allotment, body.Rate), cancellationToken)))
            .RequireAuthorization(Policies.Partner)
            .WithRepeatability();

        app.MapGet("/properties/{propertyId:guid}/room-types/{roomTypeId:guid}/inventory",
            async (Guid propertyId, Guid roomTypeId, [FromQuery] DateOnly from, [FromQuery] DateOnly to, ClaimsPrincipal user, ISender sender, CancellationToken cancellationToken) =>
                TypedResults.Ok(await sender.Send(new GetNights(propertyId, roomTypeId, user.ToCaller(), from, to), cancellationToken)))
            .RequireAuthorization(Policies.Partner);

        // Anyone may ask what a stay costs. The token that comes back is what Booking later hands to Stays to get a hold.
        app.MapPost("/properties/{propertyId:guid}/room-types/{roomTypeId:guid}/quotes",
            async (Guid propertyId, Guid roomTypeId, QuoteRequest body, ISender sender, CancellationToken cancellationToken) =>
                TypedResults.Ok(await sender.Send(
                    new CreateQuote(propertyId, roomTypeId, body.CheckIn, body.CheckOut, body.Quantity, body.Guests), cancellationToken)))
            .AllowAnonymous();
    }
}
