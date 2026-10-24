using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Time.Testing;
using Viatica.Flights.Application;
using Viatica.Flights.Application.Features.Search;
using Viatica.Flights.Application.Offers;
using Viatica.Flights.Application.Suppliers;
using Viatica.Flights.Domain;

namespace Viatica.Flights.UnitTests;

/// <summary>
/// The fan-out on a fake clock: who is waited for, who is named as missing and why, and what ends up cached. The
/// same behaviour against real airlines and real time is in the integration tests (FlightSearchTests).
/// </summary>
public sealed class SearchFanOutTests
{
    private static readonly string[] Airlines = ["ZC", "YZ", "JG", "ZJ"];

    private readonly FakeTimeProvider clock = new(Make.Now);
    private readonly MemoryOfferStore store = new();
    private readonly FlightsOptions options = new();

    private static SearchFlightsCommand Command() =>
        new("IST", "LHR", new DateOnly(2026, 11, 23), null, 1, 0, 0, Cabin.Economy);

    private SearchFlightsHandler Handler(ScriptedShopping shopping) =>
        new(shopping, store, options, clock, NullLogger<SearchFlightsHandler>.Instance);

    /// <summary>Runs the search while advancing the fake clock until it finishes, as real time would.</summary>
    private async Task<SearchFlightsResult> RunAsync(SearchFlightsHandler handler)
    {
        Task<SearchFlightsResult> search = handler.Handle(Command(), CancellationToken.None);

        for (int step = 0; step < 200 && !search.IsCompleted; step++)
        {
            clock.Advance(TimeSpan.FromMilliseconds(50));
            await Task.Delay(1);
        }

        search.IsCompleted.ShouldBeTrue("the search never finished, even with 10 s of fake time");
        return await search;
    }

    [Fact]
    public async Task Offers_from_every_airline_come_back_cheapest_first_with_departure_as_the_tie_break()
    {
        var shopping = new ScriptedShopping(Airlines)
            .On("ZC", Make.Offers("ZC", 300m, 250m))
            .On("YZ", Make.Offers("YZ", 250m))
            .On("JG", Make.Offers("JG", 199m))
            .On("ZJ", new SupplierOffers([Make.SupplierOffer("ZJ", 250m, departureHour: 7)]));

        SearchFlightsResult result = await RunAsync(Handler(shopping));

        FlightSearchPage page = result.Case<FlightSearchPage>();
        page.IncompleteSuppliers.ShouldBeEmpty();
        page.Offers.Value.Select(static o => (o.Airline, o.Total.Amount, o.Departure.Hour))
            .ShouldBe([("JG", 199m, 9), ("ZJ", 250m, 7), ("YZ", 250m, 9), ("ZC", 250m, 10), ("ZC", 300m, 9)]);
        page.Offers.Value.Select(static o => o.Id).ShouldBeUnique();
        page.Offers.Value.ShouldAllBe(static o => o.Id.Length == 32);
    }

    [Fact]
    public async Task An_airline_still_working_at_the_deadline_is_left_out_and_named_as_a_timeout()
    {
        var shopping = new ScriptedShopping(Airlines)
            .On("ZC", Make.Offers("ZC", 300m))
            .On("YZ", async cancellationToken =>
            {
                await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
                return Make.Offers("YZ", 1m);
            })
            .On("JG", Make.Offers("JG", 310m))
            .On("ZJ", Make.Offers("ZJ", 320m));

        SearchFlightsResult result = await RunAsync(Handler(shopping));

        FlightSearchPage page = result.Case<FlightSearchPage>();
        page.IncompleteSuppliers.ShouldBe([new IncompleteSupplier("YZ", IncompleteReason.Timeout)]);
        page.Offers.Value.ShouldNotContain(static o => o.Airline == "YZ");
        page.Offers.Value.Count.ShouldBe(3);
    }

    [Fact]
    public async Task The_answer_is_not_held_for_the_slow_airline_beyond_the_deadline()
    {
        var shopping = new ScriptedShopping(["ZC", "YZ"])
            .On("ZC", Make.Offers("ZC", 300m))
            .On("YZ", async cancellationToken =>
            {
                await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
                return Make.Offers("YZ", 1m);
            });

        DateTimeOffset started = clock.GetUtcNow();
        await RunAsync(Handler(shopping));

        // The safety net waits 150 ms longer than the 2.5 s deadline; the fake clock moves in 50 ms steps.
        (clock.GetUtcNow() - started).ShouldBeLessThanOrEqualTo(TimeSpan.FromMilliseconds(2750));
        (clock.GetUtcNow() - started).ShouldBeGreaterThanOrEqualTo(options.SearchDeadline);
    }

