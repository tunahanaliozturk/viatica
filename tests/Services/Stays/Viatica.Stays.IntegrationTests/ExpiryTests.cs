using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Viatica.Contracts;
using Viatica.Stays.Application.Features.Holds;
using Viatica.Stays.Application.Features.Quotes;
using Viatica.Stays.Domain;
using Viatica.Testing;

namespace Viatica.Stays.IntegrationTests;

/// <summary>
/// A hold lives for fifteen minutes. The sweeper judges that on the host's clock, which the tests move, and ticks on
/// the real one, so a test moves the clock and waits for the next tick.
/// </summary>
public sealed class ExpiryTests(StaysFixture stays) : IClassFixture<StaysFixture>
{
    private StaysHost Host => stays.Host;

    private static bool IsAbout(ProbedMessage m, string type, Guid holdId) => m.Type == type && m.DataString("holdId") == holdId.ToString();

    private static async Task<(Scenario Scenario, StayHoldRequested Request)> PlaceAsync(StaysFixture stays, EventProbe probe, int quantity = 2)
    {
        var scenario = await Scenario.CreateAsync(stays.Host, allotment: 10);
        Quote quote = await scenario.QuoteAsync(quantity: quantity);
        StayHoldRequested request = Scenario.HoldRequest(quote.QuoteToken, quantity);
        await stays.Booking.SendAsync(request);
        await probe.WaitForAsync(m => IsAbout(m, "StayHoldPlaced", request.HoldId));
        return (scenario, request);
    }

    [Fact]
    public async Task A_hold_nobody_commits_is_expired_by_the_sweeper_and_its_rooms_come_back()
    {
        using EventProbe probe = stays.Probe();
        (Scenario scenario, StayHoldRequested request) = await PlaceAsync(stays, probe);

        Host.Clock.Advance(Hold.Lifetime - TimeSpan.FromSeconds(1));
        await Task.Delay(600);
        (await scenario.NightsAsync()).Count(n => n.Held == 2).ShouldBe(3, "still live one second before the end");

        Host.Clock.Advance(TimeSpan.FromSeconds(1));
        ProbedMessage expired = await probe.WaitForAsync(m => IsAbout(m, "StayHoldExpired", request.HoldId));
        expired.Key.ShouldBe(request.TripId.ToString("N"));
        expired.Data.GetProperty("itemId").GetGuid().ShouldBe(request.ItemId);

        (await scenario.NightsAsync()).ShouldAllBe(n => n.Held == 0 && n.Sold == 0 && n.Available == 10);
        (await Host.InDbAsync(db => db.Holds.AsNoTracking().Where(h => h.Id == request.HoldId).Select(static h => h.Status).SingleAsync())).ShouldBe(HoldStatus.Expired);

        // The rooms are announced as free again, under the next version.
        IReadOnlyList<ProbedMessage> changes = await scenario.EventsAsync(probe, "StayAvailabilityChanged", 3);
        changes.Select(static m => Scenario.Long(m, "availabilityVersion")).ShouldBe([1, 2, 3]);
    }

    [Fact]
    public async Task A_late_commit_or_release_of_an_expired_hold_changes_nothing()
    {
        using EventProbe probe = stays.Probe();
        (Scenario scenario, StayHoldRequested request) = await PlaceAsync(stays, probe);
        Host.Clock.Advance(Hold.Lifetime);
        await probe.WaitForAsync(m => IsAbout(m, "StayHoldExpired", request.HoldId));

        var marker = new StayHoldReleaseRequested(request.TripId, Guid.CreateVersion7(), Guid.CreateVersion7());
        await stays.Booking.SendAsync(
            new StayHoldCommitRequested(request.TripId, request.ItemId, request.HoldId),
            new StayHoldReleaseRequested(request.TripId, request.ItemId, request.HoldId),
            marker);
        await probe.WaitForAsync(m => IsAbout(m, "StayHoldReleased", marker.HoldId));

        probe.Received.ShouldNotContain(m => IsAbout(m, "StayHoldCommitted", request.HoldId));
        probe.Received.ShouldNotContain(m => IsAbout(m, "StayHoldReleased", request.HoldId));
        (await scenario.NightsAsync()).ShouldAllBe(n => n.Held == 0 && n.Sold == 0);
        (await scenario.AvailabilityVersionAsync()).ShouldBe(3, "set-up, hold, expiry, and nothing since");
    }
}

/// <summary>The sweeper held back, so that a test decides exactly when expiry happens relative to a commit.</summary>
public sealed class ManualExpiryTests(ManualSweepFixture stays) : IClassFixture<ManualSweepFixture>
{
    private StaysHost Host => stays.Host;

