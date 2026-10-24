using System.Globalization;
using System.Text.Json;
using Microsoft.Extensions.Logging;
using HttpMethod = OpenSearch.Net.HttpMethod;

namespace Viatica.Search.Infrastructure.Indexing;

/// <summary>
/// Creating, aliasing and swapping the two indices. Readers and the live writer only ever name the aliases
/// (<c>stays</c>, <c>cars</c>), so replacing the index behind an alias is invisible to them.
/// </summary>
public sealed partial class IndexAdmin(OpenSearchRest rest, TimeProvider clock, ILogger<IndexAdmin> logger)
{
    /// <summary>
    /// Makes sure each alias exists, creating a first index behind it when it does not. Safe to run any number of
    /// times and from several instances at once; this is the service's one-shot <c>migrate</c> step.
    /// </summary>
    public async Task EnsureAsync(CancellationToken cancellationToken)
    {
        foreach (IndexKind kind in Enum.GetValues<IndexKind>())
        {
            string alias = IndexDefinitions.AliasOf(kind);
            if (await rest.ExistsAsync($"_alias/{alias}", cancellationToken))
            {
                continue;
            }

            string index = NewIndexName(kind);
            await CreateAsync(kind, index, cancellationToken);
            using JsonDocument _ = await rest.SendAsync(
                HttpMethod.PUT, $"{index}/_alias/{alias}", null, cancellationToken);
            LogCreated(logger, index, alias);
        }
    }

    public string NewIndexName(IndexKind kind) =>
        $"{IndexDefinitions.AliasOf(kind)}-{clock.GetUtcNow().ToString("yyyyMMddHHmmss", CultureInfo.InvariantCulture)}-{Guid.NewGuid().ToString("N")[..6]}";

    public async Task CreateAsync(IndexKind kind, string index, CancellationToken cancellationToken)
    {
        try
        {
            using JsonDocument _ = await rest.SendAsync(HttpMethod.PUT, index, IndexDefinitions.Body(kind), cancellationToken);
        }
        catch (OpenSearchRequestException ex) when (ex.Status == 400 && ex.Message.Contains("resource_already_exists_exception", StringComparison.Ordinal))
        {
            // Another instance won the race to create it; the index is there, which is all that was wanted.
        }
    }

    /// <summary>The indices an alias points at now; empty when the alias does not exist.</summary>
    public async Task<IReadOnlyList<string>> BehindAsync(string alias, CancellationToken cancellationToken)
    {
        using JsonDocument answer = await rest.SendAsync(HttpMethod.GET, $"_alias/{alias}", null, cancellationToken, 404);
        return answer.RootElement.ValueKind == JsonValueKind.Object && !answer.RootElement.TryGetProperty("error", out _)
            ? [.. answer.RootElement.EnumerateObject().Select(static p => p.Name)]
            : [];
    }

    /// <summary>
    /// Points each alias at its new index and away from the old ones in one request, which OpenSearch applies
    /// atomically: a query sees the old index or the new one, never neither and never both.
    /// </summary>
    public async Task SwapAsync(IReadOnlyDictionary<IndexKind, string> newIndices, CancellationToken cancellationToken)
    {
        var actions = new List<string>();
        foreach ((IndexKind kind, string index) in newIndices)
        {
            string alias = IndexDefinitions.AliasOf(kind);
            foreach (string old in await BehindAsync(alias, cancellationToken))
            {
                actions.Add($$"""{ "remove": { "index": "{{old}}", "alias": "{{alias}}" } }""");
            }

            actions.Add($$"""{ "add": { "index": "{{index}}", "alias": "{{alias}}" } }""");
        }

        using JsonDocument _ = await rest.SendAsync(
            HttpMethod.POST, "_aliases", $$"""{ "actions": [ {{string.Join(", ", actions)}} ] }""", cancellationToken);
    }

    public async Task RefreshAsync(string index, CancellationToken cancellationToken)
    {
        using JsonDocument _ = await rest.SendAsync(HttpMethod.POST, $"{index}/_refresh", null, cancellationToken);
    }

    public async Task DeleteAsync(string index, CancellationToken cancellationToken)
    {
        using JsonDocument _ = await rest.SendAsync(HttpMethod.DELETE, index, null, cancellationToken, 404);
    }

    [LoggerMessage(Level = LogLevel.Information, Message = "Created index {Index} behind alias {Alias}")]
    private static partial void LogCreated(ILogger logger, string index, string alias);
}
