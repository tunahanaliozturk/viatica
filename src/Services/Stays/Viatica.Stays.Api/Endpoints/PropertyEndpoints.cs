using System.Security.Claims;
using Microsoft.AspNetCore.Http.HttpResults;
using Relevox;
using Viatica.Kernel.Querying;
using Viatica.Kernel.Web;
using Viatica.Stays.Application;
using Viatica.Stays.Application.Features.Properties;
using Viatica.Stays.Domain;

namespace Viatica.Stays.Api.Endpoints;

internal static class PropertyEndpoints
{
    public const string PartnerOrAdmin = "partner-or-admin";

    public static void MapPropertyEndpoints(this IEndpointRouteBuilder app)
    {
        // Public: anyone reads a published property. A draft answers 404 unless the token is its partner's or an admin's,
        // which is why these endpoints look at the token without demanding one.
        app.MapGet("/properties", async (HttpContext http, ISender sender, CancellationToken cancellationToken) =>
            http.Page(await sender.Send(new ListProperties(http.BindCollectionQuery(PropertyQueries.Fields), Caller.Anonymous, OwnOnly: false), cancellationToken)))
            .AllowAnonymous();

        app.MapGet("/partner/properties", async (HttpContext http, ClaimsPrincipal user, ISender sender, CancellationToken cancellationToken) =>
            http.Page(await sender.Send(new ListProperties(http.BindCollectionQuery(PropertyQueries.Fields), user.ToCaller(), OwnOnly: true), cancellationToken)))
            .RequireAuthorization(Policies.Partner);

        app.MapGet("/properties/{propertyId:guid}", async (Guid propertyId, HttpContext http, ClaimsPrincipal user, ISender sender, CancellationToken cancellationToken) =>
            http.Respond(await sender.Send(new GetProperty(propertyId, user.ToCaller()), cancellationToken)))
            .AllowAnonymous();

        // Creation is a PUT with an id the client chose, so a retry after a lost response finds the property it made.
        app.MapPut("/properties/{propertyId:guid}", async (Guid propertyId, PropertyDetails body, HttpContext http, ClaimsPrincipal user, ISender sender, CancellationToken cancellationToken) =>
            http.Respond(
                await sender.Send(new CreateProperty(propertyId, user.PartnerId()!.Value, body), cancellationToken),
                $"/properties/{propertyId}"))
            .RequireAuthorization(Policies.Partner);

        app.MapPatch("/properties/{propertyId:guid}", async (Guid propertyId, PropertyPatch body, HttpContext http, ClaimsPrincipal user, ISender sender, CancellationToken cancellationToken) =>
            http.Respond(await sender.Send(new UpdateProperty(propertyId, user.ToCaller(), body, http.IfMatchVersion()), cancellationToken)))
            .RequireAuthorization(Policies.Partner)
            .RequireIfMatch();

        app.MapPost("/properties/{propertyId:guid}/publish", async (Guid propertyId, HttpContext http, ClaimsPrincipal user, ISender sender, CancellationToken cancellationToken) =>
            http.Respond(await sender.Send(new PublishProperty(propertyId, user.ToCaller(), http.IfMatchVersion()), cancellationToken)))
            .RequireAuthorization(Policies.Partner)
            .RequireIfMatch();

        app.MapPost("/properties/{propertyId:guid}/unpublish", async (Guid propertyId, HttpContext http, ClaimsPrincipal user, ISender sender, CancellationToken cancellationToken) =>
            http.Respond(await sender.Send(new UnpublishProperty(propertyId, user.ToCaller(), http.IfMatchVersion()), cancellationToken)))
            .RequireAuthorization(PartnerOrAdmin)
            .RequireIfMatch();
    }

    /// <summary>The ETag header, and 201 with a Location for a resource that was just created, 200 for everything else.</summary>
    public static Results<Created<T>, Ok<T>> Respond<T>(this HttpContext http, Resource<T> resource, string? location = null)
    {
        http.Response.SetETag(resource.Version);
        return resource.IsNew && location is not null
            ? TypedResults.Created(Operations.OperationLocation(http.Request, location), resource.Value)
            : TypedResults.Ok(resource.Value);
    }

    public static Caller ToCaller(this ClaimsPrincipal user) => new(user.PartnerId(), user.IsAdmin());
}
