using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.Options;

namespace Viatica.Kernel.Web;

/// <summary>Marks an endpoint that answers without an <c>api-version</c>: health probes, the OpenAPI document.</summary>
[AttributeUsage(AttributeTargets.Class | AttributeTargets.Method, Inherited = false)]
public sealed class ApiVersionNeutralAttribute : Attribute;

public static class ApiVersionEndpointExtensions
{
    /// <summary>Lets the endpoint answer without <c>api-version</c>. Every other endpoint requires it.</summary>
    public static TBuilder WithoutApiVersion<TBuilder>(this TBuilder builder)
        where TBuilder : IEndpointConventionBuilder
    {
        ArgumentNullException.ThrowIfNull(builder);
        return builder.WithMetadata(new ApiVersionNeutralAttribute());
    }
}

/// <summary>
/// Requires <c>?api-version=</c> on every routed endpoint not marked <see cref="ApiVersionNeutralAttribute"/>, and
/// rejects a version the service does not support. ADR 0003 says why this is a middleware and not Asp.Versioning.
/// </summary>
/// <remarks>
/// Only <see cref="RouteEndpoint"/>s are checked. A request that matched nothing stays a 404, and the endpoint the
/// router substitutes for a wrong HTTP method (a plain <see cref="Endpoint"/>) stays a 405, so a client is not
/// told to add a version to a URL that does not exist.
/// </remarks>
internal sealed class ApiVersionMiddleware(RequestDelegate next, IOptions<ViaticaApiOptions> options)
{
    public const string Parameter = "api-version";

    private readonly HashSet<string> supported = new(options.Value.SupportedApiVersions, StringComparer.Ordinal);
    private readonly string list = string.Join(", ", options.Value.SupportedApiVersions);

    public Task InvokeAsync(HttpContext context)
    {
        if (context.GetEndpoint() is not RouteEndpoint endpoint
            || endpoint.Metadata.GetMetadata<ApiVersionNeutralAttribute>() is not null)
        {
            return next(context);
        }

        var versions = context.Request.Query[Parameter];
        if (versions.Count == 0)
        {
            return ErrorResponses.WriteAsync(context, StatusCodes.Status400BadRequest,
                new ApiError(ApiErrorCode.MissingApiVersionParameter,
                    $"The api-version query parameter is required. Supported versions: {list}.")
                {
                    Target = Parameter,
                });
        }

        if (versions.Count > 1 || !supported.Contains(versions[0]!))
        {
            return ErrorResponses.WriteAsync(context, StatusCodes.Status400BadRequest,
                new ApiError(ApiErrorCode.UnsupportedApiVersionValue,
                    versions.Count > 1
                        ? $"Send api-version once. Supported versions: {list}."
                        : $"This service does not support api-version '{Clip(versions[0]!)}'. Supported versions: {list}.")
                {
                    Target = Parameter,
                });
        }

        return next(context);
    }

    private static string Clip(string text) => text.Length <= 32 ? text : string.Concat(text.AsSpan(0, 32), "...");
}
