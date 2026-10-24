using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Design;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Viatica.Cars.Application;
using Viatica.Cars.Application.Integration;
using Viatica.Contracts;
using Viatica.Kernel.Messaging;

namespace Viatica.Cars.Infrastructure;

public static class CarsInfrastructure
{
    /// <summary>
    /// The database, the outbox relay for <c>cars.v1</c>, the subscription to Booking's requests on
    /// <c>booking.v1</c>, and the hold expiry sweeper. Reads <c>ConnectionStrings:Cars</c> and
    /// <c>Kafka:BootstrapServers</c>. The topics and the consumer group are the platform's (<c>cars.v1</c>,
    /// <c>booking.v1</c>, <c>cars</c>); <c>Kafka:Topics:Cars</c>, <c>Kafka:Topics:Booking</c> and
    /// <c>Kafka:ConsumerGroup</c> exist so a test host can have a broker corner to itself.
    /// </summary>
    public static IServiceCollection AddCarsInfrastructure(this IServiceCollection services, IConfiguration configuration)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(configuration);

        string connectionString = configuration.GetConnectionString("Cars")
            ?? throw new InvalidOperationException("ConnectionStrings:Cars is not set.");
        string bootstrapServers = configuration["Kafka:BootstrapServers"]
            ?? throw new InvalidOperationException("Kafka:BootstrapServers is not set.");

        string carsTopic = configuration["Kafka:Topics:Cars"] ?? Topics.Cars;
        string bookingTopic = configuration["Kafka:Topics:Booking"] ?? Topics.Booking;
        string consumerGroup = configuration["Kafka:ConsumerGroup"] ?? "cars";

        services.AddDbContextPool<CarsDbContext>(options => options
            .UseNpgsql(connectionString)
            // A concurrency conflict is an expected outcome that the Api turns into 412 or the consumer retries;
            // EF Core would log every one at Error.
            .ConfigureWarnings(static warnings => warnings.Log((CoreEventId.SaveChangesFailed, LogLevel.Debug))));

        // The consumer saves the DbContext it resolved, so the handlers must be given that same instance: otherwise
        // their changes would miss the transaction that carries the inbox row.
        services.AddScoped<ICarsDb>(static sp => sp.GetRequiredService<CarsDbContext>());

        services.AddMessaging(
            new IntegrationEventTypes(ContractsJsonContext.Default, EventCatalog.TopicByType.Keys),
            options => options.BootstrapServers = bootstrapServers);
        services.AddOutboxRelay<CarsDbContext>(carsTopic);
        services.AddSubscription<CarsDbContext>(bookingTopic, consumerGroup)
            .Handle<CarHoldRequested, CarHoldRequestedHandler>()
            .Handle<CarHoldCommitRequested, CarHoldCommitRequestedHandler>()
            .Handle<CarHoldReleaseRequested, CarHoldReleaseRequestedHandler>()
            .Handle<CarBookingCancelRequested, CarBookingCancelRequestedHandler>();

        services.AddOptions<HoldSweeperOptions>().BindConfiguration(HoldSweeperOptions.Section);
        services.AddSingleton<HoldExpirySweeper>();
        services.AddHostedService(static sp => sp.GetRequiredService<HoldExpirySweeper>());

        return services;
    }
}

/// <summary>Lets <c>dotnet ef</c> build the model without a running host.</summary>
internal sealed class CarsDesignTimeFactory : IDesignTimeDbContextFactory<CarsDbContext>
{
    public CarsDbContext CreateDbContext(string[] args) =>
        new(new DbContextOptionsBuilder<CarsDbContext>()
            .UseNpgsql("Host=localhost;Port=5450;Database=cars;Username=cars;Password=cars")
            .Options);
}
