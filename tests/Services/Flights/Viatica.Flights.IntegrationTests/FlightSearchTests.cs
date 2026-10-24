using System.Diagnostics;
using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using StackExchange.Redis;
using Viatica.Testing;

namespace Viatica.Flights.IntegrationTests;

/// <summary>FLT-1 and FLT-2 against four real HTTP airlines, a real Valkey and the real service.</summary>
[Collection(DefaultFlightsGroup.Name)]
public sealed class FlightSearchTests(DefaultFlights flights, ValkeyFixture valkey) : IAsyncLifetime
{
    public async ValueTask InitializeAsync() => await flights.Sim.QuietAsync();

    public ValueTask DisposeAsync() => ValueTask.CompletedTask;

    [Fact]
    public async Task A_search_returns_offers_from_every_airline_cheapest_first()
    {
        (HttpStatusCode status, JsonElement search) = await flights.Client.SearchAsync(Travel.IstanbulToLondon());

        status.ShouldBe(HttpStatusCode.Created);
        search.GetProperty("incompleteSuppliers").GetArrayLength().ShouldBe(0);
        search.GetProperty("searchId").GetGuid().ShouldNotBe(Guid.Empty);
        search.GetProperty("expiresAt").GetDateTimeOffset().ShouldBeGreaterThan(DateTimeOffset.UtcNow);

        List<JsonElement> offers = await flights.Client.AllOffersAsync(search);
        offers.Select(static o => o.GetProperty("airline").GetString()).Distinct().Order().ShouldBe(["JG", "YZ", "ZC", "ZJ"]);

        // Price, then departure: the order FLT-1 asks for.
        offers.Zip(offers.Skip(1)).ShouldAllBe(static pair =>
            pair.First.GetProperty("total").Amount() < pair.Second.GetProperty("total").Amount()
            || (pair.First.GetProperty("total").Amount() == pair.Second.GetProperty("total").Amount()
                && pair.First.GetProperty("segments")[0].GetProperty("departureLocal").GetDateTime()
                   <= pair.Second.GetProperty("segments")[0].GetProperty("departureLocal").GetDateTime()));

        offers.Select(static o => o.GetProperty("id").GetString()).ShouldBeUnique();
    }

    [Fact]
    public async Task An_offer_is_normalised_and_never_shows_the_airlines_own_id()
    {
        (_, JsonElement search) = await flights.Client.SearchAsync(Travel.IstanbulToLondon());
        JsonElement offer = (await flights.Client.AllOffersAsync(search)).From("ZC").First();

        offer.GetProperty("id").GetString()!.Length.ShouldBe(32);
        offer.GetProperty("total").GetProperty("currency").GetString().ShouldBe("EUR");
        offer.GetProperty("cabin").GetString().ShouldBe("Economy");
        offer.GetProperty("fareClass").GetString().ShouldBeOneOf("K", "M", "Y");
        offer.GetProperty("expiresAt").GetDateTimeOffset().ShouldBeInRange(DateTimeOffset.UtcNow.AddMinutes(10), DateTimeOffset.UtcNow.AddMinutes(20));

        JsonElement segment = offer.GetProperty("segments").EnumerateArray().Single();
        segment.GetProperty("origin").GetString().ShouldBe("IST");
        segment.GetProperty("destination").GetString().ShouldBe("LHR");
        segment.GetProperty("flightNumber").GetString()!.ShouldStartWith("ZC");

        offer.ToString().ShouldNotContain("supplier", Case.Insensitive);
        offer.ToString().ShouldNotContain("OfferId");
    }

    [Fact]
    public async Task A_return_search_prices_both_legs_in_one_offer()
    {
        (HttpStatusCode status, JsonElement search) = await flights.Client.SearchAsync(Travel.IstanbulToLondon(returnDate: Travel.Return));

        status.ShouldBe(HttpStatusCode.Created);
        JsonElement offer = (await flights.Client.AllOffersAsync(search)).First();
        JsonElement[] legs = [.. offer.GetProperty("segments").EnumerateArray()];
        legs.Length.ShouldBe(2);
        legs[0].GetProperty("origin").GetString().ShouldBe("IST");
        legs[1].GetProperty("origin").GetString().ShouldBe("LHR");
    }

