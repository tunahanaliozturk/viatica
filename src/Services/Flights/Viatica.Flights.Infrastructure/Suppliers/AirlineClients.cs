using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Http.Resilience;
using Microsoft.Extensions.Options;
using Polly;
using Polly.Timeout;

namespace Viatica.Flights.Infrastructure.Suppliers;

/// <summary>One named HTTP client per airline, each with its own resilience pipeline and so its own circuit breaker.</summary>
public static class AirlineClients
{
    public static string NameOf(string airline) => "airline-" + airline;

    public static IServiceCollection AddAirlineClients(this IServiceCollection services, IConfiguration configuration)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(configuration);

        IConfigurationSection section = configuration.GetSection(SuppliersOptions.Section);
        services.AddOptions<SuppliersOptions>().Bind(section).ValidateOnStart();
        services.AddSingleton<IValidateOptions<SuppliersOptions>, SuppliersOptionsValidator>();

        // The airlines are read here, where the clients are named. Their limits are read when each pipeline is built,
        // so they follow the options the host ends up with.
        foreach (AirlineEndpoint airline in section.Get<SuppliersOptions>()?.Airlines ?? [])
        {
            string name = NameOf(airline.Code);

            services
                .AddHttpClient(name, client => client.BaseAddress = new Uri(airline.BaseUrl, UriKind.Absolute))
                .AddResilienceHandler(name, (pipeline, context) =>
                {
                    SuppliersOptions suppliers = context.GetOptions<SuppliersOptions>(Options.DefaultName);
                    BreakerOptions breaker = suppliers.CircuitBreaker;

                    // Outermost, so it sees the timeout below as a failure: an airline that keeps being too slow
                    // opens its circuit like one that keeps erroring. No retry strategy at all. A search has no
                    // time for a second try, and an order must never be created blindly twice (the handler looks
                    // it up by key instead).
                    pipeline.AddCircuitBreaker(new HttpCircuitBreakerStrategyOptions
                    {
                        Name = name + "-breaker",
                        FailureRatio = breaker.FailureRatio,
                        MinimumThroughput = breaker.MinimumThroughput,
                        SamplingDuration = breaker.SamplingDuration,
                        BreakDuration = breaker.BreakDuration,
                    });

                    // Per attempt, and per call: the gateway puts the budget on the request (the search deadline for
                    // a search, the request timeout for the rest) and this reads it back.
                    pipeline.AddTimeout(new TimeoutStrategyOptions
                    {
                        Name = name + "-timeout",
                        TimeoutGenerator = args => new ValueTask<TimeSpan>(
                            args.Context.GetRequestMessage() is { } request && request.Options.TryGetValue(AirlineGateway.TimeoutKey, out TimeSpan budget)
                                ? budget
                                : suppliers.RequestTimeout),
                    });
                });
        }

        return services;
    }
}
