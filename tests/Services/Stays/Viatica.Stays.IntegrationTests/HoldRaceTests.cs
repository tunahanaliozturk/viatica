using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Viatica.Contracts;
using Viatica.Stays.Application.Features.Holds;
using Viatica.Stays.Application.Features.Quotes;
using Viatica.Stays.Domain;
using Viatica.Stays.Infrastructure;
using Viatica.Testing;

namespace Viatica.Stays.IntegrationTests;

/// <summary>
/// The claim the service is built around: for every room type and night, <c>sold + held &lt;= allotment</c>, however
/// many requests arrive at once. Each request here runs the handler in a transaction and a DbContext of its own, as
/// a consumer would, so the contention is between real connections and real row locks.
/// </summary>
/// <remarks>
/// The host's pool allows 60 connections, so 200 requests start together and queue for a connection; the ones that
/// hold one contend for the same rows.
/// </remarks>
public sealed class HoldRaceTests(RaceFixture stays) : IClassFixture<RaceFixture>
{
    private StaysHost Host => stays.Host;

    private static bool IsAbout(ProbedMessage m, string type, Guid holdId) => m.Type == type && m.DataString("holdId") == holdId.ToString();

    /// <summary>One hold request as the consumer runs it: handler, save and commit in one transaction.</summary>
    private async Task HandleAsync(StayHoldRequested request)
    {
        await using AsyncServiceScope scope = Host.Services.CreateAsyncScope();
        StaysDbContext db = scope.ServiceProvider.GetRequiredService<StaysDbContext>();
        await using Microsoft.EntityFrameworkCore.Storage.IDbContextTransaction transaction = await db.Database.BeginTransactionAsync();

        await scope.ServiceProvider.GetRequiredService<PlaceHoldHandler>().HandleAsync(request, CancellationToken.None);
        await db.SaveChangesAsync();
        await transaction.CommitAsync();
    }

    private async Task RunTogetherAsync(IEnumerable<StayHoldRequested> requests)
    {
        var start = new TaskCompletionSource();
        Task[] running = [.. requests.Select(request => Task.Run(async () =>
        {
            await start.Task;
            await HandleAsync(request);
        }))];

        start.SetResult();
        await Task.WhenAll(running);
    }

    [Fact]
    public async Task Two_hundred_parallel_holds_for_the_last_ten_rooms_make_exactly_ten()
    {
        using EventProbe probe = stays.Probe();
        var scenario = await Scenario.CreateAsync(Host, allotment: 10);
        Quote quote = await scenario.QuoteAsync(quantity: 1);
        StayHoldRequested[] requests = [.. Enumerable.Range(0, 200).Select(_ => Scenario.HoldRequest(quote.QuoteToken))];

        await RunTogetherAsync(requests);

        // The database: ten holds, every night of the stay full, none overbooked.
        (await Host.InDbAsync(db => db.Holds.CountAsync(h => h.PropertyId == scenario.PropertyId))).ShouldBe(10);
        List<Night> nights = await scenario.NightsAsync();
        nights.Where(n => n.Date >= StaysHost.CheckIn && n.Date < StaysHost.CheckOut).ShouldAllBe(n => n.Held == 10 && n.Available == 0);
        nights.ShouldAllBe(n => n.Sold + n.Held <= n.Allotment);

        // The answers: ten placed, a hundred and ninety told it is sold out, and each request answered exactly once.
        var ids = requests.Select(static r => r.HoldId).ToHashSet();
        IReadOnlyList<ProbedMessage> placed = await probe.WaitForCountAsync(10, m => m.Type == "StayHoldPlaced" && ids.Contains(Guid.Parse(m.DataString("holdId")!)));
        IReadOnlyList<ProbedMessage> rejected = await probe.WaitForCountAsync(190, m => m.Type == "StayHoldRejected" && ids.Contains(Guid.Parse(m.DataString("holdId")!)));
        placed.Count.ShouldBe(10);
        rejected.Count.ShouldBe(190);
        rejected.ShouldAllBe(m => m.DataString("reason") == "SoldOut");
        placed.Concat(rejected).Select(static m => m.DataString("holdId")).Distinct().Count().ShouldBe(200);

        // One announcement per change, with versions that rise in the order the changes committed and have no gaps.
        IReadOnlyList<ProbedMessage> changes = await scenario.EventsAsync(probe, "StayAvailabilityChanged", 11);
        changes.Select(static m => Scenario.Long(m, "availabilityVersion")).ShouldBe(Enumerable.Range(1, 11).Select(static i => (long)i));
        Scenario.Nights(changes[^1])[0].GetProperty("available").GetInt32().ShouldBe(0);
    }

