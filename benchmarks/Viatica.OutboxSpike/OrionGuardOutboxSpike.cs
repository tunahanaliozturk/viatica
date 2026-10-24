using System.Collections.Concurrent;
using System.Diagnostics;
using System.Text.Json;
using Confluent.Kafka;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Moongazing.OrionGuard.Domain.Events;
using Moongazing.OrionGuard.Domain.Primitives;
using Moongazing.OrionGuard.EntityFrameworkCore;
using Moongazing.OrionGuard.EntityFrameworkCore.Outbox;
using Moongazing.OrionGuard.EntityFrameworkCore.Outbox.Locking;
using Viatica.Testing;

[assembly: AssemblyFixture(typeof(PostgresFixture))]
[assembly: AssemblyFixture(typeof(KafkaFixture))]

namespace Viatica.OutboxSpike;

/// <summary>
/// The four questions ADR 0004 asks of OrionGuard.EntityFrameworkCore 7.0.0, each answered against Postgres 18
/// and, for throughput, Kafka. The facts assert what the library did; the numbers go to standard output.
/// </summary>
public sealed class OrionGuardOutboxSpike(PostgresFixture postgres, KafkaFixture kafka)
{
    private const int EventsPerLedger = 10;

    [Fact]
    public async Task RunsOnEfCore11()
    {
        Console.WriteLine($"EF Core at run time: {typeof(DbContext).Assembly.GetName().Version}");
        var recorder = new RecordingDispatcher();
        using IHost host = await StartAsync(recorder);

        await SaveLedgersAsync(host, ledgers: 1, eventsPerLedger: 1);

        await Eventually.MatchesAsync(() => Task.FromResult(recorder.Dispatched.Count), static n => n == 1);
        typeof(DbContext).Assembly.GetName().Version!.Major.ShouldBe(11);
    }

    /// <summary>Events of one aggregate raised in one SaveChanges, as a saga step would raise them.</summary>
    [Fact]
    public async Task KeepsOrderPerAggregateWithoutFailures()
    {
        var recorder = new RecordingDispatcher();
        using IHost host = await StartAsync(recorder);

        await SaveLedgersAsync(host, ledgers: 200, eventsPerLedger: EventsPerLedger);
        await Eventually.MatchesAsync(() => Task.FromResult(recorder.Dispatched.Count), static n => n == 200 * EventsPerLedger);

        int outOfOrder = recorder.LedgersOutOfOrder();
        Console.WriteLine($"No failures: {outOfOrder} of 200 aggregates dispatched out of order.");

        // The dispatcher orders by OccurredOnUtc alone. Events raised in one method call land in the same
        // microsecond, the precision Postgres keeps, and rows that tie come back in whatever order the scan finds.
        await using AsyncServiceScope scope = host.Services.CreateAsyncScope();
        List<DateTime> stamps = await scope.ServiceProvider.GetRequiredService<SpikeDb>()
            .Set<OutboxMessage>().Select(static m => m.OccurredOnUtc).ToListAsync(TestContext.Current.CancellationToken);
        Console.WriteLine($"No failures: {stamps.Count} rows carry {stamps.Distinct().Count()} distinct OccurredOnUtc values.");
        outOfOrder.ShouldBeGreaterThan(0);
    }

    /// <summary>The first event of one aggregate fails once, as a Kafka produce does when a broker restarts.</summary>
    [Fact]
    public async Task KeepsOrderPerAggregateWhenOneEventFailsOnce()
    {
        var recorder = new RecordingDispatcher { FailOnceAt = 1 };
        using IHost host = await StartAsync(recorder);

        await SaveLedgersAsync(host, ledgers: 1, eventsPerLedger: EventsPerLedger);
        await Eventually.MatchesAsync(() => Task.FromResult(recorder.Dispatched.Count), static n => n == EventsPerLedger);

        int[] order = [.. recorder.Dispatched.Select(static e => e.Sequence)];
        Console.WriteLine($"One transient failure on event 1: dispatch order {string.Join(',', order)}.");
        order.ShouldNotBe([.. Enumerable.Range(1, EventsPerLedger)], "OrionGuard moves on to the next row after a failure");
    }

    /// <summary>One dispatcher, one handler producing each event to Kafka with acks=all and idempotence.</summary>
    /// <remarks>
    /// Batch 100 and 500 measured the same (63 and 64 rows/s), so only the linger setting varies here: the handler
    /// awaits each produce, and librdkafka's default linger of 5 ms is paid on every row.
    /// </remarks>
    [Theory]
    [InlineData(100, 5)]
    [InlineData(100, 0)]
    public async Task ThroughputToKafka(int batchSize, int lingerMs)
    {
        const int total = 10_000;
        string topic = await kafka.CreateTopicAsync("spike", partitions: 12);
        using var producer = new KafkaDispatcher(kafka.BootstrapServers, topic, lingerMs);
        using IHost host = await BuildAsync(producer, batchSize);

        await SaveLedgersAsync(host, ledgers: total / EventsPerLedger, eventsPerLedger: EventsPerLedger);

        var clock = Stopwatch.StartNew();
        await host.StartAsync(TestContext.Current.CancellationToken);
        await Eventually.MatchesAsync(() => PendingAsync(host), static n => n == 0, TimeSpan.FromMinutes(5));
        clock.Stop();
        await host.StopAsync(TestContext.Current.CancellationToken);

        Console.WriteLine(
            $"OrionGuard, batch {batchSize}, linger {lingerMs} ms: {total} rows to Kafka in {clock.Elapsed.TotalSeconds:F1} s = {total / clock.Elapsed.TotalSeconds:F0} rows/s");
    }

