using System.Collections.Concurrent;
using Viatica.Flights.Application.Offers;
using Viatica.Flights.Application.Suppliers;
using Viatica.Flights.Domain;
using Viatica.Kernel;

namespace Viatica.Flights.UnitTests;

/// <summary>Builds offers the way the adapter would hand them over: no id yet, a price, a flight on a date.</summary>
internal static class Make
{
    public static readonly DateTimeOffset Now = new(2026, 11, 2, 8, 0, 0, TimeSpan.Zero);

    public static SupplierOffer SupplierOffer(
        string airline, decimal total, int departureHour = 9, string? supplierId = null, DateTimeOffset? expires = null, int index = 0) =>
        new(
            supplierId ?? $"{airline}-offer-{total}-{departureHour}-{index}",
            new FlightOffer(
                "",
                airline,
                "K",
                Cabin.Economy,
                false,
                Money.Eur(total),
                10,
                expires ?? Now.AddMinutes(15),
                [new FlightSegment($"{airline}100", "IST", "LHR", new DateTime(2026, 11, 23, departureHour, 0, 0), new DateTime(2026, 11, 23, departureHour + 4, 0, 0), 240, "A320")]));

    public static SearchOutcome Offers(string airline, params decimal[] totals) =>
        new SupplierOffers([.. totals.Select((total, i) => SupplierOffer(airline, total, 9 + i, index: i))]);

    public static SearchOutcome Failure(FailureKind kind) => new SupplierFailure(kind, MayHaveReachedAirline: true);
}

/// <summary>A scripted set of airlines. A script is a function so a test can make one hang, throw or answer at will.</summary>
internal sealed class ScriptedShopping(IReadOnlyList<string> airlines) : IAirlineShopping
{
    private readonly ConcurrentDictionary<string, Func<CancellationToken, Task<SearchOutcome>>> scripts = new();

    public IReadOnlyList<string> Airlines { get; } = airlines;

    public int Calls { get; private set; }

    public ScriptedShopping On(string airline, Func<CancellationToken, Task<SearchOutcome>> script)
    {
        scripts[airline] = script;
        return this;
    }

    public ScriptedShopping On(string airline, SearchOutcome outcome) => On(airline, _ => Task.FromResult(outcome));

    public Task<SearchOutcome> SearchAsync(string airline, SearchCriteria criteria, CancellationToken cancellationToken)
    {
        Calls++;
        return scripts[airline](cancellationToken);
    }

    public Task<RepriceOutcome> RepriceAsync(string airline, string supplierOfferId, CancellationToken cancellationToken) =>
        throw new NotSupportedException();
}

internal sealed class MemoryOfferStore : IOfferStore
{
    public Dictionary<Guid, StoredSearch> Searches { get; } = [];

    public Dictionary<string, StoredOffer> Offers { get; } = [];

    public Task SaveSearchAsync(StoredSearch search, IReadOnlyList<StoredOffer> offers, CancellationToken cancellationToken)
    {
        Searches[search.Id] = search;
        foreach (StoredOffer offer in offers)
        {
            Offers[offer.Offer.Id] = offer;
        }

        return Task.CompletedTask;
    }

    public Task<StoredSearch?> GetSearchAsync(Guid searchId, CancellationToken cancellationToken) =>
        Task.FromResult(Searches.GetValueOrDefault(searchId));

    public Task<StoredOffer?> GetOfferAsync(string offerId, CancellationToken cancellationToken) =>
        Task.FromResult(Offers.GetValueOrDefault(offerId));

    public Task SaveOfferAsync(StoredOffer offer, CancellationToken cancellationToken)
    {
        Offers[offer.Offer.Id] = offer;
        return Task.CompletedTask;
    }
}
