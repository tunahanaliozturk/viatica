using System.Security.Claims;
using Relevox;
using Viatica.Cars.Application;
using Viatica.Cars.Application.Features.GetCategory;
using Viatica.Cars.Application.Features.ListCategories;
using Viatica.Cars.Application.Features.PublishCategory;
using Viatica.Cars.Application.Features.PutCategory;
using Viatica.Cars.Domain;
using Viatica.Kernel.Querying;
using Viatica.Kernel.Web;

namespace Viatica.Cars.Api.Endpoints;

/// <summary>What a partner sends to create or replace a car category.</summary>
public sealed record CategoryBody(
    string Acriss,
    string Name,
    int Seats,
    Transmission Transmission,
    FuelType Fuel,
    int Bags,
    int MinimumDriverAge,
    Uri? ImageUrl = null);

internal static class CategoryEndpoints
{
    public static void MapCategoryEndpoints(this IEndpointRouteBuilder app)
    {
        RouteGroupBuilder group = app.MapGroup("/cars/categories").RequireAuthorization(Policies.Partner);

        group.MapGet("/", async (ClaimsPrincipal user, HttpContext http, ISender sender, CancellationToken ct) =>
        {
            CollectionQuery<CarCategory> query = http.BindCollectionQuery(ListCategories.Fields);
            return http.Page(await sender.Send(new ListCategories(user.PartnerId()!.Value, query), ct));
        });

        group.MapGet("/{id:guid}", async (Guid id, ClaimsPrincipal user, HttpContext http, ISender sender, CancellationToken ct) =>
            LocationEndpoints.Respond(http, await sender.Send(new GetCategory(user.PartnerId()!.Value, id), ct)));

        group.MapPut("/{id:guid}", async (Guid id, CategoryBody body, ClaimsPrincipal user, HttpContext http, ISender sender, CancellationToken ct) =>
        {
            Versioned<CategoryView> stored = await sender.Send(
                new PutCategory(
                    user.PartnerId()!.Value, id, http.RowVersion(), body.Acriss, body.Name, body.Seats, body.Transmission,
                    body.Fuel, body.Bags, body.MinimumDriverAge, body.ImageUrl),
                ct);

            return LocationEndpoints.Respond(http, stored, Operations.OperationLocation(http.Request, $"/cars/categories/{id}"));
        }).RequireIfMatch();

        group.MapPost("/{id:guid}/publish", async (Guid id, ClaimsPrincipal user, HttpContext http, ISender sender, CancellationToken ct) =>
            LocationEndpoints.Respond(http, await sender.Send(new PublishCategory(user.PartnerId()!.Value, id, Publish: true), ct)));

        group.MapPost("/{id:guid}/unpublish", async (Guid id, ClaimsPrincipal user, HttpContext http, ISender sender, CancellationToken ct) =>
            LocationEndpoints.Respond(http, await sender.Send(new PublishCategory(user.PartnerId()!.Value, id, Publish: false), ct)));
    }
}
