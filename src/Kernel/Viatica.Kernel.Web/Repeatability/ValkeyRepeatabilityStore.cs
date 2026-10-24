using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using StackExchange.Redis;

namespace Viatica.Kernel.Web;

/// <summary>
/// Repeatable requests in Valkey (or Redis): the claim is one <c>SET key value NX PX ttl</c>, which the server
/// applies atomically, so exactly one of any number of simultaneous identical requests gets to run.
/// </summary>
/// <remarks>
/// It lives in Kernel.Web next to the middleware rather than in a package of its own: only Api hosts reference
/// Kernel.Web, every service that has repeatable endpoints already runs Valkey for rate limits, and a separate
/// project for one class would be a seam nobody else plugs into.
/// </remarks>
public sealed class ValkeyRepeatabilityStore(IConnectionMultiplexer valkey) : IRepeatabilityStore
{
    public async ValueTask<RepeatableResponse?> TryClaimAsync(
        string key, RepeatableResponse claim, TimeSpan timeToLive, CancellationToken cancellationToken)
    {
        IDatabase database = valkey.GetDatabase();
        byte[] value = Serialize(claim);

        // Two rounds cover the one race left: the entry expiring between a failed SET NX and the GET.
        for (int attempt = 0; attempt < 2; attempt++)
        {
            if (await database.StringSetAsync(key, value, timeToLive, When.NotExists))
            {
                return null;
            }

            RedisValue existing = await database.StringGetAsync(key);
            if (existing.HasValue)
            {
                return JsonSerializer.Deserialize((byte[])existing!, KernelJsonContext.Default.RepeatableResponse);
            }
        }

        throw new InvalidOperationException($"Could not claim or read repeatability key '{key}'.");
    }

    // The owner check and the write are one script, so they are one step on the server: a GET followed by a SET
    // would let a newer claim arrive in between and be overwritten.
    private const string CompleteScript = """
        local current = redis.call('GET', KEYS[1])
        if current and ARGV[1] ~= '' and cjson.decode(current).owner ~= ARGV[1] then return 0 end
        redis.call('SET', KEYS[1], ARGV[2], 'PX', ARGV[3])
        return 1
        """;

    private const string ReleaseScript = """
        local current = redis.call('GET', KEYS[1])
        if current and cjson.decode(current).owner == ARGV[1] then return redis.call('DEL', KEYS[1]) end
        return 0
        """;

    private const string RenewScript = """
        local current = redis.call('GET', KEYS[1])
        if current and cjson.decode(current).owner == ARGV[1] then return redis.call('PEXPIRE', KEYS[1], ARGV[2]) end
        return 0
        """;

    public async ValueTask CompleteAsync(string key, RepeatableResponse response, TimeSpan timeToLive, CancellationToken cancellationToken) =>
        await valkey.GetDatabase().ScriptEvaluateAsync(
            CompleteScript,
            [key],
            [response.Owner ?? string.Empty, Serialize(response), (long)timeToLive.TotalMilliseconds]);

    public async ValueTask ReleaseAsync(string key, CancellationToken cancellationToken) =>
        await valkey.GetDatabase().KeyDeleteAsync(key);

    public async ValueTask ReleaseAsync(string key, string owner, CancellationToken cancellationToken) =>
        await valkey.GetDatabase().ScriptEvaluateAsync(ReleaseScript, [key], [owner]);

    public async ValueTask<bool> RenewAsync(string key, string owner, TimeSpan timeToLive, CancellationToken cancellationToken) =>
        (long)await valkey.GetDatabase().ScriptEvaluateAsync(RenewScript, [key], [owner, (long)timeToLive.TotalMilliseconds]) == 1;

    private static byte[] Serialize(RepeatableResponse response) =>
        JsonSerializer.SerializeToUtf8Bytes(response, KernelJsonContext.Default.RepeatableResponse);
}

public static class RepeatabilityServiceCollectionExtensions
{
    /// <summary>
    /// Keeps repeatable requests in Valkey through the <see cref="IConnectionMultiplexer"/> the service registers,
    /// replacing the single-instance in-memory default.
    /// </summary>
    public static IServiceCollection AddValkeyRepeatabilityStore(this IServiceCollection services)
    {
        ArgumentNullException.ThrowIfNull(services);
        return services.AddSingleton<IRepeatabilityStore, ValkeyRepeatabilityStore>();
    }
}
