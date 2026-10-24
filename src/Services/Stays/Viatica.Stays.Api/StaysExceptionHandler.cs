using Microsoft.AspNetCore.Diagnostics;
using Microsoft.EntityFrameworkCore;
using Npgsql;
using Viatica.Kernel;
using Viatica.Kernel.Web;

namespace Viatica.Stays.Api;

/// <summary>
/// The two database failures that are the client's doing, answered as the contract says. Kernel.Web has no EF Core
/// reference, so each service maps them.
/// </summary>
/// <remarks>
/// Registered before the kernel's handler: that one answers every exception it is given, a 500 included, so a handler
/// registered after it would never run.
/// </remarks>
internal sealed class StaysExceptionHandler : IExceptionHandler
{
    public async ValueTask<bool> TryHandleAsync(HttpContext httpContext, Exception exception, CancellationToken cancellationToken)
    {
        if (httpContext.Response.HasStarted)
        {
            return false;
        }

        // A writer slipped in between the read and the save: the row's xmin is not the one this request read.
        if (exception is DbUpdateConcurrencyException)
        {
            await ErrorResponses.WriteAsync(
                httpContext,
                StatusCodes.Status412PreconditionFailed,
                new ApiError(ApiErrorCode.PreconditionFailed, "The resource has changed since it was read. Read it again and retry with the new ETag."));
            return true;
        }

        // Two requests creating the same id, or two calendar edits opening the same night, at the same moment.
        if (exception is DbUpdateException { InnerException: PostgresException { SqlState: PostgresErrorCodes.UniqueViolation } })
        {
            await ErrorResponses.WriteAsync(
                httpContext,
                StatusCodes.Status409Conflict,
                new ApiError(ApiErrorCode.Conflict, "A concurrent request created the same resource. Read it and try again."));
            return true;
        }

        return false;
    }
}
