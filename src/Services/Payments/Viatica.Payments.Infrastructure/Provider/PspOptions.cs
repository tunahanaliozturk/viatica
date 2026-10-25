using Microsoft.Extensions.Options;

namespace Viatica.Payments.Infrastructure.Provider;

/// <summary>The payment provider and how patiently it is asked, bound from <c>Payments:Provider</c>.</summary>
public sealed class PspOptions
{
    public const string Section = "Payments:Provider";

    /// <summary>The provider's API root, ending in a slash.</summary>
    public string BaseUrl { get; set; } = "";

    /// <summary>
    /// Longest any single provider call may take. An operation that exceeds it is looked up by key, not retried, so
    /// this is how long a payment waits before asking whether it happened.
    /// </summary>
    public TimeSpan RequestTimeout { get; set; } = TimeSpan.FromSeconds(5);

    public BreakerOptions CircuitBreaker { get; set; } = new();
}

/// <summary>
/// It opens when at least <see cref="FailureRatio"/> of the calls in the last <see cref="SamplingDuration"/> failed,
/// once there were <see cref="MinimumThroughput"/> of them, and stays open for <see cref="BreakDuration"/>. While it is
/// open nothing is sent: an authorisation is declined at once, and the other operations wait for it to close.
/// </summary>
public sealed class BreakerOptions
{
    public double FailureRatio { get; set; } = 0.5;

    public int MinimumThroughput { get; set; } = 10;

    public TimeSpan SamplingDuration { get; set; } = TimeSpan.FromSeconds(30);

    public TimeSpan BreakDuration { get; set; } = TimeSpan.FromSeconds(15);
}

internal sealed class PspOptionsValidator : IValidateOptions<PspOptions>
{
    public ValidateOptionsResult Validate(string? name, PspOptions options)
    {
        var failures = new List<string>();

        if (!Uri.TryCreate(options.BaseUrl, UriKind.Absolute, out Uri? url) || url.Scheme is not ("http" or "https"))
        {
            failures.Add("Payments:Provider:BaseUrl must be an absolute http or https URL.");
        }

        if (options.RequestTimeout <= TimeSpan.Zero)
        {
            failures.Add("Payments:Provider:RequestTimeout must be positive.");
        }

        BreakerOptions breaker = options.CircuitBreaker;
        if (breaker.FailureRatio is <= 0 or > 1 || breaker.MinimumThroughput < 2
            || breaker.SamplingDuration < TimeSpan.FromMilliseconds(500) || breaker.BreakDuration < TimeSpan.FromMilliseconds(500))
        {
            failures.Add("Payments:Provider:CircuitBreaker needs a failure ratio in (0, 1], a throughput of at least 2 and durations of at least 0.5 s.");
        }

        return failures.Count == 0 ? ValidateOptionsResult.Success : ValidateOptionsResult.Fail(failures);
    }
}