    [Fact]
    public async Task Failures_are_named_with_the_reason_the_adapter_gave()
    {
        var shopping = new ScriptedShopping(Airlines)
            .On("ZC", Make.Offers("ZC", 300m))
            .On("YZ", Make.Failure(FailureKind.Timeout))
            .On("JG", new SupplierFailure(FailureKind.CircuitOpen, MayHaveReachedAirline: false))
            .On("ZJ", Make.Failure(FailureKind.Error));

        SearchFlightsResult result = await RunAsync(Handler(shopping));

        result.Case<FlightSearchPage>().IncompleteSuppliers.ShouldBe(
        [
            new IncompleteSupplier("YZ", IncompleteReason.Timeout),
            new IncompleteSupplier("JG", IncompleteReason.CircuitOpen),
            new IncompleteSupplier("ZJ", IncompleteReason.Error),
        ]);
    }

    [Fact]
    public async Task An_adapter_that_throws_costs_the_search_one_airline_and_not_the_answer()
    {
        var shopping = new ScriptedShopping(["ZC", "YZ"])
            .On("ZC", Make.Offers("ZC", 300m))
            .On("YZ", _ => throw new InvalidOperationException("bug in the YZ adapter"));

        SearchFlightsResult result = await RunAsync(Handler(shopping));

        FlightSearchPage page = result.Case<FlightSearchPage>();
        page.IncompleteSuppliers.ShouldBe([new IncompleteSupplier("YZ", IncompleteReason.Error)]);
        page.Offers.Value.Count.ShouldBe(1);
    }

    [Fact]
    public async Task When_no_airline_answers_there_is_nothing_to_page_and_nothing_is_cached()
    {
        var shopping = new ScriptedShopping(["ZC", "YZ"])
            .On("ZC", Make.Failure(FailureKind.Error))
            .On("YZ", Make.Failure(FailureKind.Timeout));

        SearchFlightsResult result = await RunAsync(Handler(shopping));

        result.Case<NoSuppliersAnswered>().IncompleteSuppliers.Count.ShouldBe(2);
        store.Searches.ShouldBeEmpty();
        store.Offers.ShouldBeEmpty();
    }

    [Fact]
    public async Task Airlines_that_answered_with_no_flights_are_an_empty_page_not_an_error()
    {
        var shopping = new ScriptedShopping(["ZC", "YZ"])
            .On("ZC", new SupplierOffers([]))
            .On("YZ", Make.Failure(FailureKind.Error));

        SearchFlightsResult result = await RunAsync(Handler(shopping));

        FlightSearchPage page = result.Case<FlightSearchPage>();
        page.Offers.Value.ShouldBeEmpty();
        page.IncompleteSuppliers.Count.ShouldBe(1);
        page.ExpiresAt.ShouldBe(clock.GetUtcNow() + options.EmptySearchLifetime, TimeSpan.FromSeconds(5));
    }

    [Fact]
    public async Task The_search_ends_with_its_earliest_offer_and_every_offer_is_kept_for_ordering()
    {
        DateTimeOffset early = Make.Now.AddMinutes(4);
        var shopping = new ScriptedShopping(["ZC", "YZ"])
            .On("ZC", new SupplierOffers([Make.SupplierOffer("ZC", 300m, expires: Make.Now.AddMinutes(15))]))
            .On("YZ", new SupplierOffers([Make.SupplierOffer("YZ", 310m, supplierId: "airline-token-YZ", expires: early)]));

        FlightSearchPage page = (await RunAsync(Handler(shopping))).Case<FlightSearchPage>();

        page.ExpiresAt.ShouldBe(early);
        store.Searches[page.SearchId].Offers.Count.ShouldBe(2);
        store.Offers.Count.ShouldBe(2);

        // The airline's own id stays in the cache and never reaches a client.
        StoredOffer yz = store.Offers.Values.Single(static o => o.Offer.Airline == "YZ");
        yz.SupplierOfferId.ShouldBe("airline-token-YZ");
        yz.Offer.Id.ShouldNotContain("airline-token");
        yz.Passengers.ShouldBe(new PassengerMix(1, 0, 0));
    }

    [Fact]
    public async Task A_client_that_hangs_up_stops_the_search()
    {
        var shopping = new ScriptedShopping(["ZC"]).On("ZC", async cancellationToken =>
        {
            await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
            return Make.Offers("ZC", 1m);
        });
        using var hangUp = new CancellationTokenSource();

        Task<SearchFlightsResult> search = Handler(shopping).Handle(Command(), hangUp.Token);
        await hangUp.CancelAsync();

        await Should.ThrowAsync<OperationCanceledException>(() => search);
    }
}
