using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.HttpResults;

namespace Viatica.Kernel.Web;

/// <summary>The long running operation pattern: <c>202 Accepted</c> with an <c>Operation-Location</c> to poll.</summary>
public static class Operations
{
    public const string OperationLocationHeader = "Operation-Location";

    /// <summary>
    /// A <c>202</c> whose body is the operation's current state and whose <c>Operation-Location</c> is the absolute
    /// URL of <paramref name="operationPath"/> with the request's <c>api-version</c>.
    /// </summary>
    public static JsonHttpResult<Operation> Accepted(this HttpContext context, Operation operation, string operationPath)
    {
        ArgumentNullException.ThrowIfNull(context);
        ArgumentNullException.ThrowIfNull(operation);

        context.Response.Headers[OperationLocationHeader] = OperationLocation(context.Request, operationPath);
        return TypedResults.Json(operation, KernelJsonContext.Default.Operation, statusCode: StatusCodes.Status202Accepted);
    }

    /// <summary>The absolute URL a client polls. It carries the same <c>api-version</c> as the request that started it.</summary>
    public static string OperationLocation(HttpRequest request, string operationPath)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentException.ThrowIfNullOrWhiteSpace(operationPath);

        string version = request.Query[ApiVersionMiddleware.Parameter].ToString();
        return string.Concat(
            request.Scheme, "://", request.Host.ToUriComponent(), request.PathBase.ToUriComponent(),
            new PathString(operationPath).ToUriComponent(),
            version.Length == 0 ? string.Empty : "?api-version=" + Uri.EscapeDataString(version));
    }
}
