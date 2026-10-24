using System.Diagnostics.Metrics;
using System.Globalization;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Nodes;
using Confluent.Kafka;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using OpenSearch.Client;
using OpenSearch.Net;
using Viatica.Contracts;
using Viatica.Search.Application;
using Viatica.Search.Application.Projection;
using Viatica.Search.Infrastructure.Indexing;
using Viatica.Search.Infrastructure.Messaging;
using Viatica.Testing;

[assembly: AssemblyFixture(typeof(OpenSearchFixture))]
[assembly: AssemblyFixture(typeof(KafkaFixture))]

// Every test class works on the indices called stays and cars, so two classes must never run at once.
[assembly: CollectionBehavior(CollectionBehavior.CollectionPerAssembly)]

namespace Viatica.Search.IntegrationTests;

/// <summary>
/// The real Search host on an in-process server, wired to the assembly's OpenSearch and Kafka, with topics and
/// indices of its own for the test class. Events can go in two ways: through Kafka and the running consumers, as in
/// production, or straight into the projection when the test is about what a search answers and not about delivery.
/// </summary>
public sealed class SearchApp(OpenSearchFixture opensearch, KafkaFixture kafka) : IAsyncLifetime
{
    public const string ApiVersion = "2026-11-01";

    private SearchFactory? _factory;
    private OpenSearchClient? _client;
    private IProducer<string, byte[]>? _producer;

    /// <summary>A date far enough ahead that no test has to think about today.</summary>
    public static DateOnly Base { get; } = DateOnly.FromDateTime(DateTime.UtcNow).AddDays(30);

    public KafkaFixture Kafka => kafka;

    public string StaysTopic { get; private set; } = "";

    public string CarsTopic { get; private set; } = "";

    public HttpClient Http { get; private set; } = null!;

    public IServiceProvider Services => _factory!.Services;

    public OpenSearchRest Rest { get; private set; } = null!;

    public IndexAdmin Admin { get; private set; } = null!;

    public async ValueTask InitializeAsync()
    {
        await opensearch.ResetAsync();
        StaysTopic = await kafka.CreateTopicAsync("stays", partitions: 3);
        CarsTopic = await kafka.CreateTopicAsync("cars", partitions: 3);

        _client = new OpenSearchClient(new ConnectionSettings(new SingleNodeConnectionPool(opensearch.Uri)));
        Rest = new OpenSearchRest(_client.LowLevel);
        Admin = new IndexAdmin(Rest, TimeProvider.System, new LoggerFactory().CreateLogger<IndexAdmin>());

        // The migrate step. It runs before the host does, as compose runs it before the service.
        await Admin.EnsureAsync(CancellationToken.None);

        _factory = new SearchFactory(opensearch.Uri, kafka.BootstrapServers, StaysTopic, CarsTopic);
        Http = _factory.CreateClient();
    }

    public async ValueTask DisposeAsync()
    {
        _producer?.Dispose();
        Http?.Dispose();
        if (_factory is not null)
        {
            await _factory.DisposeAsync();
        }
    }

    /// <summary>Empties both indices, so each test starts without the previous one's properties.</summary>
    public async Task ResetAsync()
    {
        foreach (string alias in (string[])[IndexDefinitions.Stays, IndexDefinitions.Cars])
        {
            using JsonDocument _ = await Rest.SendAsync(
                OpenSearch.Net.HttpMethod.POST, $"{alias}/_delete_by_query", """{ "query": { "match_all": {} } }""", CancellationToken.None);
        }

        await RefreshAsync();
    }

    public T Service<T>()
        where T : notnull =>
        Services.GetRequiredService<T>();

    /// <summary>Publishes events to the topic of their family, the way Stays and Cars would.</summary>
    public Task PublishAsync(params IIntegrationEvent[] events) => PublishAsync(occurredAt: null, events);

    /// <summary>As <see cref="PublishAsync(IIntegrationEvent[])"/>, with the time the events claim to have happened at.</summary>
    public async Task PublishAsync(DateTimeOffset? occurredAt, params IIntegrationEvent[] events)
    {
        foreach (IIntegrationEvent message in events)
        {
            string topic = EventCatalog.TopicByType[message.GetType()] == Topics.Stays ? StaysTopic : CarsTopic;
            await Producer.ProduceAsync(topic, new Message<string, byte[]> { Key = message.PartitionKey, Value = Envelope(message, occurredAt) });
        }
    }

    // One producer for the class: starting an idempotent producer costs a broker round trip, and a test that
    // publishes a few hundred events one call at a time would otherwise pay it every time.
    private IProducer<string, byte[]> Producer => _producer ??= new ProducerBuilder<string, byte[]>(new ProducerConfig
    {
        BootstrapServers = kafka.BootstrapServers,
        EnableIdempotence = true,
        Acks = Acks.All,
    }).Build();

    /// <summary>Puts raw bytes on a topic, for the message that is not an envelope at all.</summary>
    public async Task PublishRawAsync(string topic, string key, byte[] value)
    {
        using IProducer<string, byte[]> producer = new ProducerBuilder<string, byte[]>(new ProducerConfig { BootstrapServers = kafka.BootstrapServers }).Build();
        await producer.ProduceAsync(topic, new Message<string, byte[]> { Key = key, Value = value });
    }

    public static byte[] Envelope(IIntegrationEvent message, DateTimeOffset? occurredAt = null, Guid? id = null) =>
        System.Text.Encoding.UTF8.GetBytes(new JsonObject
        {
            ["id"] = (id ?? Guid.CreateVersion7()).ToString(),
            ["type"] = message.GetType().Name,
            ["version"] = 1,
            ["occurredAt"] = (occurredAt ?? DateTimeOffset.UtcNow).ToString("O", CultureInfo.InvariantCulture),
            ["traceparent"] = null,
            ["data"] = JsonNode.Parse(JsonSerializer.Serialize(message, message.GetType(), ContractsJsonContext.Default)),
        }.ToJsonString());

