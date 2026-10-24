using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using StackExchange.Redis;
using Viatica.Contracts;
using Viatica.Flights.Application;
using Viatica.Flights.Application.Offers;
using Viatica.Flights.Application.Orders;
using Viatica.Flights.Application.Suppliers;
using Viatica.Flights.Infrastructure.Cache;
using Viatica.Flights.Infrastructure.Persistence;
using Viatica.Flights.Infrastructure.Suppliers;
using Viatica.Kernel.Messaging;
using Viatica.Kernel.Pii;

namespace Viatica.Flights.Infrastructure;

public static class FlightsInfrastructure
{
    public const string ConsumerGroup = "flights";

    /// <summary>
    /// Everything the use cases need from the outside: Postgres, Valkey, the airlines, the passenger keys, and Kafka
    /// in both directions. A missing connection string or key stops the host at start-up, not on the first request.
    /// </summary>
    public static IServiceCollection AddFlightsInfrastructure(this IServiceCollection services, IConfiguration configuration)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(configuration);

        var options = new FlightsOptions();
        configuration.GetSection(FlightsOptions.Section).Bind(options);
        services.AddSingleton(options);
        services.TryAddSingleton(TimeProvider.System);

        string postgres = configuration.GetConnectionString("Flights")
            ?? throw new InvalidOperationException("ConnectionStrings:Flights is not set.");
        services.AddDbContextPool<FlightsDbContext>(db => db.UseNpgsql(postgres));
        // The handler must get the same instance the consumer saves, or its changes miss the inbox transaction.
        services.AddScoped<IFlightsDb>(sp => sp.GetRequiredService<FlightsDbContext>());

        string valkey = configuration.GetConnectionString("Valkey")
            ?? throw new InvalidOperationException("ConnectionStrings:Valkey is not set.");
        services.AddSingleton<IConnectionMultiplexer>(_ =>
        {
            ConfigurationOptions redis = ConfigurationOptions.Parse(valkey);
            // The service starts without Valkey and reports not ready until it is there; searches fail meanwhile.
            redis.AbortOnConnectFail = false;
            return ConnectionMultiplexer.Connect(redis);
        });
        services.AddSingleton<IOfferStore, ValkeyOfferStore>();

        services.AddAirlineClients(configuration);
        services.AddSingleton<AirlineGateway>();
        services.AddSingleton<IAirlineShopping>(sp => sp.GetRequiredService<AirlineGateway>());
        services.AddSingleton<IAirlineOrdering>(sp => sp.GetRequiredService<AirlineGateway>());

        services.AddSingleton(new PassengerProtector(
            PiiSealer.FromBase64(Required(configuration, "Viatica:Pii:PassengerKey")),
            PiiSealer.FromBase64(Required(configuration, "Viatica:Pii:StorageKey"))));

        return services.AddFlightsMessaging(configuration);
    }

    private static IServiceCollection AddFlightsMessaging(this IServiceCollection services, IConfiguration configuration)
    {
        string bootstrap = Required(configuration, "Kafka:BootstrapServers");

        services.AddMessaging(
            new IntegrationEventTypes(ContractsJsonContext.Default, EventCatalog.TopicByType.Keys),
            messaging => messaging.BootstrapServers = bootstrap);

        // The topics are overridable so that integration tests running side by side each get their own pair.
        services.AddOutboxRelay<FlightsDbContext>(configuration["Kafka:FlightsTopic"] ?? Topics.Flights);

        services.AddSubscription<FlightsDbContext>(configuration["Kafka:BookingTopic"] ?? Topics.Booking, ConsumerGroup)
            .Handle<FlightOrderRequested, FlightOrderRequestedHandler>()
            .Handle<FlightTicketingRequested, FlightTicketingRequestedHandler>()
            .Handle<FlightOrderVoidRequested, FlightOrderVoidRequestedHandler>()
            .Handle<FlightOrderCancelRequested, FlightOrderCancelRequestedHandler>();

        return services;
    }

    private static string Required(IConfiguration configuration, string key) =>
        configuration[key] is { Length: > 0 } value ? value : throw new InvalidOperationException($"{key} is not set.");
}
