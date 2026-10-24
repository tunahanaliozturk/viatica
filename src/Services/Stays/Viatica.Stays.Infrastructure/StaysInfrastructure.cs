using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Viatica.Contracts;
using Viatica.Kernel.Messaging;
using Viatica.Stays.Application;
using Viatica.Stays.Application.Features.Holds;

namespace Viatica.Stays.Infrastructure;

public static class StaysInfrastructure
{
    /// <summary>
    /// The database, the outbox relay that publishes <c>stays.v1</c>, the consumer of Booking's hold requests, and
    /// the expiry sweeper. Topic names come from <c>Kafka:Topics</c> and default to the catalogue's, so a test host
    /// can read and write topics of its own.
    /// </summary>
    public static IServiceCollection AddStaysInfrastructure(this IServiceCollection services, IConfiguration configuration)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(configuration);

        string connectionString = configuration.GetConnectionString("Stays")
            ?? throw new InvalidOperationException("ConnectionStrings:Stays is not set.");
        string bootstrapServers = configuration["Kafka:BootstrapServers"]
            ?? throw new InvalidOperationException("Kafka:BootstrapServers is not set.");
        string staysTopic = configuration["Kafka:Topics:Stays"] ?? Topics.Stays;
        string bookingTopic = configuration["Kafka:Topics:Booking"] ?? Topics.Booking;
        TimeSpan sweepInterval = configuration.GetValue("Stays:HoldSweepInterval", TimeSpan.FromSeconds(5));
        const int sweepBatch = 100;

        services.AddDbContextPool<StaysDbContext>(options => options.UseNpgsql(
            connectionString,
            npgsql => npgsql.MigrationsHistoryTable("__ef_migrations_history", StaysDbContext.Schema)));

        // The consumer saves the DbContext it resolves, and the handlers must change that same instance, or their
        // changes would miss the inbox transaction. A scoped forward gives both the one instance of the scope.
        services.AddScoped<IStaysDb>(static sp => sp.GetRequiredService<StaysDbContext>());

        services.AddMessaging(
            new IntegrationEventTypes(ContractsJsonContext.Default, EventCatalog.TopicByType.Keys),
            options => options.BootstrapServers = bootstrapServers);
        services.AddOutboxRelay<StaysDbContext>(staysTopic);

        services.AddSubscription<StaysDbContext>(bookingTopic, consumerGroup: "stays")
            .Handle<StayHoldRequested, PlaceHoldHandler>()
            .Handle<StayHoldCommitRequested, CommitHoldHandler>()
            .Handle<StayHoldReleaseRequested, ReleaseHoldHandler>()
            .Handle<StayBookingCancelRequested, CancelBookingHandler>();

        services.AddSingleton<IHostedService>(sp =>
            new HoldSweeper(sp.GetRequiredService<IServiceScopeFactory>(), sp.GetRequiredService<ILogger<HoldSweeper>>(), sweepInterval, sweepBatch));

        return services;
    }
}
