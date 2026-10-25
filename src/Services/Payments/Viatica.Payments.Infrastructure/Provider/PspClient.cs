using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Http.Resilience;
using Microsoft.Extensions.Options;
using Polly;
using Polly.Timeout;

namespace Viatica.Payments.Infrastructure.Provider;

/// <summary>The one named HTTP client for the payment provider, with its resilience pipeline and so its circuit breaker.</summary>
public static class PspClient
{
    public const string Name = "psp";

    public static IServiceCollection AddPspClient(this IServiceCollection services, IConfiguration configuration)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(configuration);

        IConfigurationSection section = configuration.GetSection(PspOptions.Section);
        services.AddOptions<PspOptions>().Bind(section).ValidateOnStart();
        services.AddSingleton<IValidateOptions<PspOptions>, PspOptionsValidator>();

        // The base address is read here, where the client is named. The limits are read when the pipeline is built,
        // so they follow the options the host ends up with.
        string baseUrl = section.Get<PspOptions>()?.BaseUrl ?? "";

        services
            .AddHttpClient(Name, client =>
            {
                if (Uri.TryCreate(baseUrl, UriKind.Absolute, out Uri? url))
                {
                    client.BaseAddress = url;
                }
            })
            .AddResilienceHandler(Name, (pipeline, context) =>
            {
                PspOptions psp = context.GetOptions<PspOptions>(Options.DefaultName);
                BreakerOptions breaker = psp.CircuitBreaker;

                // Outermost, so it sees the timeout below as a failure: a provider that keeps being too slow opens its
                // circuit like one that keeps erroring. No retry strategy at all. A payment call that may have reached
                // the provider is looked up by its key and, if the provider has nothing, sent again by the operation
                // desk under the same key. A blind retry here would hide that decision from the code that has to make it.
                pipeline.AddCircuitBreaker(new HttpCircuitBreakerStrategyOptions
                {
                    Name = Name + "-breaker",
                    FailureRatio = breaker.FailureRatio,
                    MinimumThroughput = breaker.MinimumThroughput,
                    SamplingDuration = breaker.SamplingDuration,
                    BreakDuration = breaker.BreakDuration,
                });

                pipeline.AddTimeout(new TimeoutStrategyOptions
                {
                    Name = Name + "-timeout",
                    Timeout = psp.RequestTimeout,
                });
            });

        return services;
    }
}
