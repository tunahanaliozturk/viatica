using Relevox;
using Viatica.Booking.Application;
using Viatica.Booking.Application.Features.Cancellation;
using Viatica.Booking.Application.Features.Partner;
using Viatica.Kernel;
using Viatica.Kernel.Web;

namespace Viatica.Booking.Api.Endpoints;

internal static class CancellationEndpoints
{
    public static void MapCancellationEndpoints(this IEndpointRouteBuilder app) =>
        app.MapPost("/trips/{tripId:guid}/items/{itemId:guid}/cancel", async (Guid tripId, Guid itemId, HttpContext http, ISender sender, CancellationToken cancellationToken) =>
            {
                Operation operation = await sender.Send(new CancelItem(tripId, itemId, http.ToCaller()), cancellationToken);
                return http.Accepted(
                    operation.ResourceLocation is { } path ? operation with { ResourceLocation = Operations.OperationLocation(http.Request, path) } : operation,
                    $"/operations/{itemId}");
            })
            .RequireAuthorization(Policies.Traveller)
            .WithTags("Cancellation")
            .WithSummary("Cancel one confirmed item; its own policy decides the refund (BKG-4)")
            .WithDescription("Answers 202 with an Operation-Location. The owner confirms, then the refund is one payment operation. Asking again changes nothing.");
}

internal static class PartnerEndpoints
{
    public static void MapPartnerEndpoints(this IEndpointRouteBuilder app) =>
        app.MapGet("/partner/bookings", async (HttpContext http, System.Security.Claims.ClaimsPrincipal user, ISender sender, CancellationToken cancellationToken) =>
                http.Page(await sender.Send(
                    new ListPartnerBookings(http.BindCollectionQuery(PartnerBookingQueries.Fields), user.PartnerId()!.Value), cancellationToken)))
            .RequireAuthorization(Policies.Partner)
            .WithTags("Partner")
            .WithSummary("Bookings against the calling partner's own inventory (BKG-5)")
            .WithDescription("Confirmed and cancelled stays and cars. Another partner's bookings are not in the list, and the traveller is not named.");
}
