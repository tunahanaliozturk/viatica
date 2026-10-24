using System.Globalization;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Time.Testing;
using Npgsql;
using Testcontainers.Redis;
using Viatica.Cars.Application;
using Viatica.Cars.Infrastructure;
using Viatica.Kernel.Web;
using Viatica.Testing;

[assembly: AssemblyFixture(typeof(PostgresFixture))]
[assembly: AssemblyFixture(typeof(KafkaFixture))]
[assembly: AssemblyFixture(typeof(Viatica.Cars.IntegrationTests.ValkeyFixture))]

// One Postgres serves every test class and allows 100 connections. The racing tests open 60 at once on purpose, so
// the classes take turns instead of sharing that budget.
[assembly: CollectionBehavior(CollectionBehavior.CollectionPerAssembly)]

namespace Viatica.Cars.IntegrationTests;

/// <summary>The same Valkey image compose runs; the repeatability records the bulk endpoint keeps live in it.</summary>
public sealed class ValkeyFixture : IAsyncLifetime
{
    private readonly RedisContainer _container = new RedisBuilder("valkey/valkey:8.1.10-alpine").Build();

    public string ConnectionString => _container.GetConnectionString();

    public async ValueTask InitializeAsync() => await _container.StartAsync();

    public async ValueTask DisposeAsync() => await _container.DisposeAsync();
}

/// <summary>
/// The real Cars host: its own database in the shared Postgres (migrated the way compose migrates it), its own pair
/// of Kafka topics and consumer group, the shared Valkey, and a clock the test moves. Nothing else is replaced.
/// </summary>
internal sealed class CarsHost : WebApplicationFactory<Program>
{
    public const string Audience = "cars-api";
    public const string ApiVersion = "2026-11-01";

    /// <summary>A Monday morning. The rentals in the tests are five weeks later.</summary>
    public static readonly DateTimeOffset Start = new(2026, 11, 2, 9, 0, 0, TimeSpan.Zero);

    // The bytes 1..32 and 101..132 in base64, as in appsettings.Development.json. Test-only, and public.
    private const string SkipTokenKey = "AQIDBAUGBwgJCgsMDQ4PEBESExQVFhcYGRobHB0eHyA=";
    private const string QuoteKey = "ZWZnaGlqa2xtbm9wcXJzdHV2d3h5ent8fX5/gIGCg4Q=";

    private readonly string _connectionString;
    private readonly string _valkey;
    private readonly string _bootstrapServers;
    private readonly TimeSpan _sweepInterval;
    private readonly string _consumerGroup = $"cars-{Guid.NewGuid():N}";
    private EventProbe? _probe;

    private CarsHost(string connectionString, string valkey, KafkaFixture kafka, string carsTopic, string bookingTopic, TimeSpan sweepInterval)
    {
        _connectionString = connectionString;
        _valkey = valkey;
        _bootstrapServers = kafka.BootstrapServers;
        _sweepInterval = sweepInterval;
        CarsTopic = carsTopic;
        BookingTopic = bookingTopic;
        Kafka = kafka;
    }

    public FakeTimeProvider Clock { get; } = new(Start);

    public string CarsTopic { get; }

    public string BookingTopic { get; }

    public KafkaFixture Kafka { get; }

    /// <summary>Reads <c>cars.v1</c> (this host's copy of it) from the beginning.</summary>
    public EventProbe Probe => _probe ??= Kafka.Probe(CarsTopic);