    [Fact]
    public async Task One_airline_down_and_one_slow_the_answer_comes_inside_the_deadline_naming_both()
    {
        await flights.Sim.SetKnobsAsync("JG", new { Outage = true });
        await flights.Sim.SetKnobsAsync("YZ", new { LatencyMedianMs = 10_000 });

        var clock = Stopwatch.StartNew();
        (HttpStatusCode status, JsonElement search) = await flights.Client.SearchAsync(Travel.IstanbulToLondon());
        clock.Stop();

        status.ShouldBe(HttpStatusCode.Created);

        // The slow airline is waited for until the deadline and not a moment longer.
        clock.Elapsed.ShouldBeGreaterThan(TimeSpan.FromSeconds(2.3));
        clock.Elapsed.ShouldBeLessThan(TimeSpan.FromSeconds(3.2));

        search.GetProperty("incompleteSuppliers").EnumerateArray()
            .Select(static s => (s.GetProperty("airline").GetString(), s.GetProperty("reason").GetString()))
            .Order()
            .ShouldBe([("JG", "Error"), ("YZ", "Timeout")]);

        List<JsonElement> offers = await flights.Client.AllOffersAsync(search);
        offers.Select(static o => o.GetProperty("airline").GetString()).Distinct().Order().ShouldBe(["ZC", "ZJ"]);
    }

    [Fact]
    public async Task Every_page_of_a_partial_search_still_names_who_is_missing()
    {
        await flights.Sim.SetKnobsAsync("JG", new { Outage = true });
        (_, JsonElement search) = await flights.Client.SearchAsync(Travel.IstanbulToLondon());
        Guid searchId = search.GetProperty("searchId").GetGuid();

        (HttpStatusCode status, JsonElement page) = await flights.Client.GetJsonAsync(Travel.V($"/flights/searches/{searchId}?$top=3"));

        status.ShouldBe(HttpStatusCode.OK);
        page.GetProperty("searchId").GetGuid().ShouldBe(searchId);
        page.GetProperty("incompleteSuppliers").EnumerateArray().Single().GetProperty("airline").GetString().ShouldBe("JG");
        page.GetProperty("expiresAt").GetDateTimeOffset().ShouldBe(search.GetProperty("expiresAt").GetDateTimeOffset());
    }

    [Fact]
    public async Task Paging_never_calls_the_airlines_again()
    {
        (_, JsonElement search) = await flights.Client.SearchAsync(Travel.IstanbulToLondon());
        Guid searchId = search.GetProperty("searchId").GetGuid();
        int callsAfterSearch = flights.Host.Wire.Total;
        callsAfterSearch.ShouldBeGreaterThanOrEqualTo(4);

        // Every way of reading the set: the search's own nextLink, a GET with a small page, the same GET followed to the end.
        List<JsonElement> viaPost = await flights.Client.AllOffersAsync(search);
        (_, JsonElement small) = await flights.Client.GetJsonAsync(Travel.V($"/flights/searches/{searchId}?$top=4"));
        List<JsonElement> viaGet = await flights.Client.AllOffersAsync(small);
        (_, JsonElement filtered) = await flights.Client.GetJsonAsync(Travel.V($"/flights/searches/{searchId}?$filter=airline eq 'ZC'&$top=2"));
        List<JsonElement> filteredAll = await flights.Client.AllOffersAsync(filtered);

        viaPost.Count.ShouldBeGreaterThan(20);
        viaPost.Select(static o => o.GetProperty("id").GetString()).ShouldBe(viaGet.Select(static o => o.GetProperty("id").GetString()));
        filteredAll.ShouldAllBe(static o => o.GetProperty("airline").GetString() == "ZC");
        flights.Host.Wire.Total.ShouldBe(callsAfterSearch);
    }

    [Fact]
    public async Task The_next_link_of_the_search_itself_points_at_the_search_and_pages_to_the_end()
    {
        (_, JsonElement search) = await flights.Client.SearchAsync(Travel.IstanbulToLondon());
        Guid searchId = search.GetProperty("searchId").GetGuid();

        string next = search.GetProperty("nextLink").GetString()!;

        next.ShouldContain($"/flights/searches/{searchId}?api-version={FlightsHost.ApiVersion}&$skiptoken=");
        (HttpStatusCode status, JsonElement second) = await flights.Client.GetJsonAsync(next);
        status.ShouldBe(HttpStatusCode.OK);
        second.GetProperty("value").GetArrayLength().ShouldBeGreaterThan(0);
        second.GetProperty("value")[0].GetProperty("id").GetString()
            .ShouldNotBe(search.GetProperty("value")[0].GetProperty("id").GetString());
    }

