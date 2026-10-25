using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Viatica.Booking.Application;
using Viatica.Booking.Application.Integration;
using Viatica.Booking.Infrastructure.Persistence;
using Viatica.Contracts;
using Viatica.Kernel.Messaging;

namespace Viatica.Booking.Infrastructure;

public static class BookingInfrastructure
{
    public const string ConsumerGroup = "booking";

    /// <summary>
    /// Everything the use cases need from the outside: Postgres, the two personal data keys, and Kafka in both
    /// directions. A missing connection string or key stops the host at start-up, not on the first request.
    /// </summary>
    public static IServiceCollection AddBookingInfrastructure(this IServiceCollection services, IConfiguration configuration)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(configuration);

        var options = new BookingOptions();
        configuration.GetSection(BookingOptions.Section).Bind(options);
        services.TryAddSingleton(TimeProvider.System);

        string postgres = configuration.GetConnectionString("Booking")
            ?? throw new InvalidOperationException("ConnectionStrings:Booking is not set.");
        services.AddDbContextPool<BookingDbContext>(db => db.UseNpgsql(postgres));
        // The handler must get the same instance the consumer saves, or its changes miss the inbox transaction.
        services.AddScoped<IBookingDb>(sp => sp.GetRequiredService<BookingDbContext>());

        services.AddBookingApplication(
            options,
            Convert.FromBase64String(Required(configuration, "Viatica:Pii:PassengerKey")),
            Convert.FromBase64String(Required(configuration, "Viatica:Pii:StorageKey")));

        services.AddHostedService(sp => new TripCheckService(
            sp.GetRequiredService<IServiceScopeFactory>(),
            sp.GetRequiredService<ILogger<TripCheckService>>(),
            options.CheckInterval,
            options.CheckBatchSize));

        return services.AddBookingMessaging(configuration);
    }

    private static IServiceCollection AddBookingMessaging(this IServiceCollection services, IConfiguration configuration)
    {
        string bootstrap = Required(configuration, "Kafka:BootstrapServers");

        services.AddMessaging(
            new IntegrationEventTypes(ContractsJsonContext.Default, EventCatalog.TopicByType.Keys),
            messaging => messaging.BootstrapServers = bootstrap);

        // The topics are overridable so that integration tests running side by side each get their own set.
        services.AddOutboxRelay<BookingDbContext>(configuration["Kafka:BookingTopic"] ?? Topics.Booking);

        services.AddSubscription<BookingDbContext>(configuration["Kafka:StaysTopic"] ?? Topics.Stays, ConsumerGroup)
            .Handle<StayHoldPlaced, StayHoldPlacedHandler>()
            .Handle<StayHoldRejected, StayHoldRejectedHandler>()
            .Handle<StayHoldCommitted, StayHoldCommittedHandler>()
            .Handle<StayHoldReleased, StayHoldReleasedHandler>()
            .Handle<StayHoldExpired, StayHoldExpiredHandler>()
            .Handle<StayBookingCancelled, StayBookingCancelledHandler>();

        services.AddSubscription<BookingDbContext>(configuration["Kafka:CarsTopic"] ?? Topics.Cars, ConsumerGroup)
            .Handle<CarHoldPlaced, CarHoldPlacedHandler>()
            .Handle<CarHoldRejected, CarHoldRejectedHandler>()
            .Handle<CarHoldCommitted, CarHoldCommittedHandler>()
            .Handle<CarHoldReleased, CarHoldReleasedHandler>()
            .Handle<CarHoldExpired, CarHoldExpiredHandler>()
            .Handle<CarBookingCancelled, CarBookingCancelledHandler>();

        services.AddSubscription<BookingDbContext>(configuration["Kafka:FlightsTopic"] ?? Topics.Flights, ConsumerGroup)
            .Handle<FlightOrderCreated, FlightOrderCreatedHandler>()
            .Handle<FlightOrderRejected, FlightOrderRejectedHandler>()
            .Handle<FlightTicketed, FlightTicketedHandler>()
            .Handle<FlightTicketingRefused, FlightTicketingRefusedHandler>()
            .Handle<FlightOrderVoided, FlightOrderVoidedHandler>()
            .Handle<FlightOrderCancelled, FlightOrderCancelledHandler>();

        services.AddSubscription<BookingDbContext>(configuration["Kafka:PaymentsTopic"] ?? Topics.Payments, ConsumerGroup)
            .Handle<PaymentAuthorized, PaymentAuthorizedHandler>()
            .Handle<PaymentDeclined, PaymentDeclinedHandler>()
            .Handle<PaymentCaptured, PaymentCapturedHandler>()
            .Handle<PaymentVoided, PaymentVoidedHandler>()
            .Handle<PaymentRefunded, PaymentRefundedHandler>();

        return services;
    }

    private static string Required(IConfiguration configuration, string key) =>
        configuration[key] is { Length: > 0 } value ? value : throw new InvalidOperationException($"{key} is not set.");
}