    /// <summary>
    /// Applies events straight to the live aliases through the same projection the consumers use, then makes them
    /// searchable at once. For tests about what a search answers.
    /// </summary>
    public async Task SeedAsync(params IIntegrationEvent[] events)
    {
        var writer = new ProjectionWriter(
            new DocumentStore<StayDocument>(Rest, IndexDefinitions.Stays),
            new DocumentStore<CarLocationDocument>(Rest, IndexDefinitions.Cars),
            TimeProvider.System,
            measureLag: false);
        await writer.ApplyAsync([.. events.Select(static e => new ProjectionEvent(Guid.CreateVersion7(), DateTimeOffset.UtcNow, e))], CancellationToken.None);
        await RefreshAsync();
    }

    public async Task RefreshAsync()
    {
        using JsonDocument stays = await Rest.SendAsync(OpenSearch.Net.HttpMethod.POST, "stays/_refresh", null, CancellationToken.None);
        using JsonDocument cars = await Rest.SendAsync(OpenSearch.Net.HttpMethod.POST, "cars/_refresh", null, CancellationToken.None);
    }

    /// <summary>The stored document of a property, straight from the index (real time, no refresh needed).</summary>
    public async Task<JsonElement?> StayDocumentAsync(Guid propertyId)
    {
        using JsonDocument answer = await Rest.SendAsync(OpenSearch.Net.HttpMethod.GET, $"stays/_doc/{propertyId}", null, CancellationToken.None, 404);
        return answer.RootElement.TryGetProperty("found", out JsonElement found) && found.GetBoolean()
            ? answer.RootElement.GetProperty("_source").Clone()
            : null;
    }

    public async Task<(int Status, JsonElement Body)> GetAsync(string pathAndQuery)
    {
        string separator = pathAndQuery.Contains('?', StringComparison.Ordinal) ? "&" : "?";
        using HttpResponseMessage response = await Http.GetAsync(new Uri($"{pathAndQuery}{separator}api-version={ApiVersion}", UriKind.Relative));
        string text = await response.Content.ReadAsStringAsync();
        using JsonDocument body = JsonDocument.Parse(text.Length == 0 ? "{}" : text);
        return ((int)response.StatusCode, body.RootElement.Clone());
    }

    /// <summary>Follows a <c>nextLink</c> exactly as returned.</summary>
    public async Task<(int Status, JsonElement Body)> FollowAsync(string link)
    {
        using HttpResponseMessage response = await Http.GetAsync(new Uri(link));
        using JsonDocument body = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        return ((int)response.StatusCode, body.RootElement.Clone());
    }

    private sealed class SearchFactory(Uri openSearch, string kafkaServers, string staysTopic, string carsTopic) : WebApplicationFactory<Program>
    {
        protected override void ConfigureWebHost(IWebHostBuilder builder)
        {
            builder.UseSetting("OpenSearch:Uri", openSearch.ToString());
            builder.UseSetting("Kafka:BootstrapServers", kafkaServers);
            builder.UseSetting("Search:Messaging:StaysTopic", staysTopic);
            builder.UseSetting("Search:Messaging:CarsTopic", carsTopic);
            builder.UseSetting("Search:Messaging:ConsumerGroup", $"search-{Guid.NewGuid():N}");
            builder.UseSetting("Search:Messaging:PollTimeout", "00:00:00.100");
            builder.UseSetting("Viatica:Auth:TestSigningKey", TestTokens.SigningKeyBase64);
            builder.ConfigureLogging(static logging => logging.SetMinimumLevel(Microsoft.Extensions.Logging.LogLevel.Warning));
        }
    }
}

/// <summary>Counts what the service reports through its meter, so a test can say "two events were dropped".</summary>
public sealed class MetricRecorder : IDisposable
{
    private readonly MeterListener _listener = new();
    private readonly Lock _gate = new();
    private readonly Dictionary<string, long> _counts = [];
    private readonly List<double> _lags = [];

    public MetricRecorder()
    {
        _listener.InstrumentPublished = static (instrument, listener) =>
        {
            if (instrument.Meter.Name == SearchTelemetry.Name)
            {
                listener.EnableMeasurementEvents(instrument);
            }
        };
        _listener.SetMeasurementEventCallback<long>(OnCount);
        _listener.SetMeasurementEventCallback<double>(OnLag);
        _listener.Start();
    }

    /// <summary>How many events ended in <paramref name="outcome"/> (applied, dropped, ignored, dead_lettered).</summary>
    public long Count(string outcome)
    {
        lock (_gate)
        {
            return _counts.GetValueOrDefault(outcome);
        }
    }

    public IReadOnlyList<double> Lags
    {
        get
        {
            lock (_gate)
            {
                return [.. _lags];
            }
        }
    }

    public void Dispose() => _listener.Dispose();

    private void OnCount(Instrument instrument, long value, ReadOnlySpan<KeyValuePair<string, object?>> tags, object? state)
    {
        if (instrument.Name != "viatica.search.events")
        {
            return;
        }

        foreach (KeyValuePair<string, object?> tag in tags)
        {
            if (tag.Key == "outcome")
            {
                lock (_gate)
                {
                    _counts[(string)tag.Value!] = _counts.GetValueOrDefault((string)tag.Value!) + value;
                }
            }
        }
    }

    private void OnLag(Instrument instrument, double value, ReadOnlySpan<KeyValuePair<string, object?>> tags, object? state)
    {
        if (instrument.Name == "viatica.search.projection.lag")
        {
            lock (_gate)
            {
                _lags.Add(value);
            }
        }
    }
}
