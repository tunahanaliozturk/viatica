namespace Viatica.AirlineSim.Behaviour;

/// <summary>The live knobs and the random stream of one airline.</summary>
/// <remarks>
/// Each airline draws from its own stream seeded from the sim's seed, so a test that sends the same calls in the same
/// order sees the same delays, refusals and timeouts on every run, and traffic to one airline never shifts the dice
/// of another. Concurrent callers interleave their draws, which is fine: rates hold over many calls either way.
/// </remarks>
internal sealed class AirlineState
{
    private readonly int _seed;
    private readonly Lock _gate = new();
    private Random _random;
    private Knobs _knobs;

    public AirlineState(Airline airline, int seed)
    {
        Airline = airline;
        _seed = seed;
        _random = new Random(seed);
        _knobs = airline.Defaults;
    }

    public Airline Airline { get; }

    public Knobs Knobs => Volatile.Read(ref _knobs);

    public void Set(Knobs knobs) => Volatile.Write(ref _knobs, knobs);

    /// <summary>Back to the default knobs and the start of the random stream, so a reset run repeats the first one.</summary>
    public void Reset()
    {
        lock (_gate)
        {
            _random = new Random(_seed);
        }

        Set(Airline.Defaults);
    }

    public bool Chance(double rate) => rate switch
    {
        <= 0 => false,
        >= 1 => true,
        _ => Next() < rate,
    };

    /// <summary>A uniform draw in [0, 1).</summary>
    public double Next()
    {
        lock (_gate)
        {
            return _random.NextDouble();
        }
    }

    public TimeSpan Latency()
    {
        Knobs knobs = Knobs;
        return knobs.LatencyMedianMs <= 0
            ? TimeSpan.Zero
            : LatencyModel.Sample(knobs.LatencyMedianMs, knobs.LatencySigma, Next(), Next());
    }
}

internal static class LatencyModel
{
    /// <summary>
    /// A log-normal delay with the given median. Supplier latency is skewed: most calls are near the median and a
    /// few are many times slower, which a normal or uniform distribution cannot produce and a deadline must survive.
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

/// <summary>Every airline's live state, created once per process from the seed.</summary>
internal sealed class Behaviours
{
    private readonly Dictionary<string, AirlineState> _airlines;

    public Behaviours(int seed) =>
        _airlines = Airline.All
            .Select((airline, index) => new AirlineState(airline, unchecked((seed * 31) + index + 1)))
            .ToDictionary(static state => state.Airline.Code, StringComparer.Ordinal);

    public IEnumerable<AirlineState> All => Airline.All.Select(airline => _airlines[airline.Code]);

    public AirlineState? Find(string code) => _airlines.GetValueOrDefault(code);

    public void ResetAll()
    {
        foreach (AirlineState state in _airlines.Values)
        {
            state.Reset();
        }
    }
}
