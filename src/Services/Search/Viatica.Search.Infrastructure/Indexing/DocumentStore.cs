using System.Globalization;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;
using System.Text.Json.Serialization.Metadata;
using Viatica.Search.Application.Projection;
using HttpMethod = OpenSearch.Net.HttpMethod;

namespace Viatica.Search.Infrastructure.Indexing;

[JsonSourceGenerationOptions(
    JsonSerializerDefaults.Web,
    UseStringEnumConverter = true,
    DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull)]
[JsonSerializable(typeof(StayDocument))]
[JsonSerializable(typeof(CarLocationDocument))]
internal sealed partial class IndexJsonContext : JsonSerializerContext;

/// <summary>
/// Documents in one index or alias, read with <c>_mget</c> and written with <c>_bulk</c>. Reads are real time (an
/// <c>_mget</c> sees a document the moment it is written, not after the next refresh), which is what lets one batch
/// build on the one before it.
/// </summary>
/// <remarks>
/// A write that carries a sequence number and primary term is conditional on them; a write without them uses
/// <c>create</c> and loses to any existing document. Both losses come back as 409 and are reported to the caller,
/// which reads the document again and replays its events on top.
/// </remarks>
public sealed class DocumentStore<TDocument> : IDocumentStore<TDocument>
    where TDocument : class
{
    private readonly OpenSearchRest _rest;
    private readonly string _target;
    private readonly JsonTypeInfo<TDocument> _typeInfo;

    public DocumentStore(OpenSearchRest rest, string indexOrAlias)
    {
        _rest = rest;
        _target = indexOrAlias;
        _typeInfo = (JsonTypeInfo<TDocument>)IndexJsonContext.Default.GetTypeInfo(typeof(TDocument))!;
    }

    public async Task<IReadOnlyDictionary<string, StoredDocument<TDocument>>> GetAsync(
        IReadOnlyCollection<string> ids, CancellationToken cancellationToken)
    {
        Dictionary<string, StoredDocument<TDocument>> found = new(StringComparer.Ordinal);
        if (ids.Count == 0)
        {
            return found;
        }

        string body = new JsonObject { ["ids"] = new JsonArray([.. ids.Select(static id => (JsonNode?)JsonValue.Create(id))]) }.ToJsonString();
        using JsonDocument answer = await _rest.SendAsync(HttpMethod.POST, $"{_target}/_mget", body, cancellationToken);

        foreach (JsonElement hit in answer.RootElement.GetProperty("docs").EnumerateArray())
        {
            if (!hit.TryGetProperty("found", out JsonElement isFound) || !isFound.GetBoolean())
            {
                continue;
            }

            TDocument document = hit.GetProperty("_source").Deserialize(_typeInfo)
                ?? throw new InvalidOperationException($"Document {hit.GetProperty("_id").GetString()} has no source.");
            found[hit.GetProperty("_id").GetString()!] = new StoredDocument<TDocument>(
                document, hit.GetProperty("_seq_no").GetInt64(), hit.GetProperty("_primary_term").GetInt64());
        }

        return found;
    }

    public async Task<IReadOnlyList<string>> WriteAsync(IReadOnlyList<DocumentWrite<TDocument>> writes, CancellationToken cancellationToken)
    {
        if (writes.Count == 0)
        {
            return [];
        }

        var lines = new List<string>(writes.Count * 2);
        foreach (DocumentWrite<TDocument> write in writes)
        {
            string meta = write.SeqNo is { } seqNo && write.PrimaryTerm is { } term
                ? $$"""{ "index": { "_index": "{{_target}}", "_id": "{{write.Id}}", "if_seq_no": {{seqNo.ToString(CultureInfo.InvariantCulture)}}, "if_primary_term": {{term.ToString(CultureInfo.InvariantCulture)}} } }"""
                : $$"""{ "create": { "_index": "{{_target}}", "_id": "{{write.Id}}" } }""";
            lines.Add(meta);
            lines.Add(JsonSerializer.Serialize(write.Document, _typeInfo));
        }

        using JsonDocument answer = await _rest.BulkAsync(lines, cancellationToken);
        if (!answer.RootElement.GetProperty("errors").GetBoolean())
        {
            return [];
        }

        List<string> conflicts = [];
        foreach (JsonElement item in answer.RootElement.GetProperty("items").EnumerateArray())
        {
            JsonElement result = item.EnumerateObject().First().Value;
            int status = result.GetProperty("status").GetInt32();
            if (status is >= 200 and < 300)
            {
                continue;
            }

            if (status == 409)
            {
                conflicts.Add(result.GetProperty("_id").GetString()!);
                continue;
            }

            string reason = result.TryGetProperty("error", out JsonElement error) ? error.ToString() : "no reason given";
            throw new OpenSearchRequestException($"Writing {result.GetProperty("_id").GetString()} answered {status}: {reason}", status);
        }

        return conflicts;
    }
}
