using Microsoft.AspNetCore.Diagnostics;
using Microsoft.EntityFrameworkCore;
using Npgsql;
using Viatica.Cars.Domain;
using Viatica.Kernel;
using Viatica.Kernel.Web;

namespace Viatica.Cars.Api;

/// <summary>
/// Maps what Postgres says about a write onto the API contract. Kernel.Web has no EF Core reference, so each
/// service does this for itself.
/// </summary>
internal sealed class DatabaseExceptionHandler : IExceptionHandler
{
    private const string OversoldConstraint = "ck_car_inventory_days_not_oversold";

    public async ValueTask<bool> TryHandleAsync(HttpContext httpContext, Exception exception, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(httpContext);

        ApiErrorException? mapped = exception switch
        {
            // Two writers read the same version; the row's xmin let one of them through.
            DbUpdateConcurrencyException => ApiErrorException.PreconditionFailed(),

            // A client generated id used twice at once, or a bulk write racing another that opened the same days.
            DbUpdateException { InnerException: PostgresException { SqlState: PostgresErrorCodes.UniqueViolation } } =>
                ApiErrorException.Conflict(ApiErrorCode.Conflict, "The request clashes with a change made at the same moment. Read the resource again and retry."),

            // The check constraint caught a hold that slipped in between the read and the write.
            DbUpdateException { InnerException: PostgresException { SqlState: PostgresErrorCodes.CheckViolation, ConstraintName: OversoldConstraint } } =>
                ApiErrorException.Conflict(CarErrorCode.AllotmentBelowCommitments, "More cars were sold or held than the new allotment allows."),

            _ => null,
        };

        if (mapped is null || httpContext.Response.HasStarted)
        {
            return false;
        }

        await ErrorResponses.WriteAsync(httpContext, mapped);
        return true;
    }
}
