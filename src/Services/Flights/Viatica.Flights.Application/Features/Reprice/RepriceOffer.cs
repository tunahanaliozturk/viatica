using Microsoft.Extensions.Logging;
using Relevox;
using Viatica.Flights.Application.Offers;
using Viatica.Flights.Application.Suppliers;
using Viatica.Kernel;

namespace Viatica.Flights.Application.Features.Reprice;

/// <summary>Asks the airline what an offer costs now (FLT-3). The step between a search result and a trip.</summary>
public sealed record RepriceOfferCommand(string OfferId) : IRequest<RepriceResult>;

/// <summary>
/// The airline held the price. <see cref="Offer"/> is a fresh offer with its own id and a new expiry; it replaces the
/// one the client asked about, and it is the one to put in a trip.
/// </summary>
public sealed record RepriceUnchanged(FlightOffer Offer);

/// <summary>
/// The price moved. The new offer comes back with the old total beside it and is never applied silently: the
/// traveller sees both and decides.
/// </summary>
public sealed record RepriceChanged(FlightOffer Offer, Money PreviousTotal);

/// <summary>The offer is not available any more: expired, forgotten by the cache, or the cabin sold out.</summary>
public sealed record RepriceOfferExpired;

/// <summary>The airline could not be asked. The offer may still be good; the client can try again.</summary>
public sealed record RepriceSupplierUnavailable;

public union RepriceResult(RepriceUnchanged, RepriceChanged, RepriceOfferExpired, RepriceSupplierUnavailable);

public sealed partial class RepriceOfferHandler(
    IAirlineShopping airlines,
    IOfferStore store,
    TimeProvider time,
    ILogger<RepriceOfferHandler> logger) : IRequestHandler<RepriceOfferCommand, RepriceResult>
{
    public async Task<RepriceResult> Handle(RepriceOfferCommand request, CancellationToken cancellationToken)
    {
        StoredOffer? stored = await store.GetOfferAsync(request.OfferId, cancellationToken);
        if (stored is null || stored.Offer.ExpiresAt <= time.GetUtcNow())
        {
            return Count("expired", new RepriceOfferExpired());
        }

        RepriceOutcome outcome = await airlines.RepriceAsync(stored.Offer.Airline, stored.SupplierOfferId, cancellationToken);
        switch (outcome)
        {
            case OfferRepriced repriced:
                // A new id every time, even when the price held: the airline issued a new offer with a fresh
                // lifetime, and the old id's cache entry ends on its own schedule.
                FlightOffer fresh = repriced.Offer.Offer with { Id = Guid.CreateVersion7().ToString("N") };
                await store.SaveOfferAsync(new StoredOffer(fresh, repriced.Offer.SupplierOfferId, stored.Passengers), cancellationToken);

                if (fresh.Total == stored.Offer.Total)
                {
                    return Count("unchanged", new RepriceUnchanged(fresh));
                }

                LogPriceChanged(logger, stored.Offer.Airline);
                return Count("changed", new RepriceChanged(fresh, stored.Offer.Total));

            case OfferGone:
                return Count("expired", new RepriceOfferExpired());

            case SupplierFailure:
                return Count("unavailable", new RepriceSupplierUnavailable());

            default:
                throw new System.Diagnostics.UnreachableException("A reprice outcome was never set.");
        }
    }

    private static RepriceResult Count(string result, RepriceResult value)
    {
        FlightsTelemetry.Reprices.Add(1, new KeyValuePair<string, object?>("result", result));
        return value;
    }

    [LoggerMessage(Level = LogLevel.Information, Message = "An offer from {Airline} came back at a different price on re-price")]
    private static partial void LogPriceChanged(ILogger logger, string airline);
}
