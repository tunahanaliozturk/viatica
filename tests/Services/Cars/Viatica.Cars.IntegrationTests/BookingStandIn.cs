using System.Globalization;
using System.Text;
using System.Text.Json;
using Confluent.Kafka;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Viatica.Contracts;
using Viatica.Kernel.Messaging;
using Viatica.Testing;

namespace Viatica.Cars.IntegrationTests;

internal sealed class StandInDb(DbContextOptions<StandInDb> options) : DbContext(options)
{
    protected override void OnModelCreating(ModelBuilder modelBuilder) => modelBuilder.AddMessaging();
}

/// <summary>
/// Plays Booking's side of the conversation: it sends Cars' requests the way Booking will, through a real outbox and
/// relay onto the booking topic, so Cars' consumer, inbox and handlers are exercised end to end.
/// </summary>
internal sealed class BookingStandIn : IAsyncDisposable
{
    private readonly IHost _host;
    private readonly KafkaFixture _kafka;
    private readonly string _topic;

    private BookingStandIn(IHost host, KafkaFixture kafka, string topic)
    {
        _host = host;
        _kafka = kafka;
        _topic = topic;
    }

    public static async Task<BookingStandIn> StartAsync(PostgresFixture postgres, KafkaFixture kafka, string bookingTopic)
    {
        string connectionString = await postgres.CreateDatabaseAsync("booking");
        await using (var db = new StandInDb(new DbContextOptionsBuilder<StandInDb>().UseNpgsql(connectionString).Options))
        {
            await db.Database.EnsureCreatedAsync();
        }

        HostApplicationBuilder builder = Host.CreateApplicationBuilder();
        builder.Logging.ClearProviders();
        builder.Logging.SetMinimumLevel(LogLevel.Warning);
        builder.Services.AddDbContextPool<StandInDb>(options => options.UseNpgsql(connectionString));
        builder.Services.AddMessaging(
            new IntegrationEventTypes(ContractsJsonContext.Default, EventCatalog.TopicByType.Keys),
            options => options.BootstrapServers = kafka.BootstrapServers);
        builder.Services.AddOutboxRelay<StandInDb>(bookingTopic, options => options.PollInterval = TimeSpan.FromMilliseconds(50));

        IHost host = builder.Build();
        await host.StartAsync();
        return new BookingStandIn(host, kafka, bookingTopic);
    }

    public async Task SendAsync(params IIntegrationEvent[] events)
    {
        await using AsyncServiceScope scope = _host.Services.CreateAsyncScope();
        StandInDb db = scope.ServiceProvider.GetRequiredService<StandInDb>();
        foreach (IIntegrationEvent @event in events)
        {
            db.AddIntegrationEvent(@event);
        }

        await db.SaveChangesAsync();
    }

    /// <summary>
    /// Writes one request to the topic by hand, in the envelope, so the very same message (same event id) can be
    /// delivered twice, which an outbox never does on purpose and a broker sometimes does.
    /// </summary>
    public async Task DeliverTwiceAsync(CarHoldRequested request)
    {
        string envelope = JsonSerializer.Serialize(new Dictionary<string, object?>
        {
            ["id"] = Guid.CreateVersion7(),
            ["type"] = nameof(CarHoldRequested),
            ["version"] = 1,
            ["occurredAt"] = DateTimeOffset.UtcNow.ToString("O", CultureInfo.InvariantCulture),
            ["traceparent"] = null,
            ["data"] = new Dictionary<string, object?>
            {
                ["tripId"] = request.TripId,
                ["itemId"] = request.ItemId,
                ["holdId"] = request.HoldId,
                ["quoteToken"] = request.QuoteToken,
                ["driverAge"] = request.DriverAge,
            },
        });

        using IProducer<string, byte[]> producer = new ProducerBuilder<string, byte[]>(new ProducerConfig
        {
            BootstrapServers = _kafka.BootstrapServers,
            Acks = Acks.All,
        }).Build();

        for (int i = 0; i < 2; i++)
        {
            await producer.ProduceAsync(_topic, new Message<string, byte[]> { Key = request.PartitionKey, Value = Encoding.UTF8.GetBytes(envelope) });
        }
    }

    public async ValueTask DisposeAsync()
    {
        await _host.StopAsync();
        _host.Dispose();
    }
}