    private async Task<IHost> StartAsync(IDomainEventDispatcher dispatcher)
    {
        IHost host = await BuildAsync(dispatcher, batchSize: 100);
        await host.StartAsync(TestContext.Current.CancellationToken);
        return host;
    }

    private async Task<IHost> BuildAsync(IDomainEventDispatcher dispatcher, int batchSize)
    {
        string connectionString = await postgres.CreateDatabaseAsync("spike");
        HostApplicationBuilder builder = Host.CreateApplicationBuilder();
        builder.Logging.ClearProviders();
        builder.Services.AddSingleton(dispatcher);
        builder.Services.AddDbContext<SpikeDb>((sp, options) => options
            .UseNpgsql(connectionString)
            .UseOrionGuardDomainEvents(sp));
        builder.Services.AddOrionGuardEfCore<SpikeDb>(options => options.UseOutbox(outbox =>
        {
            outbox.BatchSize = batchSize;
            outbox.PollingInterval = TimeSpan.FromMilliseconds(100);
        }));

        IHost host = builder.Build();
        await using AsyncServiceScope scope = host.Services.CreateAsyncScope();
        await scope.ServiceProvider.GetRequiredService<SpikeDb>().Database.EnsureCreatedAsync();
        return host;
    }

    private static async Task SaveLedgersAsync(IHost host, int ledgers, int eventsPerLedger)
    {
        // Chunks of 100 aggregates per SaveChanges keep the change tracker small while seeding thousands of rows.
        foreach (int[] chunk in Enumerable.Range(0, ledgers).Chunk(100))
        {
            await using AsyncServiceScope scope = host.Services.CreateAsyncScope();
            SpikeDb db = scope.ServiceProvider.GetRequiredService<SpikeDb>();

            foreach (int _ in chunk)
            {
                var ledger = new Ledger(Guid.CreateVersion7());
                for (int sequence = 1; sequence <= eventsPerLedger; sequence++)
                {
                    ledger.Record(sequence);
                }

                db.Ledgers.Add(ledger);
            }

            await db.SaveChangesAsync();
        }
    }

    private static async Task<int> PendingAsync(IHost host)
    {
        await using AsyncServiceScope scope = host.Services.CreateAsyncScope();
        return await scope.ServiceProvider.GetRequiredService<SpikeDb>()
            .Set<OutboxMessage>().CountAsync(static m => m.ProcessedOnUtc == null);
    }
}

public sealed class Ledger : AggregateRoot<Guid>
{
    public Ledger(Guid id)
        : base(id)
    {
    }

    public int Entries { get; private set; }

    public void Record(int sequence)
    {
        Entries++;
        RaiseEvent(new LedgerEntryRecorded(Id, sequence));
    }
}

public sealed record LedgerEntryRecorded(Guid LedgerId, int Sequence) : DomainEventBase;

public sealed class SpikeDb(DbContextOptions<SpikeDb> options) : DbContext(options)
{
    public DbSet<Ledger> Ledgers => Set<Ledger>();

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        modelBuilder.Entity<Ledger>().Ignore(static l => l.DomainEvents);
        modelBuilder.ApplyConfiguration(new OutboxMessageEntityTypeConfiguration("OrionGuard_Outbox"));
        modelBuilder.ApplyConfiguration(new OutboxLockEntityTypeConfiguration());
    }
}

internal sealed class RecordingDispatcher : IDomainEventDispatcher
{
    private int _failed;

    public int? FailOnceAt { get; init; }

    public ConcurrentQueue<LedgerEntryRecorded> Dispatched { get; } = new();

    public Task DispatchAsync(IDomainEvent @event, CancellationToken cancellationToken = default)
    {
        var entry = (LedgerEntryRecorded)@event;
        if (entry.Sequence == FailOnceAt && Interlocked.Exchange(ref _failed, 1) == 0)
        {
            throw new InvalidOperationException("Injected transient failure.");
        }

        Dispatched.Enqueue(entry);
        return Task.CompletedTask;
    }

    public async Task DispatchAsync(IEnumerable<IDomainEvent> events, CancellationToken cancellationToken = default)
    {
        foreach (IDomainEvent @event in events)
        {
            await DispatchAsync(@event, cancellationToken);
        }
    }

    public int LedgersOutOfOrder() =>
        Dispatched.GroupBy(static e => e.LedgerId)
            .Count(static g => !g.Select(static e => e.Sequence).SequenceEqual(Enumerable.Range(1, g.Count())));
}

internal sealed class KafkaDispatcher(string bootstrapServers, string topic, int lingerMs) : IDomainEventDispatcher, IDisposable
{
    private readonly IProducer<string, byte[]> _producer = new ProducerBuilder<string, byte[]>(new ProducerConfig
    {
        BootstrapServers = bootstrapServers,
        Acks = Acks.All,
        EnableIdempotence = true,
        LingerMs = lingerMs,
    }).Build();

    public async Task DispatchAsync(IDomainEvent @event, CancellationToken cancellationToken = default)
    {
        var entry = (LedgerEntryRecorded)@event;
        await _producer.ProduceAsync(
            topic,
            new Message<string, byte[]> { Key = entry.LedgerId.ToString(), Value = JsonSerializer.SerializeToUtf8Bytes(entry) },
            cancellationToken);
    }

    public async Task DispatchAsync(IEnumerable<IDomainEvent> events, CancellationToken cancellationToken = default)
    {
        foreach (IDomainEvent @event in events)
        {
            await DispatchAsync(@event, cancellationToken);
        }
    }

    public void Dispose() => _producer.Dispose();
}