    [Fact]
    public async Task Overlapping_stays_never_oversell_a_night_and_a_refused_stay_leaves_no_trace()
    {
        using EventProbe probe = stays.Probe();
        var scenario = await Scenario.CreateAsync(Host, allotment: 6);

        // Three nights, three shapes of stay over them: the first two, the last two, and all three. The middle night is
        // wanted by every stay, so it runs out first and refuses stays that had already won the other night.
        DateOnly d1 = StaysHost.CheckIn;
        (DateOnly In, DateOnly Out)[] shapes = [(d1, d1.AddDays(2)), (d1.AddDays(1), d1.AddDays(3)), (d1, d1.AddDays(3))];
        var random = new Random(20261024);
        var requests = new List<StayHoldRequested>();
        var rooms = new Dictionary<Guid, (DateOnly In, DateOnly Out, int Quantity)>();

        foreach ((DateOnly stayIn, DateOnly stayOut) in shapes)
        {
            foreach (int quantity in new[] { 1, 2 })
            {
                Quote quote = await scenario.QuoteAsync(quantity: quantity, guests: 1, checkIn: stayIn, checkOut: stayOut);
                for (int i = 0; i < 25; i++)
                {
                    StayHoldRequested request = Scenario.HoldRequest(quote.QuoteToken, quantity);
                    requests.Add(request);
                    rooms[request.HoldId] = (stayIn, stayOut, quantity);
                }
            }
        }

        await RunTogetherAsync(requests.OrderBy(_ => random.Next()));

        // Whatever won, the nights agree with the holds, and no night is over its allotment.
        List<Night> nights = await scenario.NightsAsync();
        List<Hold> held = await Host.InDbAsync(db => db.Holds.AsNoTracking().Where(h => h.PropertyId == scenario.PropertyId).ToListAsync());

        held.ShouldNotBeEmpty();
        held.Count.ShouldBeLessThan(requests.Count);
        foreach (Night night in nights)
        {
            int expected = held.Where(h => h.CheckIn <= night.Date && night.Date < h.CheckOut).Sum(static h => h.Quantity);
            night.Held.ShouldBe(expected, $"night {night.Date}: the counter must equal the holds that cover it");
            (night.Sold + night.Held).ShouldBeLessThanOrEqualTo(night.Allotment, $"night {night.Date}");
        }

        // Every request was answered once, either way.
        var all = requests.Select(static r => r.HoldId).ToHashSet();
        IReadOnlyList<ProbedMessage> answers = await probe.WaitForCountAsync(
            requests.Count,
            m => (m.Type is "StayHoldPlaced" or "StayHoldRejected") && all.Contains(Guid.Parse(m.DataString("holdId")!)));
        answers.Select(static m => m.DataString("holdId")).Distinct().Count().ShouldBe(requests.Count);
        answers.Count(static m => m.Type == "StayHoldPlaced").ShouldBe(held.Count);
    }

    [Fact]
    public async Task Commits_and_releases_racing_new_holds_keep_the_books_balanced()
    {
        var scenario = await Scenario.CreateAsync(Host, allotment: 20);
        Quote quote = await scenario.QuoteAsync(quantity: 1);
        StayHoldRequested[] first = [.. Enumerable.Range(0, 20).Select(_ => Scenario.HoldRequest(quote.QuoteToken))];
        await RunTogetherAsync(first);
        (await scenario.NightsAsync()).Where(n => n.Date >= StaysHost.CheckIn && n.Date < StaysHost.CheckOut).ShouldAllBe(n => n.Held == 20);

        // Half are committed and half released while forty more ask for the rooms the releases free.
        StayHoldRequested[] more = [.. Enumerable.Range(0, 40).Select(_ => Scenario.HoldRequest(quote.QuoteToken))];
        var work = new List<Task>();
        var start = new TaskCompletionSource();
        foreach ((StayHoldRequested hold, int index) in first.Select((hold, index) => (hold, index)))
        {
            work.Add(Task.Run(async () =>
            {
                await start.Task;
                await using AsyncServiceScope scope = Host.Services.CreateAsyncScope();
                StaysDbContext db = scope.ServiceProvider.GetRequiredService<StaysDbContext>();
                await using Microsoft.EntityFrameworkCore.Storage.IDbContextTransaction transaction = await db.Database.BeginTransactionAsync();
                if (index % 2 == 0)
                {
                    await scope.ServiceProvider.GetRequiredService<CommitHoldHandler>().HandleAsync(new StayHoldCommitRequested(hold.TripId, hold.ItemId, hold.HoldId), CancellationToken.None);
                }
                else
                {
                    await scope.ServiceProvider.GetRequiredService<ReleaseHoldHandler>().HandleAsync(new StayHoldReleaseRequested(hold.TripId, hold.ItemId, hold.HoldId), CancellationToken.None);
                }

                await db.SaveChangesAsync();
                await transaction.CommitAsync();
            }));
        }

        work.AddRange(more.Select(request => Task.Run(async () =>
        {
            await start.Task;
            await HandleAsync(request);
        })));
        start.SetResult();
        await Task.WhenAll(work);

        List<Night> nights = await scenario.NightsAsync();
        List<Hold> holds = await Host.InDbAsync(db => db.Holds.AsNoTracking().Where(h => h.PropertyId == scenario.PropertyId).ToListAsync());
        foreach (Night night in nights)
        {
            int covering(HoldStatus status) => holds.Where(h => h.Status == status && h.CheckIn <= night.Date && night.Date < h.CheckOut).Sum(static h => h.Quantity);
            night.Sold.ShouldBe(covering(HoldStatus.Committed), $"sold on {night.Date}");
            night.Held.ShouldBe(covering(HoldStatus.Held), $"held on {night.Date}");
            (night.Sold + night.Held).ShouldBeLessThanOrEqualTo(night.Allotment);
        }

        holds.Count(static h => h.Status == HoldStatus.Committed).ShouldBe(10);
        holds.Count(static h => h.Status == HoldStatus.Released).ShouldBe(10);
    }
}
