using System.Security.Claims;
using Relevox;
using Viatica.Kernel.Web;
using Viatica.Stays.Application.Features.RoomTypes;
using Viatica.Stays.Domain;

namespace Viatica.Stays.Api.Endpoints;

internal static class RoomTypeEndpoints
{
    public static void MapRoomTypeEndpoints(this IEndpointRouteBuilder app)
    {
        app.MapGet("/properties/{propertyId:guid}/room-types/{roomTypeId:guid}", async (Guid propertyId, Guid roomTypeId, HttpContext http, ClaimsPrincipal user, ISender sender, CancellationToken cancellationToken) =>
            http.Respond(await sender.Send(new GetRoomType(propertyId, roomTypeId, user.ToCaller()), cancellationToken)))
            .AllowAnonymous();

        app.MapPut("/properties/{propertyId:guid}/room-types/{roomTypeId:guid}", async (Guid propertyId, Guid roomTypeId, RoomTypeDetails body, HttpContext http, ClaimsPrincipal user, ISender sender, CancellationToken cancellationToken) =>
            http.Respond(
                await sender.Send(new PutRoomType(propertyId, roomTypeId, user.ToCaller(), body), cancellationToken),
                $"/properties/{propertyId}/room-types/{roomTypeId}"))
            .RequireAuthorization(Policies.Partner);

        app.MapPatch("/properties/{propertyId:guid}/room-types/{roomTypeId:guid}", async (Guid propertyId, Guid roomTypeId, RoomTypePatch body, HttpContext http, ClaimsPrincipal user, ISender sender, CancellationToken cancellationToken) =>
            http.Respond(await sender.Send(new UpdateRoomType(propertyId, roomTypeId, user.ToCaller(), body, http.IfMatchVersion()), cancellationToken)))
            .RequireAuthorization(Policies.Partner)
            .RequireIfMatch();
    }
}
