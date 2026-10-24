using Microsoft.Extensions.Time.Testing;
using Viatica.Kernel.Web;

namespace Viatica.Kernel.UnitTests;

public sealed class RepeatabilityStoreTests
{
    private static readonly TimeSpan Lease = TimeSpan.FromSeconds(30);

    private static RepeatableResponse Done(string owner) =>
        new("fp", true, 201, "application/json", null, "{}"u8.ToArray(), owner);

    /// <summary>
    /// The first request outlives its claim and a second one takes the id. When the first finally completes or fails,
    /// it must leave the second one's claim alone.
    /// </summary>
    [Fact]
    public async Task A_request_that_lost_its_claim_cannot_complete_or_release_the_next_one()
    {
        var clock = new FakeTimeProvider(DateTimeOffset.UtcNow);
        var store = new InMemoryRepeatabilityStore(clock);
        (await store.TryClaimAsync("k", RepeatableResponse.Claim("fp", "first"), Lease, default)).ShouldBeNull();
        clock.Advance(Lease + TimeSpan.FromSeconds(1));
        (await store.TryClaimAsync("k", RepeatableResponse.Claim("fp", "second"), Lease, default)).ShouldBeNull();

        await store.CompleteAsync("k", Done("first"), Lease, default);
        (await store.TryClaimAsync("k", RepeatableResponse.Claim("fp", "third"), Lease, default))!.Completed.ShouldBeFalse();

        await store.ReleaseAsync("k", "first", default);
        (await store.TryClaimAsync("k", RepeatableResponse.Claim("fp", "third"), Lease, default)).ShouldNotBeNull();

        await store.CompleteAsync("k", Done("second"), Lease, default);
        (await store.TryClaimAsync("k", RepeatableResponse.Claim("fp", "third"), Lease, default))!.Completed.ShouldBeTrue();
    }

    [Fact]
    public async Task Only_the_owner_can_renew_and_a_lapsed_claim_cannot_be_renewed()
    {
        var clock = new FakeTimeProvider(DateTimeOffset.UtcNow);
        var store = new InMemoryRepeatabilityStore(clock);
        (await store.TryClaimAsync("k", RepeatableResponse.Claim("fp", "first"), Lease, default)).ShouldBeNull();

        (await store.RenewAsync("k", "someone-else", Lease, default)).ShouldBeFalse();
        clock.Advance(TimeSpan.FromSeconds(20));
        (await store.RenewAsync("k", "first", Lease, default)).ShouldBeTrue();

        // Renewed at +20 s for 30 s: still held at +45 s, gone at +51 s.
        clock.Advance(TimeSpan.FromSeconds(25));
        (await store.TryClaimAsync("k", RepeatableResponse.Claim("fp", "second"), Lease, default)).ShouldNotBeNull();
        clock.Advance(TimeSpan.FromSeconds(6));
        (await store.RenewAsync("k", "first", Lease, default)).ShouldBeFalse();
    }

    [Fact]
    public async Task A_response_is_kept_when_the_claim_lapsed_and_nobody_took_the_id()
    {
        var clock = new FakeTimeProvider(DateTimeOffset.UtcNow);
        var store = new InMemoryRepeatabilityStore(clock);
        (await store.TryClaimAsync("k", RepeatableResponse.Claim("fp", "first"), Lease, default)).ShouldBeNull();
        clock.Advance(Lease + TimeSpan.FromSeconds(1));

        await store.CompleteAsync("k", Done("first"), TimeSpan.FromMinutes(5), default);

        (await store.TryClaimAsync("k", RepeatableResponse.Claim("fp", "second"), Lease, default))!.Completed.ShouldBeTrue();
    }
}
