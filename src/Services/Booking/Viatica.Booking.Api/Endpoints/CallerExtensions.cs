using System.Security.Claims;
using Viatica.Booking.Application;
using Viatica.Kernel.Web;

namespace Viatica.Booking.Api.Endpoints;

internal static class CallerExtensions
{
    public const string AccessKeyHeader = "Trip-Access-Key";

    /// <summary>
    /// Who is asking: the signed-in traveller if there is a traveller token, an admin if there is an admin token, and
    /// the secret from <c>Trip-Access-Key</c> for the anonymous trip builder. A partner token makes a caller with
    /// neither, so a partner can read no trip.
    /// </summary>
    public static Caller ToCaller(this HttpContext http)
    {
        ArgumentNullException.ThrowIfNull(http);
        ClaimsPrincipal user = http.User;

        Guid? traveller = user.Identity?.IsAuthenticated == true && user.IsInRole(Roles.Traveller) ? user.SubjectId() : null;
        string? key = http.Request.Headers[AccessKeyHeader].ToString() is { Length: > 0 } header ? header : null;
        return new Caller(traveller, user.IsAdmin(), key);
    }
}
