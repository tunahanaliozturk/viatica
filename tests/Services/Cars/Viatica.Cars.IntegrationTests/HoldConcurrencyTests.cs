using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Relevox;
using Viatica.Cars.Application.Features.Holds;
using Viatica.Cars.Application.Integration;
using Viatica.Cars.Domain;
using Viatica.Contracts;
using Viatica.Testing;

namespace Viatica.Cars.IntegrationTests;

/// <summary>
/// The claim in the requirements: many travellers racing for the last cars of a day produce exactly as many holds as
/// there are cars, never one more. Every task below is its own consumer: its own scope, its own connection, its own
/// transaction, started together.
/// </summary>
public sealed class HoldConcurrencyTests(PostgresFixture postgres, KafkaFixture kafka, ValkeyFixture valkey) : IAsyncLifetime
{
    private CarsHost host = null!;

    public async ValueTask InitializeAsync() => host = await CarsHost.StartAsync(postgres, kafka, valkey);

    public ValueTask DisposeAsync()
    {
        host.Dispose();
        return ValueTask.CompletedTask;
    }

    private static DateTimeOffset Monday => Scenario.Monday;

    private async Task<string[]> RaceAsync(string token, int contenders, int driverAge = 35)
    {
        using var start = new SemaphoreSlim(0);
        Task<string>[] races =
        [
            .. Enumerable.Range(0, contenders).Select(_ => Task.Run(async () =>
            {
                await start.WaitAsync();
                return (await host.PlaceAsync(token, driverAge)).Describe();
            })),
        ];
        start.Release(contenders);
        return await Task.WhenAll(races);
    }

    [Fact]
    public async Task Sixty_checkouts_racing_for_the_last_five_cars_of_a_day_produce_exactly_five_holds()
    {
        Marketplace m = await Scenario.ArrangeAsync(host, allotment: 5);
        string token = await Scenario.TokenAsync(host.Anonymous(), m, Monday, Monday.AddHours(24));

        string[] results = await RaceAsync(token, contenders: 60);

        results.Count(static r => r == "placed").ShouldBe(5);
        results.Count(static r => r == "SoldOut").ShouldBe(55);

        List<InventoryDay> days = await host.DaysAsync(m, "2026-12-07", "2026-12-07");
        days.Single().Held.ShouldBe(5);
        days.Single().Sold.ShouldBe(0);
        (await host.QueryAsync(db => db.Holds.CountAsync())).ShouldBe(5);
        await host.AssertInventoryIsConsistentAsync(m);
    }

    [Fact]
    public async Task Three_hundred_checkouts_racing_for_the_last_ten_cars_of_a_day_produce_exactly_ten_holds()
    {
        Marketplace m = await Scenario.ArrangeAsync(host, allotment: 10);
        string token = await Scenario.TokenAsync(host.Anonymous(), m, Monday, Monday.AddHours(24));

        // Through the same handler the consumer calls, so every contender is answered with an event.
        using var start = new SemaphoreSlim(0);
        Task[] contenders =
        [
            .. Enumerable.Range(0, 300).Select(_ => Task.Run(async () =>
            {
                var request = new CarHoldRequested(Guid.CreateVersion7(), Guid.CreateVersion7(), Guid.CreateVersion7(), token, 35);
                await start.WaitAsync();
                await host.AsConsumerAsync(async services =>
                {
                    await services.GetRequiredService<CarHoldRequestedHandler>().HandleAsync(request, CancellationToken.None);
                    return 0;
                });
            })),
        ];
        start.Release(contenders.Length);
        await Task.WhenAll(contenders);

        (await host.DaysAsync(m, "2026-12-07", "2026-12-07")).Single().Held.ShouldBe(10);
        (await host.QueryAsync(db => db.Holds.CountAsync())).ShouldBe(10);
        await host.AssertInventoryIsConsistentAsync(m);

        // The answers Booking gets: ten holds and two hundred and ninety refusals, nothing lost on the way.
        IReadOnlyList<ProbedMessage> placed = await host.Probe.WaitForCountAsync(10, static e => e.Type == "CarHoldPlaced");
        IReadOnlyList<ProbedMessage> rejected = await host.Probe.WaitForCountAsync(290, static e => e.Type == "CarHoldRejected");
        placed.Count.ShouldBe(10);
        rejected.ShouldAllBe(static e => e.DataString("reason") == "SoldOut");
    }

    [Fact]
    public async Task The_last_car_of_a_day_goes_to_exactly_one_of_twenty_contenders_again_and_again()
    {
        // Twenty rounds, so a race that is only lost one time in a thousand would still show.
        for (int round = 0; round < 20; round++)
        {
            Marketplace m = await Scenario.ArrangeAsync(host, allotment: 1);
            string token = await Scenario.TokenAsync(host.Anonymous(), m, Monday, Monday.AddHours(24));

            string[] results = await RaceAsync(token, contenders: 20);

            results.Count(static r => r == "placed").ShouldBe(1, $"round {round}");
            await host.AssertInventoryIsConsistentAsync(m);
        }
    }

