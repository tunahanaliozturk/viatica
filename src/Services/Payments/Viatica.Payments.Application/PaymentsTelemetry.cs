using System.Diagnostics;
using System.Diagnostics.Metrics;

namespace Viatica.Payments.Application;

/// <summary>
/// The service's one <see cref="ActivitySource"/> and one <see cref="Meter"/>, both named <c>Viatica.Payments</c>.
/// Tags carry operation and outcome names only: never a card number, a token, an amount or a provider reference.
/// </summary>
public static class PaymentsTelemetry
{
    public const string Name = "Viatica.Payments";

    public static ActivitySource Source { get; } = new(Name);

    private static readonly Meter Meter = new(Name);

    /// <summary>Every call to the provider, by operation and outcome: the provider's error rate.</summary>
    public static Counter<long> ProviderCalls { get; } = Meter.CreateCounter<long>("payments.provider.calls");

    /// <summary>How long the provider took to answer, by operation.</summary>
    public static Histogram<double> ProviderLatency { get; } =
        Meter.CreateHistogram<double>("payments.provider.duration", unit: "ms");

    /// <summary>Requests carried out, by operation and outcome (succeeded, declined, refused).</summary>
    public static Counter<long> Operations { get; } = Meter.CreateCounter<long>("payments.operations");

    /// <summary>Lookups by idempotency key after an unanswered call, by what they found: found, none, failed.</summary>
    public static Counter<long> Lookups { get; } = Meter.CreateCounter<long>("payments.lookups");

    /// <summary>Operations sent a second time under the same key because the lookup said the first never happened.</summary>
    public static Counter<long> Resends { get; } = Meter.CreateCounter<long>("payments.resends");

    /// <summary>Cards tokenised, by result.</summary>
    public static Counter<long> Tokenisations { get; } = Meter.CreateCounter<long>("payments.tokenisations");
}
