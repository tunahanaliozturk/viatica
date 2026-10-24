using System.Globalization;
using System.Security.Cryptography;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Http;
using Microsoft.Extensions.Logging;
using Viatica.Flights.Infrastructure.Persistence;
using Viatica.Flights.Infrastructure.Suppliers;
using Viatica.Kernel.Pii;
using Viatica.Testing;

[assembly: AssemblyFixture(typeof(PostgresFixture))]
[assembly: AssemblyFixture(typeof(KafkaFixture))]
[assembly: AssemblyFixture(typeof(ValkeyFixture))]

namespace Viatica.Flights.IntegrationTests;

/// <summary>What a test class changes about the way its Flights host is set up.</summary>
internal sealed record HostSettings
{
    /// <summary>Longest any airline call except a search may take. A test about a timed out creation makes it short.</summary>
    public string RequestTimeout { get; init; } = "00:00:08";

    /// <summary>
    /// High by default, so the breaker never opens under a test that switches an airline off and on: its window is 30
    /// seconds long and spans tests. The one test class about the breaker lowers it.
    /// </summary>
    public int BreakerMinimumThroughput { get; init; } = 1000;

    public string BreakDuration { get; init; } = "00:00:30";
}

/// <summary>
/// The real Flights service: its own Postgres database (migrated by the service's own migration), Valkey, Kafka
/// topics of its own, and the four airlines reached over HTTP. Every call that leaves for an airline is counted.
/// </summary>
internal sealed class FlightsHost : WebApplicationFactory<Program>
{
    public const string ApiVersion = "2026-11-01";

    private readonly Dictionary<string, string?> settings;
    private readonly SimHost sim;

    private FlightsHost(Dictionary<string, string?> settings, SimHost sim, WireLog wire, CapturedLogs logs)
    {
        this.settings = settings;
        this.sim = sim;
        Wire = wire;
        Logs = logs;
    }

    /// <summary>Every request that actually reached an airline, so a test can prove a call was or was not made.</summary>
    public WireLog Wire { get; }

    public CapturedLogs Logs { get; }

    public byte[] PassengerKey { get; } = RandomNumberGenerator.GetBytes(PiiSealer.KeyLength);

    public byte[] StorageKey { get; } = RandomNumberGenerator.GetBytes(PiiSealer.KeyLength);

    public string BookingTopic => settings["Kafka:BookingTopic"]!;

    public string FlightsTopic => settings["Kafka:FlightsTopic"]!;

    public string PostgresConnection => settings["ConnectionStrings:Flights"]!;

    public static async Task<FlightsHost> StartAsync(
        PostgresFixture postgres, KafkaFixture kafka, ValkeyFixture valkey, SimHost sim, HostSettings? host = null)
    {
        host ??= new HostSettings();
        string connection = await postgres.CreateDatabaseAsync("flights");

        // The schema is the service's own migration, applied before the host starts so its consumers find the tables.
        await using (var db = new FlightsDbContext(new DbContextOptionsBuilder<FlightsDbContext>().UseNpgsql(connection).Options))
        {
            await db.Database.MigrateAsync();
        }

        var settings = new Dictionary<string, string?>
        {
            ["ConnectionStrings:Flights"] = connection,
            ["ConnectionStrings:Valkey"] = valkey.ConnectionString,
            ["Kafka:BootstrapServers"] = kafka.BootstrapServers,
            ["Kafka:BookingTopic"] = await kafka.CreateTopicAsync("booking"),
            ["Kafka:FlightsTopic"] = await kafka.CreateTopicAsync("flights"),
            ["Viatica:Api:SupportedApiVersions:0"] = ApiVersion,
            ["Viatica:Api:SkipTokenKey"] = Convert.ToBase64String(RandomNumberGenerator.GetBytes(32)),
            ["Viatica:Auth:TestSigningKey"] = TestTokens.SigningKeyBase64,
            ["Flights:Suppliers:RequestTimeout"] = host.RequestTimeout,
            ["Flights:Suppliers:CircuitBreaker:MinimumThroughput"] = host.BreakerMinimumThroughput.ToString(CultureInfo.InvariantCulture),
            ["Flights:Suppliers:CircuitBreaker:BreakDuration"] = host.BreakDuration,
        };

        for (int i = 0; i < SimHost.Airlines.Length; i++)
        {
            settings[$"Flights:Suppliers:Airlines:{i}:Code"] = SimHost.Airlines[i];
            settings[$"Flights:Suppliers:Airlines:{i}:BaseUrl"] = $"http://sim.test/ndc/{SimHost.Airlines[i]}/";
        }

        var flights = new FlightsHost(settings, sim, new WireLog(), new CapturedLogs());
        settings["Viatica:Pii:PassengerKey"] = Convert.ToBase64String(flights.PassengerKey);
        settings["Viatica:Pii:StorageKey"] = Convert.ToBase64String(flights.StorageKey);

        _ = flights.Services; // starts the host, and with it the outbox relay and the consumer
        return flights;
    }

    public PiiSealer BrokerSealer => new(PassengerKey);

    public PiiSealer StorageSealer => new(StorageKey);

    public async Task<T> InDbAsync<T>(Func<FlightsDbContext, Task<T>> work)
    {
        await using AsyncServiceScope scope = Services.CreateAsyncScope();
        return await work(scope.ServiceProvider.GetRequiredService<FlightsDbContext>());
    }

    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        builder.UseEnvironment("Testing");
        foreach ((string key, string? value) in settings)
        {
            builder.UseSetting(key, value);
        }

        builder.ConfigureLogging(logging =>
        {
            logging.ClearProviders();
            logging.AddProvider(Logs);
            // Everything, from every category: the log test must see what a debugging session would see.
            logging.SetMinimumLevel(LogLevel.Trace);
            logging.AddFilter<CapturedLogs>(null, LogLevel.Trace);
        });

        builder.ConfigureTestServices(services =>
        {
            foreach (string airline in SimHost.Airlines)
            {
                services.Configure<HttpClientFactoryOptions>(AirlineClients.NameOf(airline), options =>
                    options.HttpMessageHandlerBuilderActions.Add(handler =>
                        handler.PrimaryHandler = new CountingHandler(airline, Wire, sim.CreateAirlineHandler())));
            }
        });
    }
}

/// <summary>One request that reached an airline.</summary>
internal sealed record WireEntry(string Airline, HttpMethod Method, string Path, string Query);

internal sealed class WireLog
{
    private readonly List<WireEntry> entries = [];

    public void Add(WireEntry entry)
    {
        lock (entries)
        {
            entries.Add(entry);
        }
    }

    public int Count(string? airline = null, HttpMethod? method = null, string? pathEndsWith = null)
    {
        lock (entries)
        {
            return entries.Count(e =>
                (airline is null || e.Airline == airline)
                && (method is null || e.Method == method)
                && (pathEndsWith is null || e.Path.EndsWith(pathEndsWith, StringComparison.Ordinal)));
        }
    }

    public int Total => Count();
}

/// <summary>Counts what goes out, then hands it to the sim. It sits innermost, below the resilience pipeline, so a call the breaker refuses is not counted: it never left.</summary>
internal sealed class CountingHandler(string airline, WireLog wire, HttpMessageHandler inner) : DelegatingHandler(inner)
{
    protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        wire.Add(new WireEntry(airline, request.Method, request.RequestUri!.AbsolutePath, request.RequestUri.Query));
        return base.SendAsync(request, cancellationToken);
    }
}
