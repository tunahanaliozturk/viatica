using Viatica.PspSim.Behaviour;

namespace Viatica.PspSim.UnitTests;

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
        (p99 / median).ShouldBeInRange(3.6, 4.5);
    }

    [Fact]
    public void A_timeout_card_acts_on_the_first_attempt_under_a_key_and_lets_the_repeat_through()
    {
        var state = new ProviderState(1);

        state.Decide(CardBehaviour.TimeoutPerformed, attempt: 1).ShouldBe(Fate.TimeoutPerformed);
        state.Decide(CardBehaviour.TimeoutNotPerformed, attempt: 1).ShouldBe(Fate.TimeoutNotPerformed);
        state.Decide(CardBehaviour.TimeoutPerformed, attempt: 2).ShouldBe(Fate.Normal);
        state.Decide(CardBehaviour.TimeoutNotPerformed, attempt: 2).ShouldBe(Fate.Normal);
        state.Decide(CardBehaviour.Approve, attempt: 1).ShouldBe(Fate.Normal);
        state.Decide(null, attempt: 1).ShouldBe(Fate.Normal);
    }

    [Fact]
    public void The_same_seed_decides_the_same_fates_and_reset_starts_over()
    {
        var knobs = new KnobsPatch(0, null, null, null, 0.2, 0.3, 0.5, null);
        var first = new ProviderState(42);
        var second = new ProviderState(42);
        first.Set(knobs.ApplyTo(first.Knobs));
        second.Set(knobs.ApplyTo(second.Knobs));

        Fate[] fates = [.. Enumerable.Range(0, 100).Select(_ => first.Decide(null, 1))];

        Enumerable.Range(0, 100).Select(_ => second.Decide(null, 1)).ShouldBe(fates);
        fates.Distinct().Count().ShouldBeGreaterThan(2, "the knobs should produce more than one kind of fate");

        first.Reset();
        first.Knobs.ShouldBe(Knobs.Defaults);
        first.Set(knobs.ApplyTo(first.Knobs));
        Enumerable.Range(0, 100).Select(_ => first.Decide(null, 1)).ShouldBe(fates);
    }

    [Fact]
    public void A_rate_of_one_always_fires_and_zero_never_does()
    {
        var state = new ProviderState(3);

        state.Set(Knobs.Defaults with { ErrorRate = 1 });
        Enumerable.Range(0, 20).ShouldAllBe(_ => state.Decide(null, 1) == Fate.ServerError);

        state.Set(Knobs.Defaults with { TimeoutRate = 1, PerformedRate = 1 });
        Enumerable.Range(0, 20).ShouldAllBe(_ => state.Decide(null, 1) == Fate.TimeoutPerformed);

        state.Set(Knobs.Defaults with { TimeoutRate = 1, PerformedRate = 0 });
        Enumerable.Range(0, 20).ShouldAllBe(_ => state.Decide(null, 1) == Fate.TimeoutNotPerformed);

        state.Set(Knobs.Defaults);
        Enumerable.Range(0, 20).ShouldAllBe(_ => state.Decide(null, 1) == Fate.Normal);
    }

    [Theory]
    [InlineData(-1, 0, 30000, 0.1)]
    [InlineData(0, 4, 30000, 0.1)]
    [InlineData(0, 0, 400000, 0.1)]
    [InlineData(0, 0, 30000, 1.5)]
    public void Knobs_out_of_range_are_refused(double median, double sigma, int hang, double rate)
    {
        (Knobs.Defaults with { LatencyMedianMs = median, LatencySigma = sigma, TimeoutHangMs = hang, ErrorRate = rate })
            .Problem().ShouldNotBeNull();
    }

    [Fact]
    public void The_defaults_are_valid()
    {
        Knobs.Defaults.Problem().ShouldBeNull();
    }

    [Fact]
    public void A_patch_changes_only_the_fields_it_names()
    {
        Knobs patched = new KnobsPatch(null, null, true, null, null, 0.25, null, null).ApplyTo(Knobs.Defaults);

        patched.ShouldBe(Knobs.Defaults with { Outage = true, TimeoutRate = 0.25 });
    }
}
