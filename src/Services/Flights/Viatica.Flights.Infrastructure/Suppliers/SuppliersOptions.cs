using Microsoft.Extensions.Options;

namespace Viatica.Flights.Infrastructure.Suppliers;

/// <summary>The airlines and how patiently they are asked, bound from <c>Flights:Suppliers</c>.</summary>
public sealed class SuppliersOptions
{
    public const string Section = "Flights:Suppliers";

    public List<AirlineEndpoint> Airlines { get; set; } = [];

    /// <summary>
    /// Longest any single airline call except a search may take. A creation that exceeds it is looked up by
    /// key, not retried, so this is how long an order waits before asking whether it happened.
    /// </summary>
    public TimeSpan RequestTimeout { get; set; } = TimeSpan.FromSeconds(8);

    public BreakerOptions CircuitBreaker { get; set; } = new();
}

public sealed class AirlineEndpoint
{
    /// <summary>The airline's code as it appears in <c>incompleteSuppliers</c>: <c>ZC</c>, <c>YZ</c>, ...</summary>
    public string Code { get; set; } = "";

    /// <summary>The airline's API root, ending in a slash. Each airline has its own.</summary>
    public string BaseUrl { get; set; } = "";
}

/// <summary>
/// One breaker per airline. It opens when at least <see cref="FailureRatio"/> of the calls in the last
/// <see cref="SamplingDuration"/> failed, once there were <see cref="MinimumThroughput"/> of them, and stays open for
/// <see cref="BreakDuration"/>. While it is open the airline is skipped at once instead of eating the deadline.
/// </summary>
public sealed class BreakerOptions
{
    public double FailureRatio { get; set; } = 0.5;

    public int MinimumThroughput { get; set; } = 10;

    public TimeSpan SamplingDuration { get; set; } = TimeSpan.FromSeconds(30);

    public TimeSpan BreakDuration { get; set; } = TimeSpan.FromSeconds(30);
}

internal sealed class SuppliersOptionsValidator : IValidateOptions<SuppliersOptions>
{
    public ValidateOptionsResult Validate(string? name, SuppliersOptions options)
    {
        var failures = new List<string>();

        if (options.Airlines.Count == 0)
        {
            failures.Add("Flights:Suppliers:Airlines needs at least one airline.");
        }

        foreach (AirlineEndpoint airline in options.Airlines)
        {
            if (airline.Code is not { Length: 2 } || !airline.Code.All(static c => char.IsAsciiLetterUpper(c) || char.IsAsciiDigit(c)))
            {
                failures.Add("Every airline needs a two character upper case Code.");
            }

            if (!Uri.TryCreate(airline.BaseUrl, UriKind.Absolute, out Uri? url) || url.Scheme is not ("http" or "https"))
            {
                failures.Add($"Airline {airline.Code} needs an absolute http or https BaseUrl.");
            }
        }

        if (options.Airlines.Select(static a => a.Code).Distinct(StringComparer.Ordinal).Count() != options.Airlines.Count)
        {
            failures.Add("Each airline code can appear once.");
        }

        if (options.RequestTimeout <= TimeSpan.Zero)
        {
            failures.Add("Flights:Suppliers:RequestTimeout must be positive.");
        }

        BreakerOptions breaker = options.CircuitBreaker;
        if (breaker.FailureRatio is <= 0 or > 1 || breaker.MinimumThroughput < 2
            || breaker.SamplingDuration < TimeSpan.FromMilliseconds(500) || breaker.BreakDuration < TimeSpan.FromMilliseconds(500))
        {
            failures.Add("Flights:Suppliers:CircuitBreaker needs a failure ratio in (0, 1], a throughput of at least 2 and durations of at least 0.5 s.");
        }

        return failures.Count == 0 ? ValidateOptionsResult.Success : ValidateOptionsResult.Fail(failures);
    }
}
