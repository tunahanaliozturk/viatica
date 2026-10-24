using Viatica.Search.Application.Projection;

namespace Viatica.Search.UnitTests;

public sealed class NightLedgerTests
{
    private static readonly int First = Events.FirstDay;
    private static int Day(int offset) => Events.Today.AddDays(offset).DayNumber;

    private static NightLedger Apply(NightLedger ledger, long version, params (int Offset, int Available, long Cents)[] nights) =>
        ledger.Apply(version, nights.Select(n => new NightChange(Day(n.Offset), n.Available, n.Cents)), First, out _);

    private static string Describe(NightLedger ledger) =>
        string.Join(";", Enumerable.Range(0, ledger.Length).Select(i => $"{ledger.From + i}:{ledger.Available[i]}:{ledger.RateCents[i]}:{ledger.Version[i]}"));

    [Fact]
    public void A_newer_event_replaces_a_night_and_an_older_one_does_not()
    {
        NightLedger ledger = Apply(new NightLedger(), 5, (0, 3, 10000));

        NightLedger newer = ledger.Apply(6, [new NightChange(Day(0), 1, 12000)], First, out int appliedNewer);
        NightLedger older = ledger.Apply(4, [new NightChange(Day(0), 9, 1)], First, out int appliedOlder);

        appliedNewer.ShouldBe(1);
        newer.Available[Day(0) - newer.From].ShouldBe(1);
        appliedOlder.ShouldBe(0);
        older.Available[Day(0) - older.From].ShouldBe(3);
    }

    [Fact]
    public void The_same_event_twice_changes_nothing()
    {
        NightLedger once = Apply(new NightLedger(), 5, (0, 3, 10000), (1, 2, 11000));
        NightLedger twice = once.Apply(5, [new NightChange(Day(0), 3, 10000), new NightChange(Day(1), 2, 11000)], First, out int applied);

        applied.ShouldBe(0);
        Describe(twice).ShouldBe(Describe(once));
    }

    [Fact]
    public void An_event_that_arrives_late_still_lands_on_the_nights_the_newer_one_did_not_touch()
    {
        // Version 7 reached night 1 only. Version 5 touched nights 0 and 1 but arrives afterwards: night 0 is
        // still news, night 1 is not.
        NightLedger ledger = Apply(new NightLedger(), 7, (1, 0, 20000));
        ledger = Apply(ledger, 5, (0, 4, 10000), (1, 4, 10000));

        int night0 = Day(0) - ledger.From;
        int night1 = Day(1) - ledger.From;
        ledger.Available[night0].ShouldBe(4);
        ledger.Available[night1].ShouldBe(0);
        ledger.RateCents[night1].ShouldBe(20000);
    }

    [Fact]
    public void Any_order_of_the_same_events_gives_the_same_ledger()
    {
        // Eight overlapping events with distinct versions, applied in 200 random orders, some of them twice.
        var random = new Random(20261024);
        (long Version, (int, int, long)[] Nights)[] events =
        [
            .. Enumerable.Range(1, 8).Select(v => ((long)v, Enumerable.Range(0, 6).Select(_ => (random.Next(0, 10), random.Next(0, 5), (long)random.Next(5000, 30000))).DistinctBy(static n => n.Item1).ToArray())),
        ];

        NightLedger expected = new();
        foreach ((long version, (int, int, long)[] nights) in events.OrderBy(static e => e.Version))
        {
            expected = Apply(expected, version, nights);
        }

        for (int round = 0; round < 200; round++)
        {
            NightLedger actual = new();
            foreach ((long version, (int, int, long)[] nights) in events.Concat(events.Take(round % 5)).OrderBy(_ => random.Next()))
            {
                actual = Apply(actual, version, nights);
            }

            Describe(actual).ShouldBe(Describe(expected), $"round {round}");
        }
    }

    [Fact]
    public void Nights_before_today_and_beyond_the_horizon_are_dropped()
    {
        NightLedger ledger = new NightLedger().Apply(
            1,
            [new NightChange(First - 5, 1, 1), new NightChange(First + NightLedger.HorizonDays + 1, 1, 1), new NightChange(Day(3), 2, 100)],
            First,
            out int applied);

        applied.ShouldBe(1);
        ledger.Length.ShouldBe(1);
    }

    [Fact]
    public void Trimming_drops_the_days_that_have_passed()
    {
        NightLedger ledger = Apply(new NightLedger(), 1, (0, 1, 100), (1, 1, 100), (2, 1, 100));

        NightLedger trimmed = ledger.Trim(Day(2));

        trimmed.From.ShouldBe(Day(2));
        trimmed.Length.ShouldBe(1);
    }

    [Fact]
    public void Runs_are_the_stretches_with_enough_units_and_a_reported_night()
    {
        // Units per night: 3, 3, 1, 3, (night 4 never reported), 3
        NightLedger ledger = Apply(new NightLedger(), 1, (0, 3, 100), (1, 3, 100), (2, 1, 100), (3, 3, 100), (5, 3, 100));

        ledger.Runs(1).ShouldBe([new DayRange(Day(0), Day(3)), new DayRange(Day(5), Day(5))]);
        ledger.Runs(2).ShouldBe([new DayRange(Day(0), Day(1)), new DayRange(Day(3), Day(3)), new DayRange(Day(5), Day(5))]);
        ledger.Runs(4).ShouldBeEmpty();
    }

    [Fact]
    public void Packed_rates_are_sorted_by_day_and_carry_the_cents()
    {
        NightLedger ledger = Apply(new NightLedger(), 1, (2, 1, 25050), (0, 1, 19900));

        long[] packed = ledger.PackedRates();

        packed.ShouldBe([((long)Day(0) << 32) | 19900, ((long)Day(2) << 32) | 25050]);
    }
}
