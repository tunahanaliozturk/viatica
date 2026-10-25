using System.Text;
using System.Text.Json;
using Confluent.Kafka;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Npgsql;
using Viatica.Contracts;
using Viatica.Kernel.Messaging;
using Viatica.Testing;

namespace Viatica.Payments.IntegrationTests;

/// <summary>
/// Booking as far as Payments can tell: something that writes its instructions to <c>booking.v1</c> through an outbox,
/// so Payments receives real envelopes from a real topic and answers on <c>payments.v1</c>. The saga itself is not here.
/// </summary>
internal sealed class BookingStub : IAsyncDisposable
{
    private readonly IHost host;
    private readonly string connection;

    private BookingStub(IHost host, string connection)
    {
        this.host = host;
        this.connection = connection;
    }

    public static async Task<BookingStub> StartAsync(PostgresFixture postgres, KafkaFixture kafka, string bookingTopic)
    {
        string connection = await postgres.CreateDatabaseAsync("booking");

        HostApplicationBuilder builder = Host.CreateApplicationBuilder();
        builder.Logging.ClearProviders();
        builder.Services.AddDbContextPool<BookingStubDb>(options => options.UseNpgsql(connection));
        builder.Services.AddMessaging(
            new IntegrationEventTypes(ContractsJsonContext.Default, EventCatalog.TopicByType.Keys),
            options => options.BootstrapServers = kafka.BootstrapServers);
        builder.Services.AddOutboxRelay<BookingStubDb>(bookingTopic, options => options.PollInterval = TimeSpan.FromMilliseconds(50));

        IHost stub = builder.Build();
        await using (AsyncServiceScope scope = stub.Services.CreateAsyncScope())
        {
            await scope.ServiceProvider.GetRequiredService<BookingStubDb>().Database.EnsureCreatedAsync();
        }

        await stub.StartAsync();
        return new BookingStub(stub, connection);
    }

    /// <summary>
    /// Queues the events and saves once, as a saga step would. Every event gets a new event id, so sending the same
    /// request twice is a redelivery under a new id, which the inbox cannot catch and the operation record must.
    /// </summary>
    public async Task SendAsync(params IIntegrationEvent[] events)
    {
        await using AsyncServiceScope scope = host.Services.CreateAsyncScope();
        BookingStubDb db = scope.ServiceProvider.GetRequiredService<BookingStubDb>();
        foreach (IIntegrationEvent message in events)
        {
            db.AddIntegrationEvent(message);
        }

        await db.SaveChangesAsync();
    }

    public async ValueTask DisposeAsync()
    {
        await host.StopAsync();
        host.Dispose();
        using var pooled = new NpgsqlConnection(connection);
        NpgsqlConnection.ClearPool(pooled);
    }
}

internal sealed class BookingStubDb(DbContextOptions<BookingStubDb> options) : DbContext(options)
{
    protected override void OnModelCreating(ModelBuilder modelBuilder) => modelBuilder.AddMessaging();
}

/// <summary>
/// Puts an event on <c>booking.v1</c> in the envelope Kernel.Messaging writes, with an event id the test chooses. The
/// stub above mints a new id for every event; this is for the one case that needs the same id twice, which is Kafka
/// delivering a message again.
/// </summary>
internal sealed class RawBookingSender : IDisposable
{
    private readonly IProducer<string, byte[]> producer;
    private readonly string topic;

    public RawBookingSender(string bootstrapServers, string topic)
    {
        this.topic = topic;
        producer = new ProducerBuilder<string, byte[]>(new ProducerConfig { BootstrapServers = bootstrapServers, Acks = Acks.All, EnableIdempotence = true }).Build();
    }

    public async Task SendAsync(IIntegrationEvent message, Guid eventId)
    {
        string type = message.GetType().Name;
        string data = JsonSerializer.Serialize(message, message.GetType(), ContractsJsonContext.Default);
        string envelope = $$"""{"id":"{{eventId}}","type":"{{type}}","version":1,"occurredAt":"{{DateTimeOffset.UtcNow:O}}","traceparent":null,"data":{{data}}}""";

        await producer.ProduceAsync(topic, new Message<string, byte[]> { Key = message.PartitionKey, Value = Encoding.UTF8.GetBytes(envelope) });
    }

    public void Dispose()
    {
        producer.Flush(TimeSpan.FromSeconds(5));
        producer.Dispose();
    }
}
