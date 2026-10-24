using System.Diagnostics;
using Microsoft.Extensions.Logging;
using Relevox;
using Viatica.Flights.Application.Offers;
using Viatica.Flights.Application.Suppliers;

namespace Viatica.Flights.Application.Features.Search;

/// <summary>
/// Asks every airline at once and answers when they have all replied or the deadline passes, whichever comes first.
/// Whoever is missing is named in the response; nobody is waited for.
/// </summary>
/// <remarks>
/// <para>
/// The deadline is enforced twice, on purpose. Each airline client carries its own timeout of the same length
/// inside its resilience pipeline, so a slow airline is recorded as a failure by its circuit breaker. The wait here
/// is the safety net: it ends the request even if an adapter ignores cancellation, and it is a little longer than
/// the deadline so the adapters' own timeouts normally report first.
/// </para>
/// <para>
/// A late answer is dropped, not cached for later: an offer that arrives after the response went out would be an
/// offer no client has seen.
/// </para>
/// </remarks>
public sealed partial class SearchFlightsHandler(
    IAirlineShopping airlines,
    IOfferStore store,
    FlightsOptions options,
    TimeProvider time,
    ILogger<SearchFlightsHandler> logger) : IRequestHandler<SearchFlightsCommand, SearchFlightsResult>
{
    /// <summary>The safety net waits this much longer than the deadline.</summary>
    private static readonly TimeSpan SafetyMargin = TimeSpan.FromMilliseconds(150);

    public async Task<SearchFlightsResult> Handle(SearchFlightsCommand request, CancellationToken cancellationToken)
    {
        using Activity? activity = FlightsTelemetry.Source.StartActivity("flights.search");
        long started = time.GetTimestamp();
        SearchCriteria criteria = request.ToCriteria();

        AirlineAnswer[] answers = await AskEveryAirlineAsync(criteria, cancellationToken);

        var incomplete = new List<IncompleteSupplier>();
        var found = new List<(string Airline, SupplierOffer Offer)>();
        foreach (AirlineAnswer answer in answers)
        {
            switch (answer.Outcome)
            {
                case SupplierOffers offers:
                    found.AddRange(offers.Offers.Select(offer => (answer.Airline, offer)));
                    break;
                case SupplierFailure failure:
                    incomplete.Add(new IncompleteSupplier(answer.Airline, ReasonOf(failure.Kind)));
                    FlightsTelemetry.IncompleteSuppliers.Add(
                        1,
                        new KeyValuePair<string, object?>("airline", answer.Airline),
                        new KeyValuePair<string, object?>("reason", failure.Kind.ToString()));
                    break;
                case null:
                    throw new UnreachableException("A search outcome was never set.");
            }
        }

        double elapsedMs = time.GetElapsedTime(started).TotalMilliseconds;
        FlightsTelemetry.SearchDuration.Record(elapsedMs);
        FlightsTelemetry.Searches.Add(1, new KeyValuePair<string, object?>("complete", incomplete.Count == 0));

        if (found.Count == 0 && incomplete.Count == answers.Length)
        {
            LogNoSuppliers(logger, answers.Length, elapsedMs);
            return new NoSuppliersAnswered(incomplete);
        }

        DateTimeOffset now = time.GetUtcNow();
        StoredOffer[] stored =
        [
            .. found
                .Select(item => new StoredOffer(
                    item.Offer.Offer with { Id = Guid.CreateVersion7().ToString("N") },
                    item.Offer.SupplierOfferId,
                    criteria.Passengers))
                .OrderBy(static o => o.Offer.Total.Amount)
                .ThenBy(static o => o.Offer.Departure)
                .ThenBy(static o => o.Offer.Id, StringComparer.Ordinal),
        ];

        // The search is only as good as its shortest-lived offer: past that, a page could show a fare nobody can buy.
        DateTimeOffset expiresAt = stored.Length == 0 ? now + options.EmptySearchLifetime : stored.Min(static o => o.Offer.ExpiresAt);
        var search = new StoredSearch(Guid.CreateVersion7(), expiresAt, incomplete, [.. stored.Select(static o => o.Offer)]);
        await store.SaveSearchAsync(search, stored, cancellationToken);

        LogSearched(logger, search.Id, stored.Length, answers.Length - incomplete.Count, answers.Length, elapsedMs);
        return OfferPaging.Page(search, OfferRow.FirstPage());
    }

    private async Task<AirlineAnswer[]> AskEveryAirlineAsync(SearchCriteria criteria, CancellationToken cancellationToken)
    {
        // Linked to the caller, so a client that hangs up stops the calls. The hard stop cancels whatever is still
        // running when the safety net fires.
        using var hardStop = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        Task<AirlineAnswer>[] calls = [.. airlines.Airlines.Select(code => AskAsync(code, criteria, hardStop.Token, cancellationToken))];

        try
        {
            await Task.WhenAll(calls).WaitAsync(options.SearchDeadline + SafetyMargin, time, cancellationToken);
        }
        catch (TimeoutException)
        {
            // Some airline did not finish. It is named below by the tasks that never completed.
        }

        await hardStop.CancelAsync();

        return
        [
            .. calls.Select((call, i) => call.IsCompletedSuccessfully
                ? call.Result
                : new AirlineAnswer(airlines.Airlines[i], new SupplierFailure(FailureKind.Timeout, MayHaveReachedAirline: true))),
        ];
    }

    private async Task<AirlineAnswer> AskAsync(
        string airline, SearchCriteria criteria, CancellationToken hardStop, CancellationToken caller)
    {
        try
        {
            return new AirlineAnswer(airline, await airlines.SearchAsync(airline, criteria, hardStop));
        }
        catch (OperationCanceledException) when (!caller.IsCancellationRequested)
        {
            // Our own hard stop, not the client hanging up: this airline ran out of time.
            return new AirlineAnswer(airline, new SupplierFailure(FailureKind.Timeout, MayHaveReachedAirline: true));
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            // An adapter bug must cost this search one airline, not the whole answer.
            LogAirlineBroke(logger, exception, airline);
            return new AirlineAnswer(airline, new SupplierFailure(FailureKind.Error, MayHaveReachedAirline: true));
        }
    }

    private static IncompleteReason ReasonOf(FailureKind kind) => kind switch
    {
        FailureKind.Timeout => IncompleteReason.Timeout,
        FailureKind.CircuitOpen => IncompleteReason.CircuitOpen,
        _ => IncompleteReason.Error,
    };

    private readonly record struct AirlineAnswer(string Airline, SearchOutcome Outcome);

    [LoggerMessage(Level = LogLevel.Information,
        Message = "Search {SearchId}: {Offers} offers, {Answered} of {Asked} airlines answered, {ElapsedMs:F0} ms")]
    private static partial void LogSearched(ILogger logger, Guid searchId, int offers, int answered, int asked, double elapsedMs);

    [LoggerMessage(Level = LogLevel.Error, Message = "The adapter for airline {Airline} threw during a search")]
    private static partial void LogAirlineBroke(ILogger logger, Exception exception, string airline);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Search failed: none of {Asked} airlines answered, {ElapsedMs:F0} ms")]
    private static partial void LogNoSuppliers(ILogger logger, int asked, double elapsedMs);
}
