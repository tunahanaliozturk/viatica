using System.Collections.Concurrent;

namespace Viatica.Kernel.Web;

/// <summary>A response kept for replay, or, with <see cref="Completed"/> false, the claim of a request still running.</summary>
/// <param name="Owner">
/// Who wrote the entry: a token the claiming request makes up and keeps. A store that honours it lets only that
/// owner complete, renew or release the claim, so a request that outlived its claim cannot touch a newer one.
/// </param>
public sealed record RepeatableResponse(
    string Fingerprint,
    bool Completed,
    int StatusCode,
    string? ContentType,
    IReadOnlyDictionary<string, string>? Headers,
    ReadOnlyMemory<byte> Body,
    string? Owner = null)
{
    public static RepeatableResponse Claim(string fingerprint, string? owner = null) =>
        new(fingerprint, false, 0, null, null, ReadOnlyMemory<byte>.Empty, owner);
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

    /// <summary>
    /// Replaces the owner's claim with the response to replay. When <see cref="RepeatableResponse.Owner"/> is set and
    /// the key now holds another owner's entry, nothing is written: the claim this response answers lapsed and
    /// someone else took the id.
    /// </summary>
    ValueTask CompleteAsync(string key, RepeatableResponse response, TimeSpan timeToLive, CancellationToken cancellationToken);

    /// <summary>Drops the owner's claim so a retry can run, after a failure that should not be replayed.</summary>
    ValueTask ReleaseAsync(string key, CancellationToken cancellationToken);

    /// <summary>
    /// Drops the claim only if <paramref name="owner"/> still holds it. The default is the unfenced
    /// <see cref="ReleaseAsync(string, CancellationToken)"/>, which a store written before owners existed keeps.
    /// </summary>
    ValueTask ReleaseAsync(string key, string owner, CancellationToken cancellationToken) => ReleaseAsync(key, cancellationToken);

    /// <summary>
    /// Pushes the expiry of <paramref name="owner"/>'s claim to <paramref name="timeToLive"/> from now, which is how a
    /// request that is still running keeps its claim. Returns false when the claim is gone or is another owner's,
    /// and the request should stop renewing. The default does nothing, for a store whose claims do not expire.
    /// </summary>
    ValueTask<bool> RenewAsync(string key, string owner, TimeSpan timeToLive, CancellationToken cancellationToken) => ValueTask.FromResult(true);
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
        DateTimeOffset now = time.GetUtcNow();
        var replacement = new Entry(response, now + timeToLive);
        while (true)
        {
            entries.TryGetValue(key, out Entry? current);
            if (current is null || current.ExpiresAt <= now)
            {
                // The claim lapsed and nobody took the id: the response is still worth keeping.
                if (current is null ? entries.TryAdd(key, replacement) : entries.TryUpdate(key, replacement, current))
                {
                    return ValueTask.CompletedTask;
                }

                continue;
            }

            if (response.Owner is not null && current.Response.Owner != response.Owner)
            {
                return ValueTask.CompletedTask;
            }

            if (entries.TryUpdate(key, replacement, current))
            {
                return ValueTask.CompletedTask;
            }
        }
    }

    public ValueTask ReleaseAsync(string key, CancellationToken cancellationToken)
    {
        entries.TryRemove(key, out _);
        return ValueTask.CompletedTask;
    }

    public ValueTask ReleaseAsync(string key, string owner, CancellationToken cancellationToken)
    {
        if (entries.TryGetValue(key, out Entry? current) && current.Response.Owner == owner)
        {
            entries.TryRemove(new KeyValuePair<string, Entry>(key, current));
        }

        return ValueTask.CompletedTask;
    }

    public ValueTask<bool> RenewAsync(string key, string owner, TimeSpan timeToLive, CancellationToken cancellationToken)
    {
        DateTimeOffset now = time.GetUtcNow();
        bool renewed = entries.TryGetValue(key, out Entry? current)
            && current.ExpiresAt > now
            && current.Response.Owner == owner
            && entries.TryUpdate(key, current with { ExpiresAt = now + timeToLive }, current);
        return ValueTask.FromResult(renewed);
    }

    private sealed record Entry(RepeatableResponse Response, DateTimeOffset ExpiresAt);
}