    /// <summary>
    /// Starts a host on a fresh database. The sweeper's interval defaults to an hour of fake time, so it never ticks
    /// by itself and tests sweep on purpose; one test shortens it to see the background loop work.
    /// </summary>
    public static async Task<CarsHost> StartAsync(
        PostgresFixture postgres, KafkaFixture kafka, ValkeyFixture valkey, TimeSpan? sweepInterval = null)
    {
        // A cap on the pool, so a test that starts hundreds of checkouts at once queues them for a connection instead of
        // asking Postgres for hundreds of its own (it allows 100 in all, and every host in the run shares them).
        string connectionString = new NpgsqlConnectionStringBuilder(await postgres.CreateDatabaseAsync("cars")) { MaxPoolSize = 40 }.ConnectionString;

        // Migrate before the host starts: its relay and consumer begin work as soon as it does.
        await using (var db = new CarsDbContext(new DbContextOptionsBuilder<CarsDbContext>().UseNpgsql(connectionString).Options))
        {
            await db.Database.MigrateAsync();
        }

        var host = new CarsHost(
            connectionString,
            valkey.ConnectionString,
            kafka,
            await kafka.CreateTopicAsync("cars"),
            await kafka.CreateTopicAsync("booking"),
            sweepInterval ?? TimeSpan.FromHours(1));
        _ = host.Services;
        return host;
    }

    public HttpClient Partner(Guid partnerId) => Client(TestTokens.Partner(Audience, partnerId));

    public HttpClient Traveller() => Client(TestTokens.Traveller(Audience));

    public HttpClient Anonymous() => Client(null);

    public HttpClient Client(string? token)
    {
        HttpClient client = CreateClient();
        if (token is not null)
        {
            client.DefaultRequestHeaders.Authorization = new("Bearer", token);
        }

        return client;
    }

    /// <summary>A scope with the service's own wiring: Relevox, the DbContext, the clock.</summary>
    public async Task<T> InScopeAsync<T>(Func<IServiceProvider, Task<T>> work)
    {
        await using AsyncServiceScope scope = Services.CreateAsyncScope();
        return await work(scope.ServiceProvider);
    }

    /// <summary>
    /// Runs <paramref name="work"/> the way the Kafka consumer runs a handler: a scope of its own, one transaction
    /// around it, one save, one commit. Many of these at once are many consumers racing for the same cars.
    /// </summary>
    public Task<T> AsConsumerAsync<T>(Func<IServiceProvider, Task<T>> work) =>
        InScopeAsync(async services =>
        {
            CarsDbContext db = services.GetRequiredService<CarsDbContext>();
            await using IDbContextTransaction transaction = await db.Database.BeginTransactionAsync();
            T result = await work(services);
            await db.SaveChangesAsync();
            await transaction.CommitAsync();
            return result;
        });

    public Task<T> QueryAsync<T>(Func<CarsDbContext, Task<T>> query) =>
        InScopeAsync(services => query(services.GetRequiredService<CarsDbContext>()));

    public HoldExpirySweeper Sweeper => Services.GetRequiredService<HoldExpirySweeper>();

    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        builder.UseEnvironment("Testing");
        builder.UseSetting("ConnectionStrings:Cars", _connectionString);
        builder.UseSetting("ConnectionStrings:Valkey", _valkey);
        builder.UseSetting("Kafka:BootstrapServers", _bootstrapServers);
        builder.UseSetting("Kafka:Topics:Cars", CarsTopic);
        builder.UseSetting("Kafka:Topics:Booking", BookingTopic);
        builder.UseSetting("Kafka:ConsumerGroup", _consumerGroup);
        builder.UseSetting("Viatica:Api:SkipTokenKey", SkipTokenKey);
        builder.UseSetting("Viatica:Quotes:SigningKey", QuoteKey);
        builder.UseSetting("Viatica:Auth:TestSigningKey", TestTokens.SigningKeyBase64);
        builder.UseSetting("Cars:HoldSweeper:Interval", _sweepInterval.ToString("c", CultureInfo.InvariantCulture));
        builder.ConfigureLogging(static logging => logging.SetMinimumLevel(LogLevel.Warning));
        builder.ConfigureTestServices(services => services.AddSingleton<TimeProvider>(Clock));
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            // Disposing a factory can run this twice (Dispose, then DisposeAsync); the probe closes once.
            Interlocked.Exchange(ref _probe, null)?.Dispose();
        }

        base.Dispose(disposing);

        // Each host has a database of its own, and so a pool of its own that nothing would otherwise close.
        Npgsql.NpgsqlConnection.ClearAllPools();
    }
}
