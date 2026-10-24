using System.Collections.Concurrent;

namespace Viatica.Kernel.Web;

/// <summary>A response kept for replay, or, with <see cref="Completed"/> false, the claim of a request still running.</summary>
public sealed record RepeatableResponse(
    string Fingerprint,
    bool Completed,
    int StatusCode,
    string? ContentType,
    IReadOnlyDictionary<string, string>? Headers,
    ReadOnlyMemory<byte> Body)
{
    public static RepeatableResponse Claim(string fingerprint) => new(fingerprint, false, 0, null, null, ReadOnlyMemory<byte>.Empty);
}

/// <summary>
/// Where repeatable requests are remembered. The one operation that matters is <see cref="TryClaimAsync"/>: it
/// must be atomic across every instance of the service, or two copies of the same request arriving together
/// both run.
/// </summary>
/// <remarks>
/// A port rather than <c>IDistributedCache</c> because that abstraction has no set-if-absent, so any claim built
/// on it is a read followed by a write with a race in between.
/// </remarks>
public interface IRepeatabilityStore
{
    /// <summary>
    /// Records <paramref name="claim"/> under <paramref name="key"/> if nothing is there. Returns null when this
    /// caller now owns the key, or the entry already stored (a claim in progress or a completed response).
    /// </summary>
    ValueTask<RepeatableResponse?> TryClaimAsync(string key, RepeatableResponse claim, TimeSpan timeToLive, CancellationToken cancellationToken);

    /// <summary>Replaces the owner's claim with the response to replay.</summary>
    ValueTask CompleteAsync(string key, RepeatableResponse response, TimeSpan timeToLive, CancellationToken cancellationToken);

    /// <summary>Drops the owner's claim so a retry can run, after a failure that should not be replayed.</summary>
    ValueTask ReleaseAsync(string key, CancellationToken cancellationToken);
}

/// <summary>
/// An in-process store, atomic through <see cref="ConcurrentDictionary{TKey, TValue}.TryAdd"/>. Correct for one
/// instance only: a second instance has its own dictionary. Services with repeatable endpoints register the
/// Valkey store with <see cref="ValkeyRepeatabilityStore"/>.
/// </summary>
public sealed class InMemoryRepeatabilityStore(TimeProvider time) : IRepeatabilityStore
{
    // Expired entries are swept once the dictionary grows past this, so memory stays bounded by the traffic of
    // one window rather than growing for the life of the process.
    private const int SweepThreshold = 10_000;

    private readonly ConcurrentDictionary<string, Entry> entries = new(StringComparer.Ordinal);

    public ValueTask<RepeatableResponse?> TryClaimAsync(
        string key, RepeatableResponse claim, TimeSpan timeToLive, CancellationToken cancellationToken)
    {
        DateTimeOffset now = time.GetUtcNow();
        if (entries.Count > SweepThreshold)
        {
            foreach (var (staleKey, stale) in entries)
            {
                if (stale.ExpiresAt <= now)
                {
                    entries.TryRemove(new KeyValuePair<string, Entry>(staleKey, stale));
                }
            }
        }

        var mine = new Entry(claim, now + timeToLive);
        while (true)
        {
            if (entries.TryAdd(key, mine))
            {
                return ValueTask.FromResult<RepeatableResponse?>(null);
            }

            if (entries.TryGetValue(key, out Entry? existing))
            {
                if (existing.ExpiresAt > now)
                {
                    return ValueTask.FromResult<RepeatableResponse?>(existing.Response);
                }

                // Expired: remove exactly that entry (not one a racing caller just added) and try again.
                entries.TryRemove(new KeyValuePair<string, Entry>(key, existing));
            }
        }
    }

    public ValueTask CompleteAsync(string key, RepeatableResponse response, TimeSpan timeToLive, CancellationToken cancellationToken)
    {
        entries[key] = new Entry(response, time.GetUtcNow() + timeToLive);
        return ValueTask.CompletedTask;
    }

    public ValueTask ReleaseAsync(string key, CancellationToken cancellationToken)
    {
        entries.TryRemove(key, out _);
        return ValueTask.CompletedTask;
    }

    private sealed record Entry(RepeatableResponse Response, DateTimeOffset ExpiresAt);
}
