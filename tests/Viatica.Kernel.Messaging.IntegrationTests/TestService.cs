using System.Collections.Concurrent;
using System.Diagnostics;
using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Viatica.Contracts;
using Viatica.Testing;

[assembly: AssemblyFixture(typeof(PostgresFixture))]
[assembly: AssemblyFixture(typeof(KafkaFixture))]

namespace Viatica.Kernel.Messaging.IntegrationTests;

// Stand-ins for the real contracts. PartitionKey is implemented explicitly, which keeps it out of the JSON: the key
// travels as the Kafka key, not inside the data.
public sealed record ThingHappened(string Key, int Sequence) : IIntegrationEvent
{
    string IIntegrationEvent.PartitionKey => Key;
}

public sealed record PoisonPill(string Key) : IIntegrationEvent
{
    string IIntegrationEvent.PartitionKey => Key;
}

[JsonSourceGenerationOptions(JsonSerializerDefaults.Web)]
[JsonSerializable(typeof(ThingHappened))]
[JsonSerializable(typeof(PoisonPill))]
internal sealed partial class TestEventsJsonContext : JsonSerializerContext;

/// <summary>A service's own DbContext: its state (here, a log of applied events) plus the outbox and the inbox.</summary>
internal sealed class TestDb(DbContextOptions<TestDb> options) : DbContext(options)
{
    public DbSet<AppliedEvent> Applied => Set<AppliedEvent>();

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        modelBuilder.AddMessaging();
        modelBuilder.Entity<AppliedEvent>().ToTable("applied_events");
    }
}

internal sealed class AppliedEvent
{
    public long Id { get; set; }

    public required string Key { get; set; }

    public int Sequence { get; set; }

    public string? TraceId { get; set; }
}

/// <summary>Applies <see cref="ThingHappened"/> by writing a row; the consumer commits it with the inbox row.</summary>
internal sealed class RecordingHandler(TestDb db) : IIntegrationEventHandler<ThingHappened>
{
    public Task HandleAsync(ThingHappened message, CancellationToken cancellationToken)
    {
        db.Applied.Add(new AppliedEvent { Key = message.Key, Sequence = message.Sequence, TraceId = Activity.Current?.TraceId.ToString() });
        return Task.CompletedTask;
    }
}

/// <summary>Fails every time, and counts how often it was asked.</summary>
internal sealed class ExplodingHandler(Attempts attempts) : IIntegrationEventHandler<PoisonPill>
{
    public Task HandleAsync(PoisonPill message, CancellationToken cancellationToken)
    {
        attempts.Count.AddOrUpdate(message.Key, 1, static (_, n) => n + 1);
        throw new InvalidOperationException("This event cannot be applied.");
    }
}

internal sealed class Attempts
{
    public ConcurrentDictionary<string, int> Count { get; } = new();
}

/// <summary>One instance of the service under test: DbContext, messaging, and whichever halves the test wants.</summary>
internal static class TestService
{
    public static readonly IntegrationEventTypes Types = new(TestEventsJsonContext.Default, [typeof(ThingHappened), typeof(PoisonPill)]);

    public static async Task<IHost> StartAsync(
        string connectionString,
        KafkaFixture kafka,
        string topic,
        bool relay = true,
        string? consumerGroup = null,
        Attempts? attempts = null)
    {
        IHost host = Build(connectionString, kafka, topic, relay, consumerGroup, attempts);
        await host.StartAsync();
        return host;
    }

    public static IHost Build(
        string connectionString,
        KafkaFixture kafka,
        string topic,
        bool relay = true,
        string? consumerGroup = null,
        Attempts? attempts = null)
    {
        HostApplicationBuilder builder = Host.CreateApplicationBuilder();
        builder.Logging.ClearProviders();
        builder.Services.AddSingleton(attempts ?? new Attempts());
        builder.Services.AddDbContext<TestDb>(options => options.UseNpgsql(connectionString));
        builder.Services.AddMessaging(Types, options => options.BootstrapServers = kafka.BootstrapServers);

        if (relay)
        {
            builder.Services.AddOutboxRelay<TestDb>(topic, options => options.PollInterval = TimeSpan.FromMilliseconds(50));
        }

        if (consumerGroup is not null)
        {
            builder.Services
                .AddSubscription<TestDb>(topic, consumerGroup, options =>
                {
                    options.MaxAttempts = 3;
                    options.RetryDelay = TimeSpan.FromMilliseconds(50);
                })
                .Handle<ThingHappened, RecordingHandler>()
                .Handle<PoisonPill, ExplodingHandler>();
        }

        return builder.Build();
    }

    /// <summary>Creates the tables in the class's database. Later calls find them and do nothing.</summary>
    public static async Task EnsureSchemaAsync(PostgresDatabase database)
    {
        await using var db = new TestDb(new DbContextOptionsBuilder<TestDb>().UseNpgsql(database.ConnectionString).Options);
        await db.Database.EnsureCreatedAsync();
    }

    public static async Task<T> QueryAsync<T>(IHost host, Func<TestDb, Task<T>> query)
    {
        await using AsyncServiceScope scope = host.Services.CreateAsyncScope();
        return await query(scope.ServiceProvider.GetRequiredService<TestDb>());
    }

    /// <summary>Saves events the way a command handler would: queued on the context, written by its SaveChanges.</summary>
    public static async Task PublishAsync(IHost host, params IIntegrationEvent[] events)
    {
        await using AsyncServiceScope scope = host.Services.CreateAsyncScope();
        TestDb db = scope.ServiceProvider.GetRequiredService<TestDb>();
        foreach (IIntegrationEvent @event in events)
        {
            db.AddIntegrationEvent(@event);
        }

        await db.SaveChangesAsync();
    }

    public static Task<List<AppliedEvent>> AppliedAsync(IHost host, string key) =>
        QueryAsync(host, db => db.Applied.AsNoTracking().Where(a => a.Key == key).OrderBy(static a => a.Id).ToListAsync());
}
