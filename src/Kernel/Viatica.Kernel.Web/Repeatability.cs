using System.Globalization;
using System.Security.Claims;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Caching.Distributed;
using Microsoft.Extensions.Options;
using Microsoft.Net.Http.Headers;

namespace Viatica.Kernel.Web;

/// <summary>Marks an endpoint that honours <c>Repeatability-Request-ID</c> and <c>Repeatability-First-Sent</c>.</summary>
[AttributeUsage(AttributeTargets.Class | AttributeTargets.Method, Inherited = false)]
public sealed class RepeatableAttribute : Attribute;

public static class RepeatabilityEndpointExtensions
{
    /// <summary>
    /// For operations without a natural id (a refund, a bulk rate update): a retried request with the same
    /// <c>Repeatability-Request-ID</c> gets the first response back instead of running twice.
    /// </summary>
    public static TBuilder WithRepeatability<TBuilder>(this TBuilder builder)
        where TBuilder : IEndpointConventionBuilder
    {
        ArgumentNullException.ThrowIfNull(builder);
        return builder.WithMetadata(new RepeatableAttribute());
    }
}

/// <summary>A response kept for replay, or the marker that the first request is still running.</summary>
internal sealed record RepeatableResponse(
    string Fingerprint,
    bool Completed,
    int StatusCode,
    string? ContentType,
    Dictionary<string, string>? Headers,
    byte[]? Body);

