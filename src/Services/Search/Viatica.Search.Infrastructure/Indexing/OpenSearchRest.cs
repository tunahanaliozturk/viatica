using System.Text.Json;
using OpenSearch.Net;
using HttpMethod = OpenSearch.Net.HttpMethod;

namespace Viatica.Search.Infrastructure.Indexing;

/// <summary>
/// An answer OpenSearch gave that the caller cannot use. <see cref="IsTransient"/> says whether trying again later
/// can help: the node was unreachable, overloaded or restarting. A 4xx will fail the same way every time.
/// </summary>
public sealed class OpenSearchRequestException : Exception
{
    public OpenSearchRequestException(string message, int status, Exception? inner = null)
        : base(message, inner)
    {
        Status = status;
    }

    /// <summary>The HTTP status, or 0 when no answer came at all.</summary>
    public int Status { get; }

    public bool IsTransient => Status is 0 or 429 or >= 500;
}

/// <summary>
/// The few REST calls the service makes, over the low-level client. The search bodies are built as JSON by hand
/// (nested queries with script sorts are what the typed client is worst at expressing) and the documents go through
/// source-generated serializers, so the typed client's reflection-based serializer is never used on a hot path.
/// </summary>
public sealed class OpenSearchRest(IOpenSearchLowLevelClient client)
{
    public async Task<JsonDocument> SendAsync(
        HttpMethod method, string path, string? body, CancellationToken cancellationToken, params int[] alsoAccept)
    {
        StringResponse response = await client.DoRequestAsync<StringResponse>(
            method, path, cancellationToken, body is null ? null : PostData.String(body));
        return Parse(response, $"{method} {path}", alsoAccept);
    }

    /// <summary>Sends newline-delimited actions and documents to <c>_bulk</c>.</summary>
    public async Task<JsonDocument> BulkAsync(IEnumerable<string> lines, CancellationToken cancellationToken)
    {
        StringResponse response = await client.BulkAsync<StringResponse>(PostData.MultiJson(lines), ctx: cancellationToken);
        return Parse(response, "POST _bulk", []);
    }

    public async Task<bool> ExistsAsync(string path, CancellationToken cancellationToken)
    {
        StringResponse response = await client.DoRequestAsync<StringResponse>(HttpMethod.HEAD, path, cancellationToken);
        return response.HttpStatusCode switch
        {
            200 => true,
            404 => false,
            { } status => throw new OpenSearchRequestException($"HEAD {path} answered {status}.", status),
            null => throw new OpenSearchRequestException($"HEAD {path} got no answer.", 0, response.OriginalException),
        };
    }

    private static JsonDocument Parse(StringResponse response, string what, int[] alsoAccept)
    {
        if (response.HttpStatusCode is not { } status)
        {
            throw new OpenSearchRequestException($"{what} got no answer: {response.OriginalException?.Message}", 0, response.OriginalException);
        }

        if (status is < 200 or >= 300 && !alsoAccept.Contains(status))
        {
            string excerpt = response.Body is { Length: > 600 } text ? text[..600] : response.Body ?? "";
            throw new OpenSearchRequestException($"{what} answered {status}: {excerpt}", status);
        }

        return JsonDocument.Parse(string.IsNullOrEmpty(response.Body) ? "{}" : response.Body);
    }
}
