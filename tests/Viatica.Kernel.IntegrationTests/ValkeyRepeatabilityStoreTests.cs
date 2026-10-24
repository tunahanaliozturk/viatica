using StackExchange.Redis;
using Testcontainers.Redis;
using Viatica.Kernel.Web;

namespace Viatica.Kernel.IntegrationTests;

/// <summary>The same image and version compose runs, so the claim is tested against the server it will meet.</summary>
public sealed class ValkeyFixture : IAsyncLifetime
{
    private readonly RedisContainer container = new RedisBuilder("valkey/valkey:8.1.10-alpine").Build();

    public ConnectionMultiplexer Valkey { get; private set; } = null!;

    public async ValueTask InitializeAsync()
    {
        await container.StartAsync();
        Valkey = await ConnectionMultiplexer.ConnectAsync(container.GetConnectionString());
    }

    public async ValueTask DisposeAsync()
    {
        await Valkey.DisposeAsync();
        await container.DisposeAsync();
    }
}

public sealed class ValkeyRepeatabilityStoreTests(ValkeyFixture fixture) : IClassFixture<ValkeyFixture>
{
    private static readonly TimeSpan Minute = TimeSpan.FromMinutes(1);

    private ValkeyRepeatabilityStore Store => new(fixture.Valkey);

    private static string NewKey() => "test:" + Guid.NewGuid();

    [Fact]
    public async Task Of_many_simultaneous_claims_exactly_one_wins()
    {
        string key = NewKey();
        using var start = new SemaphoreSlim(0);

        // One store per caller, as separate service instances would have.
        Task<RepeatableResponse?>[] claims = [.. Enumerable.Range(0, 64).Select(_ => Task.Run(async () =>
        {
            var store = new ValkeyRepeatabilityStore(fixture.Valkey);
            await start.WaitAsync();
            return await store.TryClaimAsync(key, RepeatableResponse.Claim("fp"), Minute, CancellationToken.None);
        }))];
        start.Release(64);
        RepeatableResponse?[] results = await Task.WhenAll(claims);

        results.Count(static r => r is null).ShouldBe(1);
        results.Where(static r => r is not null).ShouldAllBe(static r => !r!.Completed && r.Fingerprint == "fp");
    }

    [Fact]
    public async Task A_completed_response_comes_back_whole()
    {
        string key = NewKey();
        (await Store.TryClaimAsync(key, RepeatableResponse.Claim("fp"), Minute, CancellationToken.None)).ShouldBeNull();

        var response = new RepeatableResponse(
            "fp", true, 201, "application/json; charset=utf-8",
            new Dictionary<string, string> { ["Location"] = "/refunds/7" }, "{\"number\":7}"u8.ToArray());
        await Store.CompleteAsync(key, response, Minute, CancellationToken.None);

        RepeatableResponse stored = (await Store.TryClaimAsync(key, RepeatableResponse.Claim("fp"), Minute, CancellationToken.None))!;
        stored.Completed.ShouldBeTrue();
        stored.StatusCode.ShouldBe(201);
        stored.ContentType.ShouldBe("application/json; charset=utf-8");
        stored.Headers!["Location"].ShouldBe("/refunds/7");
        stored.Body.ToArray().ShouldBe("{\"number\":7}"u8.ToArray());
    }

    [Fact]
    public async Task A_released_claim_can_be_taken_again()
    {
        string key = NewKey();
        (await Store.TryClaimAsync(key, RepeatableResponse.Claim("fp"), Minute, CancellationToken.None)).ShouldBeNull();

        await Store.ReleaseAsync(key, CancellationToken.None);

        (await Store.TryClaimAsync(key, RepeatableResponse.Claim("fp"), Minute, CancellationToken.None)).ShouldBeNull();
    }

    [Fact]
    public async Task A_claim_expires_so_a_crashed_request_does_not_block_its_id_forever()
    {
        string key = NewKey();
        (await Store.TryClaimAsync(key, RepeatableResponse.Claim("fp"), TimeSpan.FromMilliseconds(300), CancellationToken.None)).ShouldBeNull();
        (await Store.TryClaimAsync(key, RepeatableResponse.Claim("fp"), Minute, CancellationToken.None)).ShouldNotBeNull();

        await Task.Delay(TimeSpan.FromMilliseconds(600));

        (await Store.TryClaimAsync(key, RepeatableResponse.Claim("fp"), Minute, CancellationToken.None)).ShouldBeNull();
    }
}
