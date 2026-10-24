using System.Globalization;
using System.Text.Json;
using System.Text.Json.Serialization;
using StackExchange.Redis;
using Viatica.Flights.Application.Offers;

namespace Viatica.Flights.Infrastructure.Cache;

/// <summary>
/// The offer cache in Valkey. A search is one value holding its whole result set, in the order clients page
/// through it, and every offer is a value of its own so re-pricing and ordering find one offer without reading the
/// set. Both end by themselves when the airline's price does: nothing here is ever deleted by hand, and nothing
/// outlives the offers it describes.
/// </summary>
/// <remarks>
/// <para>
/// The whole set as one value is a choice. A search holds tens of offers (four airlines, a handful of flights and
/// four fare classes each), a few kilobytes of JSON, and a page is a filter and a seek over a list in memory. A
/// sorted set per sort order would answer a page without reading the rest, and would cost a key per order, a score
/// per sort field and a filter the server cannot run. At this size one read beats it.
/// </para>
/// <para>
/// Keys are prefixed <c>flights:</c> so the cache can share a Valkey with the rate limiter and the repeatability
/// store without a collision.
/// </para>
/// </remarks>
public sealed class ValkeyOfferStore(IConnectionMultiplexer valkey, TimeProvider time) : IOfferStore
{
    /// <summary>The shortest time to live Valkey accepts and a client could use; an offer about to expire is still written.</summary>
    private static readonly TimeSpan MinimumLifetime = TimeSpan.FromSeconds(1);

    public async Task SaveSearchAsync(StoredSearch search, IReadOnlyList<StoredOffer> offers, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(search);
        ArgumentNullException.ThrowIfNull(offers);

        IDatabase db = valkey.GetDatabase();
        DateTimeOffset now = time.GetUtcNow();

        // One round trip: the commands are queued on a batch and sent together. The search is written last, so a
        // reader who finds the search always finds its offers.
        IBatch batch = db.CreateBatch();
        List<Task> writes = [.. offers.Select(offer => batch.StringSetAsync(OfferKey(offer.Offer.Id), Serialize(offer), Lifetime(offer.Offer.ExpiresAt, now)))];
        writes.Add(batch.StringSetAsync(SearchKey(search.Id), Serialize(search), Lifetime(search.ExpiresAt, now)));
        batch.Execute();

        await Task.WhenAll(writes).WaitAsync(cancellationToken);
    }

    public async Task<StoredSearch?> GetSearchAsync(Guid searchId, CancellationToken cancellationToken)
    {
        RedisValue value = await valkey.GetDatabase().StringGetAsync(SearchKey(searchId)).WaitAsync(cancellationToken);
        return value.IsNullOrEmpty ? null : JsonSerializer.Deserialize((byte[])value!, CacheJson.Default.StoredSearch);
    }

    public async Task<StoredOffer?> GetOfferAsync(string offerId, CancellationToken cancellationToken)
    {
        // An id the cache could never have issued is "not found" without a round trip, and a long one cannot be
        // used to write junk key names into a log.
        if (offerId is not { Length: > 0 and <= 64 } || !offerId.All(char.IsAsciiHexDigit))
        {
            return null;
        }

        RedisValue value = await valkey.GetDatabase().StringGetAsync(OfferKey(offerId)).WaitAsync(cancellationToken);
        return value.IsNullOrEmpty ? null : JsonSerializer.Deserialize((byte[])value!, CacheJson.Default.StoredOffer);
    }

    public Task SaveOfferAsync(StoredOffer offer, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(offer);
        return valkey.GetDatabase()
            .StringSetAsync(OfferKey(offer.Offer.Id), Serialize(offer), Lifetime(offer.Offer.ExpiresAt, time.GetUtcNow()))
            .WaitAsync(cancellationToken);
    }

    private static RedisKey SearchKey(Guid id) => "flights:search:" + id.ToString("N", CultureInfo.InvariantCulture);

    private static RedisKey OfferKey(string id) => "flights:offer:" + id;

    private static TimeSpan Lifetime(DateTimeOffset expiresAt, DateTimeOffset now) =>
        expiresAt - now is var left && left > MinimumLifetime ? left : MinimumLifetime;

    private static byte[] Serialize(StoredSearch search) => JsonSerializer.SerializeToUtf8Bytes(search, CacheJson.Default.StoredSearch);

    private static byte[] Serialize(StoredOffer offer) => JsonSerializer.SerializeToUtf8Bytes(offer, CacheJson.Default.StoredOffer);
}

[JsonSourceGenerationOptions(JsonSerializerDefaults.Web, UseStringEnumConverter = true)]
[JsonSerializable(typeof(StoredSearch))]
[JsonSerializable(typeof(StoredOffer))]
internal sealed partial class CacheJson : JsonSerializerContext;