    [Fact]
    public async Task A_filter_and_an_ordering_apply_to_the_cached_set()
    {
        (_, JsonElement search) = await flights.Client.SearchAsync(Travel.IstanbulToLondon());
        Guid searchId = search.GetProperty("searchId").GetGuid();

        (HttpStatusCode status, JsonElement page) = await flights.Client.GetJsonAsync(
            Travel.V($"/flights/searches/{searchId}?$filter=airline eq 'JG' and totalAmount ge 100&$orderby=departure desc"));

        status.ShouldBe(HttpStatusCode.OK);
        DateTime[] departures = [.. page.GetProperty("value").EnumerateArray().Select(static o => o.GetProperty("segments")[0].GetProperty("departureLocal").GetDateTime())];
        departures.ShouldNotBeEmpty();
        departures.ShouldBe(departures.OrderDescending());
        page.GetProperty("value").EnumerateArray().ShouldAllBe(static o => o.GetProperty("airline").GetString() == "JG");
    }

    [Fact]
    public async Task A_skiptoken_from_one_search_is_refused_on_another()
    {
        (_, JsonElement first) = await flights.Client.SearchAsync(Travel.IstanbulToLondon());
        (_, JsonElement second) = await flights.Client.SearchAsync(Travel.IstanbulToLondon());
        string token = first.GetProperty("nextLink").GetString()!.Split("$skiptoken=")[1];

        (HttpStatusCode status, JsonElement error) = await flights.Client.GetJsonAsync(
            Travel.V($"/flights/searches/{second.GetProperty("searchId").GetGuid()}?$skiptoken={token}"));

        status.ShouldBe(HttpStatusCode.BadRequest);
        error.GetProperty("error").GetProperty("code").GetString().ShouldBe("InvalidSkipToken");
    }

    [Fact]
    public async Task A_search_nobody_made_is_gone()
    {
        (HttpStatusCode status, JsonElement error) = await flights.Client.GetJsonAsync(Travel.V($"/flights/searches/{Guid.NewGuid()}"));

        status.ShouldBe(HttpStatusCode.NotFound);
        error.GetProperty("error").GetProperty("code").GetString().ShouldBe("SearchExpired");
    }

    [Fact]
    public async Task The_search_and_its_offers_live_exactly_as_long_as_the_earliest_offer()
    {
        (_, JsonElement search) = await flights.Client.SearchAsync(Travel.IstanbulToLondon());
        Guid searchId = search.GetProperty("searchId").GetGuid();
        List<JsonElement> offers = await flights.Client.AllOffersAsync(search);

        DateTimeOffset earliest = offers.Min(static o => o.GetProperty("expiresAt").GetDateTimeOffset());
        search.GetProperty("expiresAt").GetDateTimeOffset().ShouldBe(earliest);

        await using ConnectionMultiplexer connection = await valkey.ConnectAsync();
        IDatabase db = connection.GetDatabase();
        TimeSpan searchTtl = (await db.KeyTimeToLiveAsync($"flights:search:{searchId:N}"))!.Value;
        TimeSpan expected = earliest - DateTimeOffset.UtcNow;
        searchTtl.ShouldBeInRange(expected - TimeSpan.FromSeconds(10), expected + TimeSpan.FromSeconds(1));

        string offerKey = $"flights:offer:{offers[0].GetProperty("id").GetString()}";
        TimeSpan offerTtl = (await db.KeyTimeToLiveAsync(offerKey))!.Value;
        offerTtl.ShouldBeInRange(TimeSpan.FromMinutes(10), TimeSpan.FromMinutes(20));
    }

    [Fact]
    public async Task A_circuit_that_is_not_open_costs_nothing_and_every_airline_is_asked_once_per_search()
    {
        int before = flights.Host.Wire.Total;

        await flights.Client.SearchAsync(Travel.IstanbulToLondon());

        flights.Host.Wire.Total.ShouldBe(before + 4);
        foreach (string airline in SimHost.Airlines)
        {
            flights.Host.Wire.Count(airline, HttpMethod.Post, "/search").ShouldBeGreaterThanOrEqualTo(1);
        }
    }