/// <summary>
/// Repeatable requests as the Azure REST API Guidelines describe them (after the OASIS Repeatable Requests spec).
/// </summary>
/// <remarks>
/// <para>
/// The request id is scoped to the caller (the token's <c>sub</c>), so one user can never be served another
/// user's stored response by guessing an id. A fingerprint of method, path, query and body is stored with it: the
/// same id on a different request is a client bug and is refused rather than answered with the wrong response.
/// </para>
/// <para>
/// <c>Repeatability-First-Sent</c> is what makes forgetting safe. Entries expire at first-sent plus the window,
/// and a request whose first-sent is already outside the window is refused, so a retry that arrives after the
/// entry is gone cannot run a second time.
/// </para>
/// <para>
/// 5xx responses are not kept: the client should be able to retry a failure. Exceptions other than
/// <see cref="ApiErrorException"/> drop the in-progress marker for the same reason.
/// </para>
/// </remarks>
internal sealed class RepeatabilityMiddleware(
    RequestDelegate next, IDistributedCache cache, TimeProvider time, IOptions<ViaticaApiOptions> options)
{
    public const string RequestIdHeader = "Repeatability-Request-ID";
    public const string FirstSentHeader = "Repeatability-First-Sent";
    public const string ResultHeader = "Repeatability-Result";

    // Clocks drift; a first-sent a little in the future is a client clock, a lot in the future is a client
    // trying to stretch the window.
    private static readonly TimeSpan AllowedClockSkew = TimeSpan.FromMinutes(5);

    private static readonly string[] ReplayedHeaders =
        [HeaderNames.Location, HeaderNames.ETag, HeaderNames.ContentLocation, Operations.OperationLocationHeader];

    private readonly TimeSpan window = options.Value.RepeatabilityWindow;

    public async Task InvokeAsync(HttpContext context)
    {
        string? requestId = context.Request.Headers[RequestIdHeader];
        if (requestId is null || context.GetEndpoint()?.Metadata.GetMetadata<RepeatableAttribute>() is null)
        {
            await next(context);
            return;
        }

        if (requestId.Length is 0 or > 128 || !requestId.All(static c => char.IsAsciiLetterOrDigit(c) || c is '-' or '_' or '.' or ':'))
        {
            await Reject(context, StatusCodes.Status400BadRequest, ApiErrorCode.InvalidRequest,
                "Repeatability-Request-ID must be 1 to 128 letters, digits, '-', '_', '.' or ':'; a UUID is ideal.", RequestIdHeader);
            return;
        }

        DateTimeOffset now = time.GetUtcNow();
        if (!DateTimeOffset.TryParseExact(context.Request.Headers[FirstSentHeader].ToString(), "r",
                CultureInfo.InvariantCulture, DateTimeStyles.AdjustToUniversal, out DateTimeOffset firstSent))
        {
            await Reject(context, StatusCodes.Status400BadRequest, ApiErrorCode.InvalidRequest,
                "Repeatability-First-Sent must be an HTTP date, such as Tue, 01 Dec 2026 14:00:00 GMT.", FirstSentHeader);
            return;
        }

        if (now - firstSent > window || firstSent - now > AllowedClockSkew)
        {
            await Reject(context, StatusCodes.Status400BadRequest, ApiErrorCode.InvalidRequest,
                "Repeatability-First-Sent is outside the window this service remembers requests for. Send it as a new request with a new id.",
                FirstSentHeader);
            return;
        }

        string key = CacheKey(context.User, requestId);
        string fingerprint = await Fingerprint(context.Request);
        var expiry = new DistributedCacheEntryOptions { AbsoluteExpiration = firstSent + window };

        byte[]? existing = await cache.GetAsync(key, context.RequestAborted);
        if (existing is not null)
        {
            RepeatableResponse stored = JsonSerializer.Deserialize(existing, KernelJsonContext.Default.RepeatableResponse)!;
            if (!string.Equals(stored.Fingerprint, fingerprint, StringComparison.Ordinal))
            {
                await Reject(context, StatusCodes.Status400BadRequest, ApiErrorCode.InvalidRequest,
                    "This Repeatability-Request-ID was already used for a different request.", RequestIdHeader);
            }
            else if (!stored.Completed)
            {
                await Reject(context, StatusCodes.Status409Conflict, ApiErrorCode.Conflict,
                    "A request with this Repeatability-Request-ID is still running. Retry in a moment.", RequestIdHeader);
            }
            else
            {
                await Replay(context, stored);
            }

            return;
        }

        // ponytail: get-then-set on IDistributedCache leaves a window of a few milliseconds where two identical
        // requests arriving together both run. Closing it needs SET NX through a Redis-specific seam; until then
        // the operations behind this (refunds, bulk updates) must still be safe to apply twice at the domain level.
        await cache.SetAsync(key, Serialize(new RepeatableResponse(fingerprint, false, 0, null, null, null)), expiry, context.RequestAborted);

        Stream original = context.Response.Body;
        using var buffer = new MemoryStream();
        context.Response.Body = buffer;
        try
        {
            try
            {
                await next(context);
            }
            catch (ApiErrorException error)
            {
                // A domain refusal (sold out, stale ETag) is an answer, so it is kept and replayed like any other.
                context.Response.Clear();
                await ErrorResponses.WriteAsync(context, error);
            }
        }
        catch
        {
            context.Response.Body = original;
            await cache.RemoveAsync(key, CancellationToken.None);
            throw;
        }

        context.Response.Body = original;
        HttpResponse response = context.Response;
        if (response.StatusCode >= 500)
        {
            await cache.RemoveAsync(key, CancellationToken.None);
        }
        else
        {
            var headers = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            foreach (string name in ReplayedHeaders)
            {
                if (response.Headers.TryGetValue(name, out var value))
                {
                    headers[name] = value.ToString();
                }
            }

            var completed = new RepeatableResponse(fingerprint, true, response.StatusCode, response.ContentType, headers, buffer.ToArray());
            await cache.SetAsync(key, Serialize(completed), expiry, CancellationToken.None);
            response.Headers[ResultHeader] = "accepted";
        }

        buffer.Position = 0;
        await buffer.CopyToAsync(original, context.RequestAborted);
    }

    private static async Task Replay(HttpContext context, RepeatableResponse stored)
    {
        HttpResponse response = context.Response;
        response.StatusCode = stored.StatusCode;
        if (stored.ContentType is not null)
        {
            response.ContentType = stored.ContentType;
        }

        foreach (var (name, value) in stored.Headers ?? [])
        {
            response.Headers[name] = value;
        }

        response.Headers[ResultHeader] = "accepted";
        if (stored.Body is { Length: > 0 } body)
        {
            await response.Body.WriteAsync(body, context.RequestAborted);
        }
    }

    private static Task Reject(HttpContext context, int status, string code, string message, string target)
    {
        context.Response.Headers[ResultHeader] = "rejected";
        return ErrorResponses.WriteAsync(context, status, new ApiError(code, message) { Target = target });
    }

    private static string CacheKey(ClaimsPrincipal user, string requestId)
    {
        string caller = user.FindFirst("sub")?.Value ?? user.FindFirst(ClaimTypes.NameIdentifier)?.Value ?? string.Empty;
        byte[] hash = SHA256.HashData(Encoding.UTF8.GetBytes($"{caller.Length}:{caller}{requestId}"));
        return "viatica:repeatability:" + Convert.ToHexString(hash);
    }

    private static async Task<string> Fingerprint(HttpRequest request)
    {
        // The body is read twice: once here and once by the endpoint. Buffering makes the stream rewindable and
        // spills large bodies to disk instead of holding them in memory.
        request.EnableBuffering();
        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        hash.AppendData(Encoding.UTF8.GetBytes($"{request.Method}\n{request.PathBase}{request.Path}\n{request.QueryString}\n"));

        byte[] chunk = new byte[16 * 1024];
        int read;
        while ((read = await request.Body.ReadAsync(chunk, request.HttpContext.RequestAborted)) > 0)
        {
            hash.AppendData(chunk, 0, read);
        }

        request.Body.Position = 0;
        return Convert.ToHexString(hash.GetHashAndReset());
    }

    private static byte[] Serialize(RepeatableResponse response) =>
        JsonSerializer.SerializeToUtf8Bytes(response, KernelJsonContext.Default.RepeatableResponse);
}
