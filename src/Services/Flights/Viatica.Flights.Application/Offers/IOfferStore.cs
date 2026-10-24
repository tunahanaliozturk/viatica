namespace Viatica.Flights.Application.Offers;

/// <summary>
/// The offer cache. Offers live minutes, belong to the airlines and are worthless after their expiry, so they sit in
/// Valkey with a time to live and nowhere else.
/// </summary>
public interface IOfferStore
{
    /// <summary>
    /// Keeps the search for paging and every offer for re-pricing and ordering. The search ends at its own
    /// <see cref="StoredSearch.ExpiresAt"/>; each offer ends at its own.
    /// </summary>
    Task SaveSearchAsync(StoredSearch search, IReadOnlyList<StoredOffer> offers, CancellationToken cancellationToken);

    Task<StoredSearch?> GetSearchAsync(Guid searchId, CancellationToken cancellationToken);

    Task<StoredOffer?> GetOfferAsync(string offerId, CancellationToken cancellationToken);

    /// <summary>Keeps one offer, such as a re-priced one, until it expires.</summary>
    Task SaveOfferAsync(StoredOffer offer, CancellationToken cancellationToken);
}
