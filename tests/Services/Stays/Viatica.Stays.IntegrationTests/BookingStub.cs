using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Viatica.Contracts;
using Viatica.Kernel.Messaging;
using Viatica.Testing;

namespace Viatica.Stays.IntegrationTests;

/// <summary>
/// Booking as far as Stays can tell: something that writes its instructions to <c>booking.v1</c> through an outbox,
/// so Stays receives real envelopes from a real topic and answers on <c>stays.v1</c>. The saga itself is not here.
/// </summary>
internal sealed class BookingStub : IAsyncDisposable
{
    private readonly IHost _host;

    private BookingStub(IHost host) => _host = host;

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

        IHost host = builder.Build();
        await using (AsyncServiceScope scope = host.Services.CreateAsyncScope())
        {
            await scope.ServiceProvider.GetRequiredService<BookingStubDb>().Database.EnsureCreatedAsync();
        }

        await host.StartAsync();
        return new BookingStub(host);
    }

    /// <summary>Queues the events and saves once, as a saga step would.</summary>
    public async Task SendAsync(params IIntegrationEvent[] events)
    {
        await using AsyncServiceScope scope = _host.Services.CreateAsyncScope();
        BookingStubDb db = scope.ServiceProvider.GetRequiredService<BookingStubDb>();
        foreach (IIntegrationEvent message in events)
        {
            db.AddIntegrationEvent(message);
        }

        await db.SaveChangesAsync();
    }

    public async ValueTask DisposeAsync()
    {
        await _host.StopAsync();
        _host.Dispose();
    }
}

internal sealed class BookingStubDb(DbContextOptions<BookingStubDb> options) : DbContext(options)
{
    protected override void OnModelCreating(ModelBuilder modelBuilder) => modelBuilder.AddMessaging();
}
