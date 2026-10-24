using Confluent.Kafka;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using OpenSearch.Client;
using OpenSearch.Net;
using Viatica.Contracts;
using Viatica.Kernel.Messaging;
using Viatica.Search.Application.Cars;
using Viatica.Search.Application.Places;
using Viatica.Search.Application.Projection;
using Viatica.Search.Application.Stays;
using Viatica.Search.Infrastructure.Indexing;
using Viatica.Search.Infrastructure.Messaging;
using Viatica.Search.Infrastructure.Places;
using Viatica.Search.Infrastructure.Querying;

namespace Viatica.Search.Infrastructure;

public static class SearchInfrastructure
{
    /// <summary>
    /// Registers OpenSearch, the three search indexes, the airport list and the Kafka plumbing. Settings:
    /// <c>OpenSearch:Uri</c>, <c>Kafka:BootstrapServers</c> and, optionally, <c>Search:Messaging</c>.
    /// </summary>
    /// <param name="runConsumers">
    /// False for a host that only answers queries or only runs a one-shot command; true starts one consumer per topic.
    /// </param>
    public static IServiceCollection AddSearchInfrastructure(
        this IServiceCollection services, IConfiguration configuration, bool runConsumers = true)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(configuration);

        services.TryAddSingleton(TimeProvider.System);
        var uri = new Uri(configuration["OpenSearch:Uri"] ?? throw new InvalidOperationException("OpenSearch:Uri is not set."));
        services.AddSingleton(new OpenSearchClient(
            new ConnectionSettings(new SingleNodeConnectionPool(uri))
                // A search that has not answered in this long has lost to the next request already.
                .RequestTimeout(TimeSpan.FromSeconds(15))));
        services.AddSingleton(static sp => sp.GetRequiredService<OpenSearchClient>().LowLevel);
        services.AddSingleton<OpenSearchRest>();
        services.AddSingleton<IndexAdmin>();

        services.AddSingleton<IStaySearchIndex, StaySearchIndex>();
        services.AddSingleton<ICarSearchIndex, CarSearchIndex>();
        services.AddSingleton<IPlaceIndex, PlaceIndex>();
        services.AddSingleton<IAirportDirectory, AirportDirectory>();

        var messaging = new SearchMessagingOptions { BootstrapServers = configuration["Kafka:BootstrapServers"] ?? "" };
        configuration.GetSection(SearchMessagingOptions.Section).Bind(messaging);
        services.AddSingleton(messaging);
        services.AddMessaging(
            new IntegrationEventTypes(ContractsJsonContext.Default, EventCatalog.TopicByType.Keys),
            options => options.BootstrapServers = messaging.BootstrapServers);
        services.AddSingleton<RebuildRunner>();

        if (runConsumers)
        {
            // Both consumers write through the aliases, so a rebuild switching them over never reaches the consumers.
            services.AddSingleton(sp => new ProjectionWriter(
                new DocumentStore<StayDocument>(sp.GetRequiredService<OpenSearchRest>(), IndexDefinitions.Stays),
                new DocumentStore<CarLocationDocument>(sp.GetRequiredService<OpenSearchRest>(), IndexDefinitions.Cars),
                sp.GetRequiredService<TimeProvider>(),
                measureLag: true));
            services.AddSingleton<IHostedService>(sp => Consumer(sp, sp.GetRequiredService<SearchMessagingOptions>().StaysTopic));
            services.AddSingleton<IHostedService>(sp => Consumer(sp, sp.GetRequiredService<SearchMessagingOptions>().CarsTopic));
        }

        return services;
    }

    private static ProjectionConsumer Consumer(IServiceProvider sp, string topic)
    {
        SearchMessagingOptions options = sp.GetRequiredService<SearchMessagingOptions>();
        ILoggerFactory loggers = sp.GetRequiredService<ILoggerFactory>();
        return new ProjectionConsumer(
            topic,
            new BatchApplier(
                sp.GetRequiredService<ProjectionWriter>(),
                sp.GetRequiredService<IntegrationEventTypes>(),
                options,
                loggers.CreateLogger<BatchApplier>()),
            options,
            sp.GetRequiredService<IProducer<string, byte[]>>(),
            loggers.CreateLogger<ProjectionConsumer>());
    }
}
