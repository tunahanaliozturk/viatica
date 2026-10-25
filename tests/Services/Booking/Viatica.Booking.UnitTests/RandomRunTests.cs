using Viatica.Booking.Domain;

namespace Viatica.Booking.UnitTests;

/// <summary>
/// BKG-3 as a property: whatever mix of items, whatever the owners answer, whenever they answer, however often they
/// answer, the trip ends Confirmed with everything held and paid for, or Failed with nothing left held, ordered or
/// authorised. The owners are modelled in <see cref="SagaWorld"/> from their service docs; the order in which their
/// answers arrive, the repeats, the sweepers and the clock are chosen by a seeded random generator, so a failure
/// names the seed that reproduces it.
/// </summary>
public sealed class RandomRunTests
{
    private const int Runs = 20_000;

    [Fact]
    public void Twenty_thousand_random_runs_with_each_owner_answering_in_order_end_in_a_legal_state()
    {
        List<string> failures = [];
        for (int seed = 1; seed <= Runs; seed++)
        {
            failures.AddRange(Run(seed, anyOrder: false));
        }

        failures.ShouldBeEmpty(string.Join(Environment.NewLine, failures.Take(10)));
    }

    [Fact]
    public void Twenty_thousand_random_runs_with_answers_in_any_order_end_in_a_legal_state()
    {
        List<string> failures = [];
        for (int seed = 10_001; seed <= 10_000 + Runs; seed++)
        {
            failures.AddRange(Run(seed, anyOrder: true));
        }

        failures.ShouldBeEmpty(string.Join(Environment.NewLine, failures.Take(10)));
    }

    [Fact]
    public void The_runs_reach_every_end_the_saga_has_so_the_property_is_not_vacuous()
    {
        Dictionary<TripStatus, int> ends = [];
        Dictionary<FailureReason, int> reasons = [];
        int late = 0;
        int voidsAfterCommit = 0;

        for (int seed = 1; seed <= Runs; seed++)
        {
            SagaWorld world = Play(seed, anyOrder: false);
            ends[world.Trip.Status] = ends.GetValueOrDefault(world.Trip.Status) + 1;
            if (world.Trip.Failure is { } reason && world.Trip.Status == TripStatus.Failed)
            {
                reasons[reason] = reasons.GetValueOrDefault(reason) + 1;
            }

            late += world.LateAfterFailure;
            voidsAfterCommit += world.Sent.OfType<CancelBooking>().Count();
        }

        ends.ShouldContainKey(TripStatus.Confirmed);
        ends.ShouldContainKey(TripStatus.Failed);
        reasons.Keys.ShouldBe(Enum.GetValues<FailureReason>(), ignoreOrder: true);
        voidsAfterCommit.ShouldBeGreaterThan(0, "no run unwound a committed stay or car");
        late.ShouldBeGreaterThan(0, "no run had a hold or an order appear after the trip gave up");
    }

    private static List<string> Run(int seed, bool anyOrder)
    {
        SagaWorld world = Play(seed, anyOrder);
        return [.. world.Violations().Select(v => $"seed {seed} (anyOrder {anyOrder}): {v}; items {Describe(world)}; sent {world.Sent.Count}")];
    }

    private static string Describe(SagaWorld world) =>
        string.Join(", ", world.Trip.Items.Select(i => $"{i.Kind}:{i.Status}/{i.Awaiting}")) + $" trip {world.Trip.Status}/{world.Trip.Failure} pay {world.Trip.Payment}/{world.PaymentState}";

    private static SagaWorld Play(int seed, bool anyOrder)
    {
        var random = new Random(seed);
        int count = random.Next(1, 5);
        List<(ItemKind, Fate)> items = [];
        for (int i = 0; i < count; i++)
        {
            ItemKind kind = (ItemKind)random.Next(3);
            Fate fate = kind == ItemKind.Flight
                ? Pick(random, (Fate.Succeed, 50), (Fate.Refuse, 12), (Fate.RefuseThenSucceedLate, 10), (Fate.RefuseTicketing, 18), (Fate.VoidWindowClosed, 10))
                : Pick(random, (Fate.Succeed, 75), (Fate.Refuse, 15), (Fate.RefuseThenSucceedLate, 10));
            items.Add((kind, fate));
        }

        (SagaWorld world, _) = SagaWorld.CheckedOut(new WorldScript(items, DeclinePayment: random.Next(100) < 12), seed);
        int biggestJump = random.Next(5, 900);

        for (int step = 0; step < 90; step++)
        {
            int roll = world.Random.Next(100);
            if (roll < 30)
            {
                _ = anyOrder ? world.DeliverAny() : world.DeliverFromRandomOwner();
            }
            else if (roll < 56)
            {
                world.ProcessFromRandomOwner();
            }
            else if (roll < 66)
            {
                world.Duplicate();
            }
            else if (roll < 76)
            {
                world.Advance(TimeSpan.FromSeconds(world.Random.Next(1, biggestJump)));
            }
            else if (roll < 84)
            {
                world.Sweep();
            }
            else if (roll < 92)
            {
                world.RunLateWork();
            }
            else
            {
                world.RunChecks();
            }
        }

        world.Settle();
        return world;
    }

    private static Fate Pick(Random random, params (Fate Fate, int Weight)[] weighted)
    {
        int roll = random.Next(weighted.Sum(static w => w.Weight));
        foreach ((Fate fate, int weight) in weighted)
        {
            roll -= weight;
            if (roll < 0)
            {
                return fate;
            }
        }

        return weighted[0].Fate;
    }
}
