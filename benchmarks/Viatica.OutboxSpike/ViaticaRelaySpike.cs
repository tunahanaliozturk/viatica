using System.Diagnostics;
using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Viatica.Contracts;
using Viatica.Kernel.Messaging;
using Viatica.Testing;

namespace Viatica.OutboxSpike;

/// <summary>
/// The same measurement as <see cref="OrionGuardOutboxSpike.ThroughputToKafka"/>, against Viatica's own relay:
/// 10,000 rows already in the outbox, a 12 partition topic, acks=all and idempotence, clock running from relay
/// start until the outbox is empty.
/// </summary>
public sealed class ViaticaRelaySpike(PostgresFixture postgres, KafkaFixture kafka)
{
    private static readonly IntegrationEventTypes Types = new(SpikeEventsJsonContext.Default, [typeof(EntryRecorded)]);

    /// <summary>
    /// Ten events per key is the shape a saga produces; one per key is the best case for a relay that sends a
    /// key's rows one after another.
    /// </summary>
    [Theory]
    [InlineData(10, 1)]
    [InlineData(1, 1)]
    [InlineData(10, 2)]
    public async Task ThroughputToKafka(int eventsPerKey, int relays)
    {
        const int total = 10_000;
        string connectionString = await postgres.CreateDatabaseAsync("relay");
        string topic = await kafka.CreateTopicAsync("relay", partitions: 12);
        IHost[] hosts = [.. Enumerable.Range(0, relays).Select(_ => Build(connectionString, topic))];

        await using (AsyncServiceScope scope = hosts[0].Services.CreateAsyncScope())
        {
            await scope.ServiceProvider.GetRequiredService<RelaySpikeDb>().Database.EnsureCreatedAsync(TestContext.Current.CancellationToken);
        }

        foreach (int[] chunk in Enumerable.Range(0, total).Chunk(1000))
        {
            await using AsyncServiceScope scope = hosts[0].Services.CreateAsyncScope();
            RelaySpikeDb db = scope.ServiceProvider.GetRequiredService<RelaySpikeDb>();
            foreach (int n in chunk)
            {
                db.AddIntegrationEvent(new EntryRecorded($"ledger-{n / eventsPerKey}", n % eventsPerKey));
            }

            await db.SaveChangesAsync(TestContext.Current.CancellationToken);
        }

        var clock = Stopwatch.StartNew();
        await Task.WhenAll(hosts.Select(static h => h.StartAsync(TestContext.Current.CancellationToken)));
        await Eventually.MatchesAsync(() => PendingAsync(hosts[0]), static n => n == 0, TimeSpan.FromMinutes(5));
        clock.Stop();

        foreach (IHost host in hosts)
        {
            await host.StopAsync(TestContext.Current.CancellationToken);
            host.Dispose();
        }

        Console.WriteLine(
            $"Viatica relay, {eventsPerKey} event(s) per key, {relays} relay(s): {total} rows to Kafka in {clock.Elapsed.TotalSeconds:F1} s = {total / clock.Elapsed.TotalSeconds:F0} rows/s");
    }

    /// <summary>
    /// What the per key lock costs a writer (ADR 0007): 8 writers, each saving 500 requests, one event per request.
    /// A bare event is the worst case, because EF Core saves a single statement without a transaction. With a state
    /// change next to it, as a command handler writes, the save has a transaction either way. With distinct keys the
    /// writers never wait for each other; with one shared key every save queues behind the previous transaction.
    /// </summary>
    [Theory]
    [InlineData(false, false)]
    [InlineData(false, true)]
    [InlineData(true, true)]
    public async Task WriteCost(bool sharedKey, bool withStateChange)
    {
        const int writers = 8;
        const int savesPerWriter = 500;
        string connectionString = await postgres.CreateDatabaseAsync("write");
        using IHost host = Build(connectionString, "unused");

        await using (AsyncServiceScope scope = host.Services.CreateAsyncScope())
        {
            await scope.ServiceProvider.GetRequiredService<RelaySpikeDb>().Database.EnsureCreatedAsync(TestContext.Current.CancellationToken);
        }

        // The first pass pays for JIT and for opening the connections; only the second is timed.
        Stopwatch clock = Stopwatch.StartNew();
        for (int pass = 0; pass < 2; pass++)
        {
            clock.Restart();
            await Task.WhenAll(Enumerable.Range(0, writers).Select(async writer =>
            {
                for (int n = 0; n < savesPerWriter; n++)
                {
                    await using AsyncServiceScope scope = host.Services.CreateAsyncScope();
                    RelaySpikeDb db = scope.ServiceProvider.GetRequiredService<RelaySpikeDb>();
                    string key = sharedKey ? "ledger" : $"ledger-{pass}-{writer}-{n}";
                    if (withStateChange)
                    {
                        db.Entries.Add(new LedgerEntry { LedgerId = key, Position = n });
                    }

                    db.AddIntegrationEvent(new EntryRecorded(key, n));
                    await db.SaveChangesAsync(TestContext.Current.CancellationToken);
                }
            }));
        }

        clock.Stop();

        int saves = writers * savesPerWriter;
        Console.WriteLine(
            $"Viatica outbox writes, {(sharedKey ? "one shared key" : "distinct keys")}, {(withStateChange ? "with a state change" : "event only")}: {saves} saves by {writers} writers in {clock.Elapsed.TotalSeconds:F1} s = {saves / clock.Elapsed.TotalSeconds:F0} saves/s");
    }

    private IHost Build(string connectionString, string topic)
    {
        HostApplicationBuilder builder = Host.CreateApplicationBuilder();
        builder.Logging.ClearProviders();
        builder.Services.AddDbContext<RelaySpikeDb>(options => options.UseNpgsql(connectionString));
        builder.Services.AddMessaging(Types, options => options.BootstrapServers = kafka.BootstrapServers);
        builder.Services.AddOutboxRelay<RelaySpikeDb>(topic);
        return builder.Build();
    }

    private static async Task<int> PendingAsync(IHost host)
    {
        await using AsyncServiceScope scope = host.Services.CreateAsyncScope();
        return await scope.ServiceProvider.GetRequiredService<RelaySpikeDb>().Set<OutboxMessage>().CountAsync();
    }
}

public sealed record EntryRecorded(string LedgerId, int Sequence) : IIntegrationEvent
{
    string IIntegrationEvent.PartitionKey => LedgerId;
}

[JsonSourceGenerationOptions(JsonSerializerDefaults.Web)]
[JsonSerializable(typeof(EntryRecorded))]
internal sealed partial class SpikeEventsJsonContext : JsonSerializerContext;

public sealed class RelaySpikeDb(DbContextOptions<RelaySpikeDb> options) : DbContext(options)
{
    public DbSet<LedgerEntry> Entries => Set<LedgerEntry>();

    protected override void OnModelCreating(ModelBuilder modelBuilder) => modelBuilder.AddMessaging();
}

public sealed class LedgerEntry
{
    public long Id { get; set; }

    public required string LedgerId { get; set; }

    public int Position { get; set; }
}
