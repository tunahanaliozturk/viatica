using System.Globalization;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.Net.Http.Headers;

namespace Viatica.Kernel.Web;

/// <summary>
/// ETags from a row version, and the <c>If-Match</c> rule for updates: missing is <c>428</c>, stale is <c>412</c>.
/// </summary>
/// <remarks>
/// The ETag is the row version in quotes (Postgres <c>xmin</c> or a version column). The endpoint passes
/// <see cref="IfMatchVersion"/> into its command, the handler compares it with the row it loaded and throws
/// <see cref="ApiErrorException.PreconditionFailed"/> on a mismatch, and EF Core's concurrency token catches the
/// writer that slips in between the read and the save.
/// </remarks>
public static class ETags
{
    private static readonly object IfMatchKey = new();

    public static string Format(ulong version) => string.Create(CultureInfo.InvariantCulture, $"\"{version}\"");

    public static void SetETag(this HttpResponse response, ulong version)
    {
        ArgumentNullException.ThrowIfNull(response);
        response.Headers.ETag = Format(version);
    }

    /// <summary>
    /// Makes <c>If-Match</c> mandatory on the endpoint. Without it the request ends with <c>428</c>; with a tag
    /// that cannot match (weak, or not one this API issued) it ends with <c>412</c>.
    /// </summary>
    public static TBuilder RequireIfMatch<TBuilder>(this TBuilder builder)
        where TBuilder : IEndpointConventionBuilder
    {
        ArgumentNullException.ThrowIfNull(builder);
        return builder.AddEndpointFilter(static (context, next) =>
        {
            context.HttpContext.Items[IfMatchKey] = ReadIfMatch(context.HttpContext.Request);
            return next(context);
        });
    }

    /// <summary>
    /// The version the caller expects to change, or null for <c>If-Match: *</c> (any current version). Only
    /// available on endpoints with <see cref="RequireIfMatch"/>.
    /// </summary>
    public static ulong? IfMatchVersion(this HttpContext context)
    {
        ArgumentNullException.ThrowIfNull(context);
        return context.Items.TryGetValue(IfMatchKey, out object? value) && value is IfMatch ifMatch
            ? ifMatch.Version
            : throw new InvalidOperationException("IfMatchVersion needs RequireIfMatch() on the endpoint.");
    }

    private static IfMatch ReadIfMatch(HttpRequest request)
    {
        var header = request.Headers.IfMatch;
        if (header.Count == 0)
        {
            throw new ApiErrorException(ErrorCategory.PreconditionRequired,
                new ApiError(ApiErrorCode.PreconditionRequired,
                    "This request needs an If-Match header with the ETag from your last read.")
                {
                    Target = HeaderNames.IfMatch,
                });
        }

        if (!EntityTagHeaderValue.TryParseStrictList(header, out IList<EntityTagHeaderValue>? tags) || tags.Count != 1)
        {
            throw ApiErrorException.Invalid(ApiErrorCode.InvalidRequest,
                "If-Match must hold exactly one entity tag, or *.", HeaderNames.IfMatch);
        }

        EntityTagHeaderValue tag = tags[0];
        if (tag.Equals(EntityTagHeaderValue.Any))
        {
            return new IfMatch(null);
        }

        // If-Match compares strongly (RFC 9110 13.1.1), so a weak tag never matches; neither does one we never issued.
        ReadOnlySpan<char> value = tag.Tag.AsSpan().Trim('"');
        return !tag.IsWeak && ulong.TryParse(value, NumberStyles.None, CultureInfo.InvariantCulture, out ulong version)
            ? new IfMatch(version)
            : throw ApiErrorException.PreconditionFailed();
    }

    private sealed record IfMatch(ulong? Version);
}
