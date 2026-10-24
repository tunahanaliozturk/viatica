namespace Viatica.AirlineSim.Behaviour;

/// <summary>
/// The switches that decide how an airline misbehaves. Every simulated airline has all of them; the four differ only
/// in their defaults, so a system test can make the reliable airline slow or the slow one refuse tickets on cue.
/// </summary>
/// <param name="LatencyMedianMs">Median of the delay added to every call. Zero turns latency off.</param>
/// <param name="LatencySigma">
/// Spread of the log-normal delay. 0 is a fixed delay; 0.6 puts p99 at about four times the median, which is the
/// long tail a slow supplier really has.
/// </param>
/// <param name="Outage">When on, every supplier call answers 503 at once.</param>
/// <param name="TicketingRefusalRate">Share of orders whose ticketing is refused. Decided once per order.</param>
/// <param name="OrderTimeoutRate">Share of order creations that hang and end in a 504.</param>
/// <param name="CreateAnywayRate">Share of those timed out creations that still create the order.</param>
/// <param name="TimeoutHangMs">How long a timed out call hangs before the 504.</param>
/// <param name="PriceChangeRate">Share of re-prices that come back dearer than the offer.</param>
internal sealed record Knobs(
    double LatencyMedianMs,
    double LatencySigma,
    bool Outage,
    double TicketingRefusalRate,
    double OrderTimeoutRate,
    double CreateAnywayRate,
    int TimeoutHangMs,
    double PriceChangeRate)
{
    public const double MaxLatencyMs = 60_000;

    public string? Problem() => this switch
    {
        { LatencyMedianMs: < 0 or > MaxLatencyMs } => $"LatencyMedianMs must be between 0 and {MaxLatencyMs}.",
        { LatencySigma: < 0 or > 3 } => "LatencySigma must be between 0 and 3.",
        { TimeoutHangMs: < 0 or > 300_000 } => "TimeoutHangMs must be between 0 and 300000.",
        _ when !IsRate(TicketingRefusalRate) || !IsRate(OrderTimeoutRate) ||
               !IsRate(CreateAnywayRate) || !IsRate(PriceChangeRate) => "Rates must be between 0 and 1.",
        _ => null,
    };

    private static bool IsRate(double value) => value is >= 0 and <= 1;
}

/// <summary>A partial change to <see cref="Knobs"/>: the fields left out keep their current value.</summary>
internal sealed record KnobsPatch(
    double? LatencyMedianMs,
    double? LatencySigma,
    bool? Outage,
    double? TicketingRefusalRate,
    double? OrderTimeoutRate,
    double? CreateAnywayRate,
    int? TimeoutHangMs,
    double? PriceChangeRate)
{
    public Knobs ApplyTo(Knobs current) => new(
        LatencyMedianMs ?? current.LatencyMedianMs,
        LatencySigma ?? current.LatencySigma,
        Outage ?? current.Outage,
        TicketingRefusalRate ?? current.TicketingRefusalRate,
        OrderTimeoutRate ?? current.OrderTimeoutRate,
        CreateAnywayRate ?? current.CreateAnywayRate,
        TimeoutHangMs ?? current.TimeoutHangMs,
        PriceChangeRate ?? current.PriceChangeRate);
}

/// <summary>A simulated airline. Codes, names and ticket prefixes are invented; see docs/airline-sim.md.</summary>
internal sealed record Airline(string Code, string Name, string Hub, string TicketPrefix, string Behaviour, Knobs Defaults)
{
    // A real supplier's timeout is something the caller sees, not a number the supplier reports, so the hang is long
    // enough to outlast any sane client timeout. Tests that want the 504 itself shorten it through the admin API.
    private const int DefaultHangMs = 30_000;

    /// <summary>The four airlines, in the order the seed walks them.</summary>
    public static IReadOnlyList<Airline> All { get; } =
    [
        new("ZC", "Steady Air", "AMS", "901", "Fast and reliable.",
            new Knobs(60, 0.25, false, 0, 0, 0, DefaultHangMs, 0)),
        new("YZ", "Molasses Airways", "FRA", "902", "Slow: p50 about 1.5 s with a long tail.",
            new Knobs(1_500, 0.6, false, 0, 0, 0, DefaultHangMs, 0)),
        new("JG", "Fickle Air", "MAD", "903", "Refuses ticketing on a share of orders.",
            new Knobs(120, 0.35, false, 0.2, 0, 0, DefaultHangMs, 0)),
        new("ZJ", "Limbo Airlines", "IST", "904", "Times out on order creation, and sometimes creates the order anyway.",
            new Knobs(200, 0.4, false, 0, 0.1, 0.5, DefaultHangMs, 0)),
    ];

    public static Airline? Find(string code) => All.FirstOrDefault(airline => airline.Code == code);
}