    [Fact]
    public async Task A_rental_holds_every_one_of_its_days_or_none_so_the_scarcest_day_sets_the_winners()
    {
        Marketplace m = await Scenario.ArrangeAsync(host, allotment: 5);
        // Tuesday the 8th has two cars; the 7th and the 9th have five. A three day rental needs all three.
        await Scenario.Ok(m.Client.PostAsync(
            $"/cars/locations/{m.LocationId}/categories/{m.CategoryId}/inventory/bulk", Scenario.Inventory("2026-12-08", "2026-12-08", 2, 40m)));
        string token = await Scenario.TokenAsync(host.Anonymous(), m, Monday, Monday.AddHours(72));

        string[] results = await RaceAsync(token, contenders: 25);

        results.Count(static r => r == "placed").ShouldBe(2);
        List<InventoryDay> days = await host.DaysAsync(m, "2026-12-07", "2026-12-09");
        days.Select(static d => d.Held).ShouldBe([2, 2, 2]);   // the losers left nothing behind on the roomy days
        await host.AssertInventoryIsConsistentAsync(m);
    }

    [Fact]
    public async Task A_missing_day_is_sold_out_not_an_error_and_leaves_the_other_days_alone()
    {
        Marketplace m = await Scenario.ArrangeAsync(host, allotment: 5);
        // Inventory exists for the whole of December, so quote a rental on the 30th and 31st, then delete the 31st.
        DateTimeOffset start = new(2026, 12, 30, 10, 0, 0, TimeSpan.Zero);
        string token = await Scenario.TokenAsync(host.Anonymous(), m, start, start.AddHours(48));
        await host.QueryAsync(db => db.InventoryDays.Where(d => d.Date == new DateOnly(2026, 12, 31)).ExecuteDeleteAsync());

        HoldOutcome outcome = await host.PlaceAsync(token);

        outcome.Describe().ShouldBe("SoldOut");
        (await host.DaysAsync(m, "2026-12-30", "2026-12-30")).Single().Held.ShouldBe(0);
    }

    [Fact]
    public async Task Of_a_hold_committed_and_expired_at_the_same_moment_exactly_one_wins_and_the_counters_agree()
    {
        const int Holds = 12;
        Marketplace m = await Scenario.ArrangeAsync(host, allotment: Holds);
        string token = await Scenario.TokenAsync(host.Anonymous(), m, Monday, Monday.AddHours(24));
        Guid[] holdIds = [.. Enumerable.Range(0, Holds).Select(static _ => Guid.CreateVersion7())];
        var trips = new Dictionary<Guid, (Guid Trip, Guid Item)>();
        foreach (Guid holdId in holdIds)
        {
            trips[holdId] = (Guid.CreateVersion7(), Guid.CreateVersion7());
            HoldOutcome outcome = await host.AsConsumerAsync(sp => sp.GetRequiredService<ISender>()
                .Send(new PlaceHold(trips[holdId].Trip, trips[holdId].Item, holdId, token, 35)));
            outcome.Describe().ShouldBe("placed");
        }

        host.Clock.Advance(CarHold.Lifetime + TimeSpan.FromSeconds(1));

        // The commit arrives just after the sweeper's deadline, before the sweeper has run: both want each hold.
        using var start = new SemaphoreSlim(0);
        Task<bool>[] attempts =
        [
            .. holdIds.SelectMany(holdId => new[]
            {
                Attempt(start, () => host.SendAsync(new CommitHold(trips[holdId].Trip, trips[holdId].Item, holdId))),
                Attempt(start, () => host.SendAsync(new ExpireHold(holdId))),
            }),
        ];
        start.Release(attempts.Length);
        bool[] applied = await Task.WhenAll(attempts);

        // Exactly one side applied per hold; the other lost to the row version or found it already moved.
        applied.Count(static a => a).ShouldBe(Holds);
        foreach (Guid holdId in holdIds)
        {
            (await host.HoldRowAsync(holdId)).Status.ShouldBeOneOf(HoldStatus.Committed, HoldStatus.Expired);
        }

        await host.AssertInventoryIsConsistentAsync(m);
        List<InventoryDay> day = await host.DaysAsync(m, "2026-12-07", "2026-12-07");
        day.Single().Held.ShouldBe(0);
    }

    private static Task<bool> Attempt(SemaphoreSlim start, Func<Task<Transition>> work) =>
        Task.Run(async () =>
        {
            await start.WaitAsync();
            try
            {
                return await work() == Transition.Applied;
            }
            catch (DbUpdateConcurrencyException)
            {
                // The other writer saved the hold first. In production the consumer retries and finds it moved.
                return false;
            }
        });
}
