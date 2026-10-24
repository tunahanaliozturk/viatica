using System.Diagnostics;
using System.Diagnostics.Metrics;

namespace Viatica.PspSim;

/// <summary>
/// The sim's own signals. They answer one question a system test run raises: did the provider misbehave because we
/// told it to? Every injected delay, outage, error and timeout is counted and traced.
/// </summary>
internal static class Telemetry
{
    public const string Name = "Viatica.PspSim";

    public static readonly ActivitySource Source = new(Name);

    public static readonly Meter Meter = new(Name);

    public static readonly Histogram<double> InjectedLatency =
        Meter.CreateHistogram<double>("pspsim.injected_latency", "ms", "Delay added to a call on purpose.");

    public static readonly Counter<long> Faults =
        Meter.CreateCounter<long>("pspsim.faults", "{fault}", "Failures injected by the knobs and the timeout cards, by kind.");

    public static readonly Counter<long> Operations =
        Meter.CreateCounter<long>("pspsim.operations", "{operation}", "Mutating calls that reached the ledger, by operation, status and whether they replayed an earlier answer.");

    public static void Fault(string kind) => Faults.Add(1, new KeyValuePair<string, object?>("fault", kind));
}

/// <summary>
/// Source-generated log messages. Operation names and counts only: a card number never reaches a log line, and the
/// sim never holds one past the request that carried it.
/// </summary>
internal static partial class Log
{
    [LoggerMessage(1, LogLevel.Warning, "Answering {Operation} with a 500 and doing nothing (injected)")]
    public static partial void ServerError(ILogger logger, string operation);

    [LoggerMessage(2, LogLevel.Warning, "Timing out {Operation} (injected); the work was done before the hang: {Performed}")]
    public static partial void Timeout(ILogger logger, string operation, bool performed);

    [LoggerMessage(3, LogLevel.Information, "Answered a repeated {Operation} with the recorded result")]
    public static partial void Replayed(ILogger logger, string operation);

    [LoggerMessage(4, LogLevel.Information, "Knobs changed to {Knobs}")]
    public static partial void KnobsChanged(ILogger logger, string knobs);

    [LoggerMessage(5, LogLevel.Information, "Knobs reset to their defaults")]
    public static partial void KnobsReset(ILogger logger);
}
