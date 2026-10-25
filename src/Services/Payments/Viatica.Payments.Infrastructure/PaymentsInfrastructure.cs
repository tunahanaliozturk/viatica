using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Viatica.Contracts;
using Viatica.Kernel.Messaging;
using Viatica.Payments.Application;
using Viatica.Payments.Application.Operations;
using Viatica.Payments.Application.Provider;
using Viatica.Payments.Infrastructure.Persistence;
using Viatica.Payments.Infrastructure.Provider;

namespace Viatica.Payments.Infrastructure;

public static class PaymentsInfrastructure
{
    public const string ConsumerGroup = "payments";

    /// <summary>
    /// Everything the use cases need from the outside: Postgres, the payment provider, and Kafka in both directions. A
    /// missing connection string or provider address stops the host at start-up, not on the first request.
    /// </summary>
    public static IServiceCollection AddPaymentsInfrastructure(this IServiceCollection services, IConfiguration configuration)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(configuration);

        services.TryAddSingleton(TimeProvider.System);

        string postgres = configuration.GetConnectionString("Payments")
            ?? throw new InvalidOperationException("ConnectionStrings:Payments is not set.");
        services.AddDbContextPool<PaymentsDbContext>(db => db.UseNpgsql(postgres));
        // The handler must get the same instance the consumer saves, or its changes miss the inbox transaction.
        services.AddScoped<IPaymentsDb>(sp => sp.GetRequiredService<PaymentsDbContext>());
        services.AddSingleton<ProviderSettlement>();
        services.AddScoped<OperationDesk>();

        services.AddPspClient(configuration);
        services.AddSingleton<PspGateway>();
        services.AddSingleton<IPaymentProvider>(sp => sp.GetRequiredService<PspGateway>());

        return services.AddPaymentsMessaging(configuration);
    }

    private static IServiceCollection AddPaymentsMessaging(this IServiceCollection services, IConfiguration configuration)
    {
        string bootstrap = configuration["Kafka:BootstrapServers"] is { Length: > 0 } servers
            ? servers
            : throw new InvalidOperationException("Kafka:BootstrapServers is not set.");

        services.AddMessaging(
            new IntegrationEventTypes(ContractsJsonContext.Default, EventCatalog.TopicByType.Keys),
            messaging => messaging.BootstrapServers = bootstrap);

        // The topics are overridable so that integration tests running side by side each get their own pair.
        services.AddOutboxRelay<PaymentsDbContext>(configuration["Kafka:PaymentsTopic"] ?? Topics.Payments);

        // A capture, void or refund the provider cannot settle is tried again rather than decided, and the provider
        // can be down for as long as its circuit stays open. The default five attempts over a few seconds would park
        // the message on the dead letter topic before the circuit closed. Eight attempts with the delay doubling from
        // half a second to at most twenty seconds hold the partition for about a minute (0.5 + 1 + 2 + 4 + 8 + 16 +
        // 20), which outlasts a 15 second break and a lookup timeout. Only this trip's partition waits.
        IConfigurationSection consumer = configuration.GetSection("Payments:Consumer");
        services.AddSubscription<PaymentsDbContext>(
                configuration["Kafka:BookingTopic"] ?? Topics.Booking,
                ConsumerGroup,
                options =>
                {
                    options.MaxAttempts = consumer.GetValue("MaxAttempts", 8);
                    options.RetryDelay = consumer.GetValue("RetryDelay", TimeSpan.FromMilliseconds(500));
                    options.MaxRetryDelay = consumer.GetValue("MaxRetryDelay", TimeSpan.FromSeconds(20));
                })
            .Handle<PaymentAuthorizationRequested, PaymentAuthorizationRequestedHandler>()
            .Handle<PaymentCaptureRequested, PaymentCaptureRequestedHandler>()
            .Handle<PaymentVoidRequested, PaymentVoidRequestedHandler>()
            .Handle<PaymentRefundRequested, PaymentRefundRequestedHandler>();

        return services;
    }
}
