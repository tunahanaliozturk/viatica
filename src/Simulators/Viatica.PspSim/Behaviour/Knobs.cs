namespace Viatica.PspSim.Behaviour;

/// <summary>
/// The switches that decide how the provider misbehaves, live, through the admin API. Test cards decide what happens
/// to one card; these decide what happens to every call.
/// </summary>
/// <param name="LatencyMedianMs">Median of the delay added to every call. Zero turns latency off.</param>
/// <param name="LatencySigma">Spread of the log-normal delay. 0 is a fixed delay.</param>
/// <param name="Outage">When on, every call answers 503 at once, before anything is done.</param>
/// <param name="LookupOutage">When on, only the lookup by idempotency key answers 503. Operations keep working.</param>
/// <param name="ErrorRate">Share of mutating calls that answer 500 without doing anything.</param>
/// <param name="TimeoutRate">Share of mutating calls that hang and end in a 504.</param>
/// <param name="PerformedRate">Share of those timed out calls that did their work before hanging.</param>
/// <param name="TimeoutHangMs">How long a timed out call hangs before the 504.</param>
internal sealed record Knobs(
    double LatencyMedianMs,
    double LatencySigma,
    bool Outage,
    bool LookupOutage,
    double ErrorRate,
    double TimeoutRate,
    double PerformedRate,
    int TimeoutHangMs)
{
    public const double MaxLatencyMs = 60_000;

    /// <summary>Quiet defaults: a little latency, nothing failing.</summary>
    public static Knobs Defaults { get; } = new(80, 0.3, false, false, 0, 0, 0.5, 30_000);

    public string? Problem() => this switch
    {
        { LatencyMedianMs: < 0 or > MaxLatencyMs } => $"LatencyMedianMs must be between 0 and {MaxLatencyMs}.",
        { LatencySigma: < 0 or > 3 } => "LatencySigma must be between 0 and 3.",
        { TimeoutHangMs: < 0 or > 300_000 } => "TimeoutHangMs must be between 0 and 300000.",
        _ when !IsRate(ErrorRate) || !IsRate(TimeoutRate) || !IsRate(PerformedRate) => "Rates must be between 0 and 1.",
        _ => null,
    };

    private static bool IsRate(double value) => value is >= 0 and <= 1;
}

/// <summary>A partial change to <see cref="Knobs"/>: the fields left out keep their current value.</summary>
internal sealed record KnobsPatch(
    double? LatencyMedianMs,
    double? LatencySigma,
    bool? Outage,
    bool? LookupOutage,
    double? ErrorRate,
    double? TimeoutRate,
    double? PerformedRate,
    int? TimeoutHangMs)
{
    public Knobs ApplyTo(Knobs current) => new(
        LatencyMedianMs ?? current.LatencyMedianMs,
        LatencySigma ?? current.LatencySigma,
        Outage ?? current.Outage,
        LookupOutage ?? current.LookupOutage,
        ErrorRate ?? current.ErrorRate,
        TimeoutRate ?? current.TimeoutRate,
        PerformedRate ?? current.PerformedRate,
        TimeoutHangMs ?? current.TimeoutHangMs);
}

/// <summary>What a mutating call runs into, decided before it does anything.</summary>
internal enum Fate
{
    Normal,

    /// <summary>A 500 and nothing done.</summary>
    ServerError,

    /// <summary>Hangs, then a 504, and nothing was done.</summary>
    TimeoutNotPerformed,

    /// <summary>Does its work, then hangs and answers 504.</summary>
    TimeoutPerformed,
}

/// <summary>The live knobs and the random stream of the provider.</summary>
/// <remarks>
/// The stream is seeded, so a test that sends the same calls in the same order sees the same failures on every run.
/// Concurrent callers interleave their draws, which is fine: rates hold over many calls either way.
/// </remarks>
internal sealed class ProviderState(int seed)
{
    private readonly Lock _gate = new();
    private Random _random = new(seed);
    private Knobs _knobs = Knobs.Defaults;

    public Knobs Knobs => Volatile.Read(ref _knobs);

    public void Set(Knobs knobs) => Volatile.Write(ref _knobs, knobs);

    /// <summary>Back to the default knobs and the start of the random stream, so a reset run repeats the first one.</summary>
    public void Reset()
    {
        lock (_gate)
        {
            _random = new Random(seed);
        }

        Set(Knobs.Defaults);
    }

    public TimeSpan Latency()
    {
        Knobs knobs = Knobs;
        return knobs.LatencyMedianMs <= 0
            ? TimeSpan.Zero
            : LatencyModel.Sample(knobs.LatencyMedianMs, knobs.LatencySigma, Next(), Next());
    }

    /// <summary>
    /// What happens to a mutating call. A timeout card acts on the first attempt under an idempotency key only: the
    /// repeat is what shows whether the caller resolves the doubt correctly, so it must be allowed to succeed.
    /// </summary>
    public Fate Decide(CardBehaviour? card, int attempt)
    {
        if (attempt == 1 && card == CardBehaviour.TimeoutPerformed)
        {
            return Fate.TimeoutPerformed;
        }

        if (attempt == 1 && card == CardBehaviour.TimeoutNotPerformed)
        {
            return Fate.TimeoutNotPerformed;
        }

        Knobs knobs = Knobs;
        if (Chance(knobs.ErrorRate))
        {
            return Fate.ServerError;
        }

        if (Chance(knobs.TimeoutRate))
        {
            return Chance(knobs.PerformedRate) ? Fate.TimeoutPerformed : Fate.TimeoutNotPerformed;
        }

        return Fate.Normal;
    }

    private bool Chance(double rate) => rate switch
    {
        <= 0 => false,
        >= 1 => true,
        _ => Next() < rate,
    };

    private double Next()
    {
        lock (_gate)
        {
            return _random.NextDouble();
        }
    }
}

internal static class LatencyModel
{
    /// <summary>
    /// A log-normal delay with the given median: most calls near the median, a few many times slower, which is the
    /// shape a deadline has to survive.
    /// </summary>
    /// <param name="u1">A uniform draw in [0, 1).</param>
    /// <param name="u2">A second, independent uniform draw in [0, 1).</param>
    public static TimeSpan Sample(double medianMs, double sigma, double u1, double u2)
    {
        // Box-Muller turns two uniforms into one standard normal. 1 - u1 keeps the logarithm away from zero.
        double normal = Math.Sqrt(-2 * Math.Log(1 - u1)) * Math.Cos(2 * Math.PI * u2);
        double ms = Math.Min(medianMs * Math.Exp(sigma * normal), Knobs.MaxLatencyMs);
        return TimeSpan.FromMilliseconds(ms);
    }
}
