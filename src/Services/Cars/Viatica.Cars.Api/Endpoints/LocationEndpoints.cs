using System.Security.Claims;
using Relevox;
using Viatica.Cars.Application;
using Viatica.Cars.Application.Features.GetLocation;
using Viatica.Cars.Application.Features.ListLocations;
using Viatica.Cars.Application.Features.PublishLocation;
using Viatica.Cars.Application.Features.PutLocation;
using Viatica.Cars.Domain;
using Viatica.Kernel.Querying;
using Viatica.Kernel.Web;

namespace Viatica.Cars.Api.Endpoints;

/// <summary>What a partner sends to create or replace a location. The id is in the path, the partner in the token.</summary>
public sealed record LocationBody(
    string Name,
    Address Address,
    GeoPoint Position,
    IReadOnlyList<OpeningWindow> OpeningHours,
    CancellationPolicy CancellationPolicy,
    string? AirportCode = null);

internal static class LocationEndpoints
{
    public static void MapLocationEndpoints(this IEndpointRouteBuilder app)
    {
        RouteGroupBuilder group = app.MapGroup("/cars/locations").RequireAuthorization(Policies.Partner);

        group.MapGet("/", ListAsync);
        group.MapGet("/{id:guid}", GetAsync);

        // PUT with an id the client chose, so a retry is the same request. If-Match is required: the version read
        // before, or * to create (or to replace whatever is there).
        group.MapPut("/{id:guid}", PutAsync).RequireIfMatch();

        group.MapPost("/{id:guid}/publish", (Guid id, ClaimsPrincipal user, HttpContext http, ISender sender, CancellationToken ct) =>
            PublishAsync(id, publish: true, user, http, sender, ct));
        group.MapPost("/{id:guid}/unpublish", (Guid id, ClaimsPrincipal user, HttpContext http, ISender sender, CancellationToken ct) =>
            PublishAsync(id, publish: false, user, http, sender, ct));
    }

    private static async Task<IResult> ListAsync(ClaimsPrincipal user, HttpContext http, ISender sender, CancellationToken ct)
    {
        CollectionQuery<RentalLocation> query = http.BindCollectionQuery(ListLocations.Fields);
        return http.Page(await sender.Send(new ListLocations(user.PartnerId()!.Value, query), ct));
    }

    private static async Task<IResult> GetAsync(Guid id, ClaimsPrincipal user, HttpContext http, ISender sender, CancellationToken ct) =>
        Respond(http, await sender.Send(new GetLocation(user.PartnerId()!.Value, id), ct));

    private static async Task<IResult> PutAsync(
        Guid id, LocationBody body, ClaimsPrincipal user, HttpContext http, ISender sender, CancellationToken ct)
    {
        Versioned<LocationView> stored = await sender.Send(
            new PutLocation(
                user.PartnerId()!.Value, id, http.RowVersion(), body.Name, body.Address, body.Position,
                body.AirportCode, body.OpeningHours, body.CancellationPolicy),
            ct);

        return Respond(http, stored, Operations.OperationLocation(http.Request, $"/cars/locations/{id}"));
    }

    private static async Task<IResult> PublishAsync(
        Guid id, bool publish, ClaimsPrincipal user, HttpContext http, ISender sender, CancellationToken ct) =>
        Respond(http, await sender.Send(new PublishLocation(user.PartnerId()!.Value, id, publish), ct));

    /// <summary>
    /// The version in <c>If-Match</c> as the row version the Application compares with. The ETag holds a ulong, the
    /// row's xmin is 32 bits, so a larger number is a tag this service never issued.
    /// </summary>
    internal static uint? RowVersion(this HttpContext http) =>
        http.IfMatchVersion() switch
        {
            null => null,
            <= uint.MaxValue and { } version => (uint)version,
            _ => throw Viatica.Kernel.ApiErrorException.PreconditionFailed(),
        };

    internal static IResult Respond<T>(HttpContext http, Versioned<T> stored, string? location = null)
    {
        if (stored.Version != 0)
        {
            http.Response.SetETag(stored.Version);
        }

        return stored.Created && location is not null
            ? TypedResults.Created(location, stored.Value)
            : TypedResults.Ok(stored.Value);
    }
}
