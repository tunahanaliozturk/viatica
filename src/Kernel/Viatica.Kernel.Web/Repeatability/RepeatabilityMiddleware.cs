using System.Globalization;
using System.Security.Claims;
using System.Security.Cryptography;
using System.Text;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
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

/// <summary>
/// Repeatable requests as the Azure REST API Guidelines describe them (after the OASIS Repeatable Requests spec).
/// </summary>
/// <remarks>
/// <para>
/// The first request atomically claims its id in the <see cref="IRepeatabilityStore"/>, runs, and replaces the
/// claim with its response. A copy arriving while it runs finds the claim and gets <c>409</c>; a copy arriving
/// after it finds the response and gets it replayed. Only the claimant ever runs the handler.
/// </para>
/// <para>
/// The id is scoped to the caller (the token's <c>sub</c>), so one user can never be served another user's stored
/// response by guessing an id. A fingerprint of method, path, query and body is stored with it: the same id on a
/// different request is a client bug and is refused rather than answered with the wrong response.
/// </para>
/// <para>
/// <c>Repeatability-First-Sent</c> is what makes forgetting safe. Completed responses are kept until first-sent
/// plus the window, and a request whose first-sent is already outside the window is refused, so a retry that
/// arrives after the entry is gone cannot run a second time.
/// </para>
/// <para>
/// A running request holds its claim on a short lease that it renews until it finishes, instead of for the rest of
/// the window. A handler that runs longer than the window, or a store clock that disagrees with this instance's,
/// therefore cannot let a copy claim the id while the original still runs; a process that dies stops renewing and
/// frees its id after one lease, not after a day of 409s. Every claim carries an owner token and the store
/// completes, renews and releases only for that owner, so a request that did lose its claim cannot overwrite or
/// delete the next one.
/// </para>
/// <para>
/// A 5xx or an exception other than <see cref="ApiErrorException"/> releases the claim: the client should be able
/// to retry a failure, and a claim stuck until expiry would turn every retry into a 409.
/// </para>
/// </remarks>
internal sealed class RepeatabilityMiddleware(
    RequestDelegate next, IRepeatabilityStore store, TimeProvider time, IOptions<ViaticaApiOptions> options)
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
    private readonly TimeSpan lease = options.Value.RepeatabilityLease;

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

        string key = Key(context.User, requestId);
        string fingerprint = await Fingerprint(context.Request);
        string owner = Guid.NewGuid().ToString("N");

        RepeatableResponse? existing = await store.TryClaimAsync(key, RepeatableResponse.Claim(fingerprint, owner), lease, context.RequestAborted);
        if (existing is not null)
        {
            await Answer(context, existing, fingerprint);
            return;
        }

        Stream original = context.Response.Body;
        using var buffer = new MemoryStream();
        context.Response.Body = buffer;
        using var stopRenewing = new CancellationTokenSource();
        Task renewing = KeepClaimAsync(key, owner, stopRenewing.Token);
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
            await stopRenewing.CancelAsync();
            await renewing;
            await store.ReleaseAsync(key, owner, CancellationToken.None);
            throw;
        }

        await stopRenewing.CancelAsync();
        await renewing;
        context.Response.Body = original;
        HttpResponse response = context.Response;
        if (response.StatusCode >= 500)
        {
            await store.ReleaseAsync(key, owner, CancellationToken.None);
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

            var completed = new RepeatableResponse(fingerprint, true, response.StatusCode, response.ContentType, headers, buffer.ToArray(), owner);

            // Measured again: the handler may have run for a good part of the window. A response is kept at least one
            // clock skew past it, so a copy that this instance's clock still accepts is replayed, not run again.
            TimeSpan keep = firstSent + window - time.GetUtcNow();
            await store.CompleteAsync(key, completed, keep > AllowedClockSkew ? keep : AllowedClockSkew, CancellationToken.None);
            response.Headers[ResultHeader] = "accepted";
        }

        buffer.Position = 0;
        await buffer.CopyToAsync(original, context.RequestAborted);
    }

    // Renews the claim until told to stop. A store that fails here is also failing the request's own calls, which
    // surface the error; the claim just lapses after one lease.
    private async Task KeepClaimAsync(string key, string owner, CancellationToken stop)
    {
        using var timer = new PeriodicTimer(lease / 3, time);
        try
        {
            while (await timer.WaitForNextTickAsync(stop) && await store.RenewAsync(key, owner, lease, stop))
            {
            }
        }
        catch (Exception)
        {
        }
    }

    private static Task Answer(HttpContext context, RepeatableResponse existing, string fingerprint)
    {
        if (!string.Equals(existing.Fingerprint, fingerprint, StringComparison.Ordinal))
        {
            return Reject(context, StatusCodes.Status400BadRequest, ApiErrorCode.InvalidRequest,
                "This Repeatability-Request-ID was already used for a different request.", RequestIdHeader);
        }

        return existing.Completed
            ? Replay(context, existing)
            : Reject(context, StatusCodes.Status409Conflict, ApiErrorCode.Conflict,
                "A request with this Repeatability-Request-ID is still running. Retry in a moment.", RequestIdHeader);
    }

    private static async Task Replay(HttpContext context, RepeatableResponse stored)
    {
        HttpResponse response = context.Response;
        response.StatusCode = stored.StatusCode;
        if (stored.ContentType is not null)
        {
            response.ContentType = stored.ContentType;
        }

        foreach (var (name, value) in stored.Headers ?? new Dictionary<string, string>())
        {
            response.Headers[name] = value;
        }

        response.Headers[ResultHeader] = "accepted";
        if (!stored.Body.IsEmpty)
        {
            await response.Body.WriteAsync(stored.Body, context.RequestAborted);
        }
    }

    private static Task Reject(HttpContext context, int status, string code, string message, string target)
    {
        context.Response.Headers[ResultHeader] = "rejected";
        return ErrorResponses.WriteAsync(context, status, new ApiError(code, message) { Target = target });
    }

    private static string Key(ClaimsPrincipal user, string requestId)
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
}
