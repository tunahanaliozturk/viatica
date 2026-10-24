using Confluent.Kafka;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Hosting;
using Viatica.Contracts;

namespace Viatica.Kernel.Messaging;

/// <summary>Wiring for a service host: Kafka, the outbox relay, and one hosted consumer per subscription.</summary>
public static class MessagingServiceCollectionExtensions
{
    /// <summary>
    /// Registers the event types and one Kafka producer for the process, shared by the outbox relay and the dead
    /// letter path. Call once, before <see cref="AddOutboxRelay{TDbContext}"/> and
    /// <see cref="AddSubscription{TDbContext}"/>.
    /// </summary>
    public static IServiceCollection AddMessaging(
        this IServiceCollection services,
        IntegrationEventTypes eventTypes,
        Action<MessagingOptions> configure)
    {
        ArgumentNullException.ThrowIfNull(eventTypes);
        ArgumentNullException.ThrowIfNull(configure);

        var options = new MessagingOptions();
        configure(options);

        services.TryAddSingleton(options);
        services.TryAddSingleton(eventTypes);
        // acks=all waits for every in-sync replica, so an acknowledged row cannot be lost to one broker dying, and
        // idempotence stops the producer's own retries from writing a message twice or out of order.
        services.TryAddSingleton<IProducer<string, byte[]>>(_ => new ProducerBuilder<string, byte[]>(new ProducerConfig
        {
            BootstrapServers = options.BootstrapServers,
            Acks = Acks.All,
            EnableIdempotence = true,
        }).Build());

        return services;
    }

    /// <summary>
    /// Runs an <see cref="OutboxRelay{TDbContext}"/> that sends the outbox of <typeparamref name="TDbContext"/> to
    /// <paramref name="topic"/>. Every instance of the service may run one.
    /// </summary>
    public static IServiceCollection AddOutboxRelay<TDbContext>(
        this IServiceCollection services,
        string topic,
        Action<OutboxRelayOptions>? configure = null)
        where TDbContext : DbContext
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(topic);

        var options = new OutboxRelayOptions { Topic = topic };
        configure?.Invoke(options);

        // Registered by factory, not AddHostedService: that deduplicates by implementation type, and the type alone
        // does not say which options it runs with.
        services.AddSingleton<IHostedService>(sp => ActivatorUtilities.CreateInstance<OutboxRelay<TDbContext>>(sp, options));
        return services;
    }

    /// <summary>
    /// Starts a hosted consumer reading <paramref name="topic"/> as <paramref name="consumerGroup"/>. Add a handler
    /// per event type with <see cref="SubscriptionBuilder.Handle{TEvent, THandler}"/>; other types on the topic are
    /// skipped.
    /// </summary>
    public static SubscriptionBuilder AddSubscription<TDbContext>(
        this IServiceCollection services,
        string topic,
        string consumerGroup,
        Action<SubscriptionOptions>? configure = null)
        where TDbContext : DbContext
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(topic);
        ArgumentException.ThrowIfNullOrWhiteSpace(consumerGroup);

        var options = new SubscriptionOptions { Topic = topic, ConsumerGroup = consumerGroup };
        configure?.Invoke(options);

        var builder = new SubscriptionBuilder(services);
        services.AddSingleton<IHostedService>(sp => new SubscriptionConsumer<TDbContext>(options, builder.Handlers, sp));
        return builder;
    }
}

/// <summary>Adds handlers to one subscription.</summary>
public sealed class SubscriptionBuilder
{
    private readonly IServiceCollection _services;

    internal SubscriptionBuilder(IServiceCollection services) => _services = services;

    internal Dictionary<Type, HandleEvent> Handlers { get; } = [];

    /// <summary>Hands every <typeparamref name="TEvent"/> on the topic to a scoped <typeparamref name="THandler"/>.</summary>
    public SubscriptionBuilder Handle<TEvent, THandler>()
        where TEvent : IIntegrationEvent
        where THandler : class, IIntegrationEventHandler<TEvent>
    {
        _services.TryAddScoped<THandler>();
        Handlers[typeof(TEvent)] = static (sp, message, ct) => sp.GetRequiredService<THandler>().HandleAsync((TEvent)message, ct);
        return this;
    }
}