    private static bool IsAbout(ProbedMessage m, string type, Guid holdId) => m.Type == type && m.DataString("holdId") == holdId.ToString();

    private async Task<bool> ExpireAsync(Guid holdId)
    {
        await using AsyncServiceScope scope = Host.Services.CreateAsyncScope();
        return await scope.ServiceProvider.GetRequiredService<ExpireDueHolds>().ExpireAsync(holdId, CancellationToken.None);
    }

    [Fact]
    public async Task A_commit_after_the_expiry_is_refused_even_before_the_sweeper_has_noticed()
    {
        using EventProbe probe = stays.Probe();
        var scenario = await Scenario.CreateAsync(Host, allotment: 10);
        Quote quote = await scenario.QuoteAsync(quantity: 2);
        StayHoldRequested request = Scenario.HoldRequest(quote.QuoteToken, 2);
        await stays.Booking.SendAsync(request);
        await probe.WaitForAsync(m => IsAbout(m, "StayHoldPlaced", request.HoldId));

        Host.Clock.Advance(Hold.Lifetime);
        var marker = new StayHoldReleaseRequested(request.TripId, Guid.CreateVersion7(), Guid.CreateVersion7());
        await stays.Booking.SendAsync(new StayHoldCommitRequested(request.TripId, request.ItemId, request.HoldId), marker);
        await probe.WaitForAsync(m => IsAbout(m, "StayHoldReleased", marker.HoldId));

        probe.Received.ShouldNotContain(m => IsAbout(m, "StayHoldCommitted", request.HoldId));
        (await scenario.NightsAsync()).Count(n => n.Held == 2 && n.Sold == 0).ShouldBe(3, "refused, not sold; the sweeper has not run");

        // The sweeper then does its work, and the saga hears it.
        (await ExpireAsync(request.HoldId)).ShouldBeTrue();
        await probe.WaitForAsync(m => IsAbout(m, "StayHoldExpired", request.HoldId));
        (await scenario.NightsAsync()).ShouldAllBe(n => n.Held == 0 && n.Sold == 0);
    }

    [Fact]
    public async Task Of_many_sweepers_reaching_one_hold_exactly_one_expires_it()
    {
        using EventProbe probe = stays.Probe();
        var scenario = await Scenario.CreateAsync(Host, allotment: 10);
        Quote quote = await scenario.QuoteAsync(quantity: 3);
        StayHoldRequested request = Scenario.HoldRequest(quote.QuoteToken, 3);
        await stays.Booking.SendAsync(request);
        await probe.WaitForAsync(m => IsAbout(m, "StayHoldPlaced", request.HoldId));
        Host.Clock.Advance(Hold.Lifetime);

        bool[] results = await Task.WhenAll(Enumerable.Range(0, 12).Select(_ => Task.Run(() => ExpireAsync(request.HoldId))));

        results.Count(static r => r).ShouldBe(1);
        (await scenario.NightsAsync()).ShouldAllBe(n => n.Held == 0 && n.Available == 10, "returned once, not once per sweeper");
        await probe.WaitForAsync(m => IsAbout(m, "StayHoldExpired", request.HoldId));
        (await scenario.AvailabilityVersionAsync()).ShouldBe(3);
    }

    [Fact]
    public async Task The_sweeper_finds_due_holds_oldest_first_and_only_those()
    {
        using EventProbe probe = stays.Probe();
        var scenario = await Scenario.CreateAsync(Host, allotment: 10);
        Quote quote = await scenario.QuoteAsync();
        StayHoldRequested early = Scenario.HoldRequest(quote.QuoteToken);
        await stays.Booking.SendAsync(early);
        await probe.WaitForAsync(m => IsAbout(m, "StayHoldPlaced", early.HoldId));
        Host.Clock.Advance(TimeSpan.FromMinutes(10));

        Quote later = await scenario.QuoteAsync();
        StayHoldRequested late = Scenario.HoldRequest(later.QuoteToken);
        await stays.Booking.SendAsync(late);
        await probe.WaitForAsync(m => IsAbout(m, "StayHoldPlaced", late.HoldId));
        Host.Clock.Advance(TimeSpan.FromMinutes(6));

        await using AsyncServiceScope scope = Host.Services.CreateAsyncScope();
        IReadOnlyList<Guid> due = await scope.ServiceProvider.GetRequiredService<ExpireDueHolds>().FindDueAsync(10, CancellationToken.None);

        due.ShouldBe([early.HoldId], "the later hold has four minutes left");
    }
}