    [Fact]
    public async Task When_every_airline_is_down_the_answer_is_a_503_that_names_them_all()
    {
        foreach (string airline in SimHost.Airlines)
        {
            await flights.Sim.SetKnobsAsync(airline, new { Outage = true });
        }

        using HttpResponseMessage response = await flights.Client.PostAsJsonAsync(Travel.V("/flights/searches"), Travel.IstanbulToLondon());
        JsonElement body = await ApiCalls.ReadAsync(response);

        response.StatusCode.ShouldBe(HttpStatusCode.ServiceUnavailable);
        response.Headers.RetryAfter.ShouldNotBeNull();
        JsonElement error = body.GetProperty("error");
        error.GetProperty("code").GetString().ShouldBe("NoSuppliersAvailable");
        error.GetProperty("details").EnumerateArray().Select(static d => d.GetProperty("target").GetString()).Order().ShouldBe(["JG", "YZ", "ZC", "ZJ"]);
    }

    [Fact]
    public async Task A_bad_search_is_a_400_with_every_broken_field_in_the_details()
    {
        using HttpResponseMessage response = await flights.Client.PostAsJsonAsync(Travel.V("/flights/searches"), new
        {
            origin = "ist",
            destination = "ist",
            departureDate = DateOnly.FromDateTime(DateTime.UtcNow).AddDays(-2).ToString("yyyy-MM-dd"),
            adults = 1,
            children = 0,
            infants = 3,
        });
        JsonElement error = (await ApiCalls.ReadAsync(response)).GetProperty("error");

        response.StatusCode.ShouldBe(HttpStatusCode.BadRequest);
        error.GetProperty("code").GetString().ShouldBe("InvalidRequest");
        error.GetProperty("details").EnumerateArray()
            .Select(static d => (d.GetProperty("target").GetString(), d.GetProperty("code").GetString()))
            .ShouldBe(
                [("origin", "InvalidAirport"), ("destination", "InvalidAirport"), ("destination", "OriginEqualsDestination"), ("departureDate", "DepartureInPast"), ("infants", "TooManyInfants")],
                ignoreOrder: true);
    }

    [Fact]
    public async Task A_search_without_an_api_version_is_refused()
    {
        using HttpResponseMessage response = await flights.Client.PostAsJsonAsync("/flights/searches", Travel.IstanbulToLondon());

        response.StatusCode.ShouldBe(HttpStatusCode.BadRequest);
        (await ApiCalls.ReadAsync(response)).GetProperty("error").GetProperty("code").GetString().ShouldBe("MissingApiVersionParameter");
    }
}

/// <summary>The circuit breaker per airline (the requirements' reliability section), on the whole service.</summary>
public sealed class CircuitBreakerSearchTests(BreakerFlights flights) : IClassFixture<BreakerFlights>
{
    [Fact]
    public async Task A_failing_airline_is_skipped_without_a_call_once_its_circuit_is_open()
    {
        await flights.Sim.QuietAsync();
        await flights.Sim.SetKnobsAsync("JG", new { Outage = true });

        // Three searches, three failed calls to JG: the breaker opens at three.
        for (int i = 0; i < 3; i++)
        {
            (_, JsonElement search) = await flights.Client.SearchAsync(Travel.IstanbulToLondon());
            Reasons(search).ShouldBe([("JG", "Error")]);
        }

        flights.Host.Wire.Count("JG", HttpMethod.Post, "/search").ShouldBe(3);

        var clock = Stopwatch.StartNew();
        for (int i = 0; i < 3; i++)
        {
            (HttpStatusCode status, JsonElement search) = await flights.Client.SearchAsync(Travel.IstanbulToLondon());
            status.ShouldBe(HttpStatusCode.Created);
            Reasons(search).ShouldBe([("JG", "CircuitOpen")]);
            (await flights.Client.AllOffersAsync(search)).Select(static o => o.GetProperty("airline").GetString()).Distinct().Order().ShouldBe(["YZ", "ZC", "ZJ"]);
        }

        // The airline was never asked again, and the other three were.
        flights.Host.Wire.Count("JG", HttpMethod.Post, "/search").ShouldBe(3);
        flights.Host.Wire.Count("ZC", HttpMethod.Post, "/search").ShouldBe(6);
        clock.Elapsed.ShouldBeLessThan(TimeSpan.FromSeconds(5));
    }

    private static (string?, string?)[] Reasons(JsonElement search) =>
        [.. search.GetProperty("incompleteSuppliers").EnumerateArray().Select(static s => (s.GetProperty("airline").GetString(), s.GetProperty("reason").GetString()))];
}
