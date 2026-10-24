using System.Security.Claims;
using Relevox;
using Viatica.Cars.Application;
using Viatica.Cars.Application.Features.PartnerSettings;
using Viatica.Kernel;
using Viatica.Kernel.Web;

namespace Viatica.Cars.Api.Endpoints;

/// <summary>The fees a partner applies across all its locations.</summary>
public sealed record PartnerSettingsBody(Money OneWayFee, Money YoungDriverSurchargePerDay);

internal static class PartnerSettingsEndpoints
{
    public static void MapPartnerSettingsEndpoints(this IEndpointRouteBuilder app)
    {
        RouteGroupBuilder group = app.MapGroup("/cars/partner/settings").RequireAuthorization(Policies.Partner);

        // Before the first save this answers the defaults (both fees zero) with no ETag.
        group.MapGet("/", async (ClaimsPrincipal user, HttpContext http, ISender sender, CancellationToken ct) =>
            LocationEndpoints.Respond(http, await sender.Send(new GetPartnerSettings(user.PartnerId()!.Value), ct)));

        // There is one settings record per partner, so its id is the partner: PUT, with If-Match * the first time.
        group.MapPut("/", async (PartnerSettingsBody body, ClaimsPrincipal user, HttpContext http, ISender sender, CancellationToken ct) =>
        {
            Versioned<PartnerSettingsView> stored = await sender.Send(
                new PutPartnerSettings(user.PartnerId()!.Value, http.RowVersion(), body.OneWayFee, body.YoungDriverSurchargePerDay), ct);
            return LocationEndpoints.Respond(http, stored, Operations.OperationLocation(http.Request, "/cars/partner/settings"));
        }).RequireIfMatch();
    }
}
