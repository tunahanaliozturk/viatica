using Viatica.AirlineSim.Behaviour;

namespace Viatica.AirlineSim.UnitTests;

public sealed class BehaviourTests
{
    [Fact]
    public void Latency_has_the_configured_median_and_a_long_tail()
    {
        var random = new Random(7);
        double[] samples =
        [
            .. Enumerable.Range(0, 20_000)
                .Select(_ => LatencyModel.Sample(1_500, 0.6, random.NextDouble(), random.NextDouble()).TotalMilliseconds)
                .Order(),
        ];

        double median = samples[samples.Length / 2];
        double p99 = samples[(int)(samples.Length * 0.99)];

        median.ShouldBeInRange(1_450, 1_550);
        // exp(2.326 * 0.6) is about 4.0: the slow airline's p99 is around six seconds.
        (p99 / median).ShouldBeInRange(3.6, 4.5);
    }

    [Fact]
    public void Zero_sigma_is_a_fixed_delay()
    {
        LatencyModel.Sample(200, 0, 0.3, 0.9).ShouldBe(TimeSpan.FromMilliseconds(200));
    }

    [Fact]
    public void The_same_seed_draws_the_same_decisions_and_reset_starts_over()
    {
        Airline fickle = Airline.Find("JG")!;
        var first = new AirlineState(fickle, 42);
        var second = new AirlineState(fickle, 42);

        bool[] draws = [.. Enumerable.Range(0, 50).Select(_ => first.Chance(0.5))];

        Enumerable.Range(0, 50).Select(_ => second.Chance(0.5)).ShouldBe(draws);
        first.Reset();
        Enumerable.Range(0, 50).Select(_ => first.Chance(0.5)).ShouldBe(draws);
    }

    [Fact]
    public void A_rate_holds_over_many_draws()
    {
        var state = new AirlineState(Airline.Find("JG")!, 9);

        double share = Enumerable.Range(0, 10_000).Count(_ => state.Chance(0.2)) / 10_000.0;

        share.ShouldBeInRange(0.18, 0.22);
    }

    [Fact]
    public void Each_airline_draws_from_its_own_stream()
    {
        var behaviours = new Behaviours(1);
        AirlineState steady = behaviours.Find("ZC")!;
        AirlineState fickle = behaviours.Find("JG")!;

        Enumerable.Range(0, 20).Select(_ => steady.Next())
            .ShouldNotBe(Enumerable.Range(0, 20).Select(_ => fickle.Next()));
    }

    [Fact]
    public void A_patch_changes_only_what_it_names_and_reset_restores_the_defaults()
    {
        AirlineState steady = new Behaviours(1).Find("ZC")!;
        var patch = new KnobsPatch(null, null, Outage: true, null, null, null, null, null);

        steady.Set(patch.ApplyTo(steady.Knobs));

        steady.Knobs.ShouldBe(steady.Airline.Defaults with { Outage = true });
        steady.Reset();
        steady.Knobs.ShouldBe(steady.Airline.Defaults);
    }

    [Theory]
    [InlineData(-1, 0.3, 0.1, 100)]
    [InlineData(100, 4, 0.1, 100)]
    [InlineData(100, 0.3, 1.5, 100)]
    [InlineData(100, 0.3, 0.1, -5)]
    public void Knobs_out_of_range_are_rejected(double median, double sigma, double rate, int hang)
    {
        new Knobs(median, sigma, false, rate, 0, 0, hang, 0).Problem().ShouldNotBeNull();
    }

    [Fact]
    public void Every_default_is_valid_and_the_four_airlines_behave_differently()
    {
        Airline.All.ShouldAllBe(airline => airline.Defaults.Problem() == null);

        Airline.Find("ZC")!.Defaults.ShouldSatisfyAllConditions(
            k => k.LatencyMedianMs.ShouldBeLessThan(100),
            k => k.TicketingRefusalRate.ShouldBe(0),
            k => k.OrderTimeoutRate.ShouldBe(0));
        Airline.Find("YZ")!.Defaults.LatencyMedianMs.ShouldBe(1_500);
        Airline.Find("JG")!.Defaults.TicketingRefusalRate.ShouldBeGreaterThan(0);
        Airline.Find("ZJ")!.Defaults.ShouldSatisfyAllConditions(
            k => k.OrderTimeoutRate.ShouldBeGreaterThan(0),
            k => k.CreateAnywayRate.ShouldBeGreaterThan(0));
    }
}
