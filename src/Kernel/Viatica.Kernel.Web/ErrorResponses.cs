using System.Text.Json;
using Microsoft.AspNetCore.Diagnostics;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.Logging;

namespace Viatica.Kernel.Web;

/// <summary>The body of every non-2xx response: <c>{ "error": { ... } }</c>.</summary>
public sealed record ErrorResponse(ApiError Error);

/// <summary>Writes the error envelope, and maps error categories and status codes onto each other.</summary>
public static class ErrorResponses
{
    public static int StatusCodeOf(ErrorCategory category) => category switch
    {
        ErrorCategory.InvalidRequest => StatusCodes.Status400BadRequest,
        ErrorCategory.NotFound => StatusCodes.Status404NotFound,
        ErrorCategory.Conflict => StatusCodes.Status409Conflict,
        ErrorCategory.PreconditionFailed => StatusCodes.Status412PreconditionFailed,
        ErrorCategory.PreconditionRequired => StatusCodes.Status428PreconditionRequired,
        ErrorCategory.TooManyRequests => StatusCodes.Status429TooManyRequests,
        _ => StatusCodes.Status500InternalServerError,
    };

    /// <summary>
    /// The error for a status the framework produced with no body: an unmatched route, a wrong method, a failed
    /// authentication, a body that would not bind. The message is generic on purpose; the framework's own text
    /// can name internal types.
    /// </summary>
    public static ApiError ForStatusCode(int statusCode) => statusCode switch
    {
        400 => new(ApiErrorCode.InvalidRequest, "The request is invalid."),
        401 => new(ApiErrorCode.Unauthorized, "Authentication is required."),
        403 => new(ApiErrorCode.Forbidden, "You are not allowed to do this."),
        404 => new(ApiErrorCode.NotFound, "The resource was not found."),
        405 => new(ApiErrorCode.MethodNotAllowed, "The HTTP method is not allowed on this resource."),
        409 => new(ApiErrorCode.Conflict, "The request conflicts with the current state of the resource."),
        412 => new(ApiErrorCode.PreconditionFailed, "The resource has changed since it was read."),
        415 => new(ApiErrorCode.UnsupportedMediaType, "The request body must be application/json."),
        428 => new(ApiErrorCode.PreconditionRequired, "This request needs an If-Match header."),
        429 => new(ApiErrorCode.TooManyRequests, "Too many requests. Wait before trying again."),
        >= 400 and < 500 => new(ApiErrorCode.InvalidRequest, "The request is invalid."),
        _ => new(ApiErrorCode.InternalError, "An internal error occurred."),
    };

    public static Task WriteAsync(HttpContext context, ApiErrorException exception)
    {
        ArgumentNullException.ThrowIfNull(exception);
        return WriteAsync(context, StatusCodeOf(exception.Category), exception.Error);
    }

    public static Task WriteAsync(HttpContext context, int statusCode, ApiError error)
    {
        ArgumentNullException.ThrowIfNull(context);

        context.Response.StatusCode = statusCode;
        context.Response.ContentType = "application/json; charset=utf-8";
        return JsonSerializer.SerializeAsync(
            context.Response.Body, new ErrorResponse(error), KernelJsonContext.Default.ErrorResponse,
            context.RequestAborted);
    }

    /// <summary>The <c>UseStatusCodePages</c> handler: an envelope for any error status left without a body.</summary>
    internal static Task WriteForStatusCodeAsync(StatusCodeContext context)
    {
        int status = context.HttpContext.Response.StatusCode;
        return WriteAsync(context.HttpContext, status, ForStatusCode(status));
    }
}

/// <summary>
/// Turns exceptions into the envelope: <see cref="ApiErrorException"/> with its own status and code, a request the
/// framework could not bind as a 400, anything else as a logged 500 that reveals nothing.
/// </summary>
internal sealed partial class ApiExceptionHandler(ILogger<ApiExceptionHandler> logger) : IExceptionHandler
{
    public async ValueTask<bool> TryHandleAsync(HttpContext httpContext, Exception exception, CancellationToken cancellationToken)
    {
        if (httpContext.Response.HasStarted)
        {
            return false;
        }

        switch (exception)
        {
            case ApiErrorException error:
                await ErrorResponses.WriteAsync(httpContext, error);
                return true;

            // Thrown for a malformed body or a missing parameter when the host throws on bad requests (the
            // development default). The status is the framework's; the message is not, it can name types.
            case BadHttpRequestException badRequest:
                await ErrorResponses.WriteAsync(httpContext, badRequest.StatusCode, ErrorResponses.ForStatusCode(badRequest.StatusCode));
                return true;

            // The caller went away. There is nobody to answer and nothing went wrong on our side.
            case OperationCanceledException when httpContext.RequestAborted.IsCancellationRequested:
                return true;
        }

        // The route pattern, not the path: the path can carry ids, and the log has no use for them.
        string endpoint = (httpContext.GetEndpoint() as RouteEndpoint)?.RoutePattern.RawText ?? "(no endpoint)";
        LogUnhandled(logger, exception, httpContext.Request.Method, endpoint);

        await ErrorResponses.WriteAsync(httpContext, StatusCodes.Status500InternalServerError, ErrorResponses.ForStatusCode(500));
        return true;
    }

    [LoggerMessage(Level = LogLevel.Error, Message = "Unhandled exception on {Method} {Endpoint}")]
    private static partial void LogUnhandled(ILogger logger, Exception exception, string method, string endpoint);
}
