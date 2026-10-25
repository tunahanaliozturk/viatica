using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Time.Testing;
using Npgsql;
using Viatica.Stays.Application;
using Viatica.Stays.Domain;
using Viatica.Stays.Infrastructure;
using Viatica.Testing;

namespace Viatica.Stays.IntegrationTests;

/// <summary>
/// The real Stays host on its own database, its own pair of topics, a Valkey, and a clock the test moves. Everything
/// else is as in production: migrations applied by the real migration, the outbox relay, the consumer of Booking's
/// topic, the expiry sweeper.
/// </summary>
internal sealed class StaysHost : WebApplicationFactory<Program>
{
    public const string Version = "2026-11-01";

    /// <summary>A Monday morning. Stays in the tests start a few weeks later.</summary>
    public static readonly DateTimeOffset Start = new(2026, 11, 2, 8, 0, 0, TimeSpan.Zero);

    public static readonly DateOnly CheckIn = new(2026, 12, 1);

    public static readonly DateOnly CheckOut = new(2026, 12, 4);

    private static readonly byte[] QuoteKeyBytes = RandomNumberGenerator.GetBytes(32);

    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web)
    {
        Converters = { new JsonStringEnumConverter() },
    };

    private readonly string _connectionString;
    private readonly string _valkey;
    private readonly string _bootstrapServers;
    private readonly TimeSpan _sweepInterval;
    private HttpClient? _client;

    private StaysHost(string connectionString, string valkey, string bootstrapServers, string staysTopic, string bookingTopic, TimeSpan sweepInterval)
    {
        _connectionString = connectionString;
        _valkey = valkey;
        _bootstrapServers = bootstrapServers;
        StaysTopic = staysTopic;
        BookingTopic = bookingTopic;
        _sweepInterval = sweepInterval;
    }

    public string StaysTopic { get; }

    public string BookingTopic { get; }

    public string ConnectionString => _connectionString;

    public FakeTimeProvider Clock { get; } = new(Start);

    public QuoteTokens Tokens { get; } = new(QuoteKeyBytes);

    public HttpClient Client => _client ??= CreateClient();

    /// <summary>
    /// Starts a host with its schema in place. The sweeper runs on the real clock every <paramref name="sweepInterval"/>
    /// but judges expiry on <see cref="Clock"/>, so moving the clock makes holds due and the next tick expires them.
    /// </summary>
    public static async Task<StaysHost> StartAsync(
        PostgresFixture postgres, KafkaFixture kafka, ValkeyFixture valkey, TimeSpan? sweepInterval = null, int maxPool = 20)
    {
        string connection = new NpgsqlConnectionStringBuilder(await postgres.CreateDatabaseAsync("stays")) { MaxPoolSize = maxPool }.ConnectionString;

        await using (var migrator = new StaysDbContext(new DbContextOptionsBuilder<StaysDbContext>().UseNpgsql(connection).Options))
        {
            await migrator.Database.MigrateAsync();
        }

        var host = new StaysHost(
            connection,
            valkey.ConnectionString,
            kafka.BootstrapServers,
            await kafka.CreateTopicAsync("stays"),
            await kafka.CreateTopicAsync("booking"),
            sweepInterval ?? TimeSpan.FromMilliseconds(200));
        _ = host.Server;
        return host;
    }

    public async Task InDbAsync(Func<StaysDbContext, Task> work)
    {
        await using AsyncServiceScope scope = Services.CreateAsyncScope();
        await work(scope.ServiceProvider.GetRequiredService<StaysDbContext>());
    }

    public async Task<T> InDbAsync<T>(Func<StaysDbContext, Task<T>> work)
    {
        await using AsyncServiceScope scope = Services.CreateAsyncScope();
        return await work(scope.ServiceProvider.GetRequiredService<StaysDbContext>());
    }

    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        builder.UseEnvironment("Testing");
        builder.UseSetting("ConnectionStrings:Stays", _connectionString);
        builder.UseSetting("ConnectionStrings:Valkey", _valkey);
        builder.UseSetting("Kafka:BootstrapServers", _bootstrapServers);
        builder.UseSetting("Kafka:Topics:Stays", StaysTopic);
        builder.UseSetting("Kafka:Topics:Booking", BookingTopic);
        builder.UseSetting("Stays:QuoteKey", Convert.ToBase64String(QuoteKeyBytes));
        builder.UseSetting("Stays:HoldSweepInterval", _sweepInterval.ToString());
        builder.UseSetting("Viatica:Api:SkipTokenKey", Convert.ToBase64String(RandomNumberGenerator.GetBytes(32)));
        builder.UseSetting("Viatica:Auth:TestSigningKey", TestTokens.SigningKeyBase64);
        builder.ConfigureLogging(static logging => logging.SetMinimumLevel(LogLevel.Warning));
        builder.ConfigureTestServices(services => services.AddSingleton<TimeProvider>(Clock));
    }

    protected override void Dispose(bool disposing)
    {
        _client?.Dispose();
        base.Dispose(disposing);

        // Each host has a database of its own, and so a pool of its own that nothing would otherwise close. Writers of
        // one key now wait on each other's transactions (ADR 0007), which grows these pools; left open they add up past
        // the server's 100 connections over the run. Cleared per pool, as Cars clears all of them.
        using var connection = new NpgsqlConnection(_connectionString);
        NpgsqlConnection.ClearPool(connection);
    }

    // ------------------------------------------------------------------------------------------------------------
    // Calling the API the way a client does.

    public const string Audience = "stays-api";

    public static string PartnerToken(Guid partnerId) => TestTokens.Partner(Audience, partnerId);

    public static string AdminToken() => TestTokens.Admin(Audience);

    public static string TravellerToken() => TestTokens.Traveller(Audience);

    public Task<HttpResponseMessage> SendAsync(
        HttpMethod method, string path, object? body = null, string? token = null, string? ifMatch = null,
        IDictionary<string, string>? headers = null, bool withVersion = true)
    {
        string url = withVersion ? path + (path.Contains('?', StringComparison.Ordinal) ? "&" : "?") + "api-version=" + Version : path;
        var request = new HttpRequestMessage(method, url);

        if (body is not null)
        {
            request.Content = JsonContent.Create(body, options: JsonOptions);
        }

        if (token is not null)
        {
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        }

        if (ifMatch is not null)
        {
            request.Headers.TryAddWithoutValidation("If-Match", ifMatch);
        }

        foreach ((string name, string value) in headers ?? new Dictionary<string, string>())
        {
            request.Headers.TryAddWithoutValidation(name, value);
        }

        return Client.SendAsync(request);
    }

    public Task<HttpResponseMessage> GetAsync(string path, string? token = null) => SendAsync(HttpMethod.Get, path, token: token);

    public static async Task<T> ReadAsync<T>(HttpResponseMessage response)
    {
        string text = await response.Content.ReadAsStringAsync();
        return JsonSerializer.Deserialize<T>(text, JsonOptions) ?? throw new InvalidOperationException($"Empty body: {text}");
    }

    public static async Task<JsonElement> JsonAsync(HttpResponseMessage response)
    {
        using JsonDocument document = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        return document.RootElement.Clone();
    }

    /// <summary>The error envelope's top-level <c>code</c>, after checking that there is an envelope.</summary>
    public static async Task<string> ErrorCodeAsync(HttpResponseMessage response)
    {
        JsonElement root = await JsonAsync(response);
        return root.GetProperty("error").GetProperty("code").GetString()!;
    }

    public static string ETagOf(HttpResponseMessage response) =>
        response.Headers.ETag?.ToString() ?? throw new InvalidOperationException("The response has no ETag.");

    public static Dictionary<string, string> RepeatabilityHeaders(Guid id, DateTimeOffset firstSent) => new()
    {
        ["Repeatability-Request-ID"] = id.ToString(),
        ["Repeatability-First-Sent"] = firstSent.ToString("R", System.Globalization.CultureInfo.InvariantCulture),
    };

    public static string Json(object value) => JsonSerializer.Serialize(value, JsonOptions);
}
