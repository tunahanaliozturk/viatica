using System.Security.Claims;
using Relevox;
using Viatica.Cars.Application.Features.GetInventory;
using Viatica.Cars.Application.Features.SetInventory;
using Viatica.Kernel;
using Viatica.Kernel.Web;

namespace Viatica.Cars.Api.Endpoints;

/// <summary>Every day from <c>From</c> to <c>To</c> inclusive gets this allotment and this rate.</summary>
public sealed record InventoryBody(DateOnly From, DateOnly To, int Allotment, Money Rate);

internal static class InventoryEndpoints
{
    public static void MapInventoryEndpoints(this IEndpointRouteBuilder app)
    {
        RouteGroupBuilder group = app
            .MapGroup("/cars/locations/{locationId:guid}/categories/{categoryId:guid}/inventory")
            .RequireAuthorization(Policies.Partner);

        // A range of days is edited as one request and one transaction. It has no id of its own to PUT, so a retry
        // is made safe by Repeatability-Request-ID, kept in Valkey.
        group.MapPost("/bulk", async (
            Guid locationId, Guid categoryId, InventoryBody body, ClaimsPrincipal user, ISender sender, CancellationToken ct) =>
            TypedResults.Ok(await sender.Send(
                new SetInventory(user.PartnerId()!.Value, locationId, categoryId, body.From, body.To, body.Allotment, body.Rate), ct)))
            .WithRepeatability();

        group.MapGet("/", async (
            Guid locationId, Guid categoryId, DateOnly from, DateOnly to, ClaimsPrincipal user, ISender sender, CancellationToken ct) =>
            TypedResults.Ok(await sender.Send(new GetInventory(user.PartnerId()!.Value, locationId, categoryId, from, to), ct)));
    }
}
