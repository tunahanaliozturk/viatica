using System.Net;
using System.Text;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Viatica.Flights.Application;
using Viatica.Flights.Application.Suppliers;
using Viatica.Flights.Domain;
using Viatica.Flights.Infrastructure.Suppliers;
using Viatica.Kernel;
using Viatica.Kernel.Pii;

namespace Viatica.Flights.UnitTests;

/// <summary>
/// The adapter against a scripted airline: what it sends, how it reads each answer, which failures it says might
/// have worked, and what the resilience pipeline does. The same adapter against the real simulator is exercised by
/// the integration tests; this is where the awkward statuses are easy to produce.
/// </summary>
public sealed class AirlineGatewayTests : IDisposable
{
    private readonly ServiceProvider services;
    private readonly Dictionary<string, ScriptedAirline> airlines = [];

    public AirlineGatewayTests()
        : this(requestTimeout: "00:00:05")
    {
    }

    private AirlineGatewayTests(string requestTimeout)
    {
        var config = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["Flights:Suppliers:Airlines:0:Code"] = "ZC",
            ["Flights:Suppliers:Airlines:0:BaseUrl"] = "http://zc.test/ndc/ZC/",
            ["Flights:Suppliers:Airlines:1:Code"] = "YZ",
            ["Flights:Suppliers:Airlines:1:BaseUrl"] = "http://yz.test/ndc/YZ/",
            ["Flights:Suppliers:RequestTimeout"] = requestTimeout,
            ["Flights:Suppliers:CircuitBreaker:MinimumThroughput"] = "4",
            ["Flights:Suppliers:CircuitBreaker:FailureRatio"] = "0.5",
        }).Build();

        var collection = new ServiceCollection();
        collection.AddLogging();
        collection.AddAirlineClients(config);
        foreach (string code in (string[])["ZC", "YZ"])
        {
            var airline = new ScriptedAirline();
            airlines[code] = airline;
            collection.AddHttpClient(AirlineClients.NameOf(code)).ConfigurePrimaryHttpMessageHandler(() => airline);
        }

        services = collection.BuildServiceProvider();
    }

    public void Dispose() => services.Dispose();

    private ScriptedAirline Zc => airlines["ZC"];

    private AirlineGateway Gateway(TimeSpan? searchDeadline = null) => new(
        services.GetRequiredService<IHttpClientFactory>(),
        services.GetRequiredService<IOptions<SuppliersOptions>>(),
        new FlightsOptions { SearchDeadline = searchDeadline ?? TimeSpan.FromSeconds(2.5) },
        TimeProvider.System,
        NullLogger<AirlineGateway>.Instance);

    private static SearchCriteria Criteria() =>
        new(new IataCode("IST"), new IataCode("LHR"), new DateOnly(2026, 11, 23), null, new PassengerMix(2, 1, 1), Cabin.Business);

    private const string OfferJson = """
        { "OfferId": "tok-1", "ExpiresAt": "2026-11-02T08:15:00+00:00", "FareClass": "K", "Cabin": "Economy", "Refundable": false,
          "Price": { "Base": 216.00, "Taxes": 42.88, "Total": 258.88, "Currency": "EUR" }, "SeatsLeft": 25,
          "Segments": [ { "FlightNumber": "ZC239", "Origin": "IST", "Destination": "LHR", "DepartureLocal": "2026-11-23T14:55:00",
                          "ArrivalLocal": "2026-11-23T15:45:00", "DurationMinutes": 230, "Aircraft": "Airbus A321neo" } ] }
        """;

    private static string OrderJson(string status = "Booked", string extra = "") => $$"""
        { "BookingReference": "PPT4VZ", "IdempotencyKey": "key-1", "Status": "{{status}}", "FareClass": "K",
          "Passengers": { "Adults": 1, "Children": 0, "Infants": 0 },
          "Price": { "Base": 216.00, "Taxes": 42.88, "Total": 258.88, "Currency": "EUR" },
          "CreatedAt": "2026-11-02T08:00:00Z", "TicketingTimeLimit": "2026-11-03T08:00:00Z",
          "TicketedAt": null, "TicketNumbers": {{(status == "Ticketed" ? "[\"9011234567890\"]" : "[]")}}, "VoidedAt": null, "CancelledAt": null,
          "RefundAmount": {{(status == "Cancelled" ? "42.88" : "null")}}, "Segments": [] {{extra}} }
        """;

    private static string Error(string code, string text = "text") => $$"""{ "Errors": [ { "Code": "{{code}}", "DescText": "{{text}}" } ] }""";

    // --- search ---

    [Fact]
    public async Task A_search_posts_the_airlines_own_shape_to_the_airlines_own_path()
    {
        Zc.Respond(_ => ScriptedAirline.Json(HttpStatusCode.OK, """{ "Airline": "ZC", "Offers": [] }"""));

        await Gateway().SearchAsync("ZC", Criteria(), CancellationToken.None);

        ScriptedAirline.Request sent = Zc.Requests.Single();
        sent.Method.ShouldBe(HttpMethod.Post);
        sent.Url.ShouldBe("http://zc.test/ndc/ZC/search");
        sent.Body.ShouldBe("""{"Origin":"IST","Destination":"LHR","DepartureDate":"2026-11-23","ReturnDate":null,"Passengers":{"Adults":2,"Children":1,"Infants":1},"Cabin":"Business"}""");
    }

    [Fact]
    public async Task Offers_are_normalised_into_the_platforms_model()
    {
        Zc.Respond(_ => ScriptedAirline.Json(HttpStatusCode.OK, $$"""{ "Airline": "ZC", "Offers": [ {{OfferJson}} ] }"""));

        SearchOutcome outcome = await Gateway().SearchAsync("ZC", Criteria(), CancellationToken.None);

        SupplierOffer offer = outcome.Case<SupplierOffers>().Offers.Single();
        offer.SupplierOfferId.ShouldBe("tok-1");
        offer.Offer.Airline.ShouldBe("ZC");
        offer.Offer.FareClass.ShouldBe("K");
        offer.Offer.Cabin.ShouldBe(Cabin.Economy);
        offer.Offer.Total.ShouldBe(Money.Eur(258.88m));
        offer.Offer.SeatsLeft.ShouldBe(25);
        offer.Offer.ExpiresAt.ShouldBe(new DateTimeOffset(2026, 11, 2, 8, 15, 0, TimeSpan.Zero));
        offer.Offer.Segments.Single().ShouldBe(new Application.Offers.FlightSegment(
            "ZC239", "IST", "LHR", new DateTime(2026, 11, 23, 14, 55, 0), new DateTime(2026, 11, 23, 15, 45, 0), 230, "Airbus A321neo"));
    }

    [Fact]
    public async Task An_offer_the_adapter_cannot_read_is_dropped_and_the_rest_are_kept()
    {
        string unreadable = OfferJson.Replace("\"Economy\"", "\"FirstClassSuite\"", StringComparison.Ordinal);
        string tooPrecise = OfferJson.Replace("258.88", "258.885", StringComparison.Ordinal);
        Zc.Respond(_ => ScriptedAirline.Json(HttpStatusCode.OK, $$"""{ "Airline": "ZC", "Offers": [ {{unreadable}}, {{tooPrecise}}, {{OfferJson}} ] }"""));

        SearchOutcome outcome = await Gateway().SearchAsync("ZC", Criteria(), CancellationToken.None);

        outcome.Case<SupplierOffers>().Offers.Count.ShouldBe(1);
    }

    // --- failures: did the airline maybe act? ---

    [Theory]
    [InlineData(503, FailureKind.Error, false)]   // unavailable before doing anything
    [InlineData(500, FailureKind.Error, true)]
    [InlineData(502, FailureKind.Error, true)]
    [InlineData(504, FailureKind.Timeout, true)]
    public async Task A_server_error_says_whether_the_airline_might_have_acted(int status, FailureKind kind, bool mayHaveReached)
    {
        Zc.Respond(_ => ScriptedAirline.Json((HttpStatusCode)status, Error("SERVICE_UNAVAILABLE")));

        SearchOutcome outcome = await Gateway().SearchAsync("ZC", Criteria(), CancellationToken.None);

        outcome.Case<SupplierFailure>().ShouldBe(new SupplierFailure(kind, mayHaveReached));
    }

    [Fact]
    public async Task A_dropped_connection_might_have_reached_the_airline()
    {
        Zc.Respond(_ => throw new HttpRequestException("connection reset"));

        (await Gateway().SearchAsync("ZC", Criteria(), CancellationToken.None))
            .Case<SupplierFailure>().ShouldBe(new SupplierFailure(FailureKind.Error, true));
    }

    [Fact]
    public async Task An_unreadable_body_is_an_airline_in_trouble_and_not_a_no()
    {
        Zc.Respond(_ => ScriptedAirline.Json(HttpStatusCode.OK, "<html>maintenance</html>"));

        (await Gateway().SearchAsync("ZC", Criteria(), CancellationToken.None))
            .Case<SupplierFailure>().Kind.ShouldBe(FailureKind.Error);
    }

    [Fact]
    public async Task A_search_gives_up_at_the_deadline_and_says_timeout()
    {
        Zc.RespondAsync(async cancellationToken =>
        {
            await Task.Delay(TimeSpan.FromSeconds(10), cancellationToken);
            return ScriptedAirline.Json(HttpStatusCode.OK, """{ "Airline": "ZC", "Offers": [] }""");
        });

        DateTime started = DateTime.UtcNow;
        SearchOutcome outcome = await Gateway(searchDeadline: TimeSpan.FromMilliseconds(300)).SearchAsync("ZC", Criteria(), CancellationToken.None);

        outcome.Case<SupplierFailure>().ShouldBe(new SupplierFailure(FailureKind.Timeout, true));
        (DateTime.UtcNow - started).ShouldBeLessThan(TimeSpan.FromSeconds(3));
    }

    [Fact]
    public async Task The_caller_hanging_up_is_a_cancellation_and_not_a_failure()
    {
        Zc.RespondAsync(async cancellationToken =>
        {
            await Task.Delay(TimeSpan.FromSeconds(10), cancellationToken);
            return ScriptedAirline.Json(HttpStatusCode.OK, "{}");
        });
        using var cts = new CancellationTokenSource(TimeSpan.FromMilliseconds(100));

        await Should.ThrowAsync<OperationCanceledException>(() => Gateway().SearchAsync("ZC", Criteria(), cts.Token));
    }

    // --- resilience ---

    [Fact]
    public async Task A_failing_call_is_not_retried()
    {
        Zc.Respond(_ => ScriptedAirline.Json(HttpStatusCode.InternalServerError, Error("BOOM")));

        await Gateway().SearchAsync("ZC", Criteria(), CancellationToken.None);

        Zc.Requests.Count.ShouldBe(1);
    }

    [Fact]
    public async Task A_circuit_opens_after_repeated_failures_and_the_airline_is_skipped_without_a_call()
    {
        Zc.Respond(_ => ScriptedAirline.Json(HttpStatusCode.InternalServerError, Error("BOOM")));
        AirlineGateway gateway = Gateway();

        for (int i = 0; i < 4; i++)
        {
            (await gateway.SearchAsync("ZC", Criteria(), CancellationToken.None)).Case<SupplierFailure>().Kind.ShouldBe(FailureKind.Error);
        }

        SearchOutcome skipped = await gateway.SearchAsync("ZC", Criteria(), CancellationToken.None);

        skipped.Case<SupplierFailure>().ShouldBe(new SupplierFailure(FailureKind.CircuitOpen, MayHaveReachedAirline: false));
        Zc.Requests.Count.ShouldBe(4);
    }

    [Fact]
    public async Task Too_slow_counts_as_failing_so_a_slow_airline_opens_its_circuit_too()
    {
        Zc.RespondAsync(async cancellationToken =>
        {
            await Task.Delay(TimeSpan.FromSeconds(5), cancellationToken);
            return ScriptedAirline.Json(HttpStatusCode.OK, "{}");
        });
        AirlineGateway gateway = Gateway(searchDeadline: TimeSpan.FromMilliseconds(100));

        for (int i = 0; i < 4; i++)
        {
            (await gateway.SearchAsync("ZC", Criteria(), CancellationToken.None)).Case<SupplierFailure>().Kind.ShouldBe(FailureKind.Timeout);
        }

        (await gateway.SearchAsync("ZC", Criteria(), CancellationToken.None)).Case<SupplierFailure>().Kind.ShouldBe(FailureKind.CircuitOpen);
    }

    [Fact]
    public async Task One_airlines_open_circuit_does_not_touch_another()
    {
        Zc.Respond(_ => ScriptedAirline.Json(HttpStatusCode.InternalServerError, Error("BOOM")));
        airlines["YZ"].Respond(_ => ScriptedAirline.Json(HttpStatusCode.OK, """{ "Airline": "YZ", "Offers": [] }"""));
        AirlineGateway gateway = Gateway();

        for (int i = 0; i < 6; i++)
        {
            await gateway.SearchAsync("ZC", Criteria(), CancellationToken.None);
        }

        (await gateway.SearchAsync("YZ", Criteria(), CancellationToken.None)).Case<SupplierOffers>();
    }

    [Fact]
    public async Task An_airline_saying_no_is_not_an_airline_failing()
    {
        Zc.Respond(_ => ScriptedAirline.Json(HttpStatusCode.UnprocessableEntity, Error("TICKETING_REFUSED", "declined")));
        AirlineGateway gateway = Gateway();

        // Ten refusals in a row would open any breaker that counted them.
        for (int i = 0; i < 10; i++)
        {
            (await gateway.TicketAsync("ZC", "PPT4VZ", CancellationToken.None)).Case<TicketsRefused>();
        }

        Zc.Requests.Count.ShouldBe(10);
    }

    // --- orders ---

    [Fact]
    public async Task An_order_carries_our_key_and_the_passengers_in_the_airlines_types_and_nothing_else()
    {
        Zc.Respond(_ => ScriptedAirline.Json(HttpStatusCode.Created, OrderJson()));
        PassengerDetails[] passengers =
        [
            new(PassengerType.Adult, "Ayse", "Yilmaz", new DateOnly(1988, 4, 2), "U12345678"),
            new(PassengerType.Child, "Deniz", "Yilmaz", new DateOnly(2018, 1, 1)),
            new(PassengerType.Infant, "Ela", "Yilmaz", new DateOnly(2026, 3, 1)),
        ];

        CreateOrderOutcome outcome = await Gateway().CreateOrderAsync("ZC", new NewSupplierOrder("tok-1", "key-1", passengers), CancellationToken.None);

        ScriptedAirline.Request sent = Zc.Requests.Single();
        sent.Url.ShouldBe("http://zc.test/ndc/ZC/orders");
        sent.Body.ShouldBe(
            """{"OfferId":"tok-1","IdempotencyKey":"key-1","Passengers":[{"Type":"ADT","GivenName":"Ayse","Surname":"Yilmaz","BirthDate":"1988-04-02"},{"Type":"CHD","GivenName":"Deniz","Surname":"Yilmaz","BirthDate":"2018-01-01"},{"Type":"INF","GivenName":"Ela","Surname":"Yilmaz","BirthDate":"2026-03-01"}]}""");
        sent.Body.ShouldNotContain("U12345678");

        SupplierOrder order = outcome.Case<OrderCreated>().Order;
        order.BookingReference.ShouldBe("PPT4VZ");
        order.Status.ShouldBe(SupplierOrderStatus.Booked);
        order.Total.ShouldBe(Money.Eur(258.88m));
        order.TicketingTimeLimit.ShouldBe(new DateTimeOffset(2026, 11, 3, 8, 0, 0, TimeSpan.Zero));
    }

    [Theory]
    [InlineData(HttpStatusCode.Created)]
    [InlineData(HttpStatusCode.OK)]
    public async Task A_new_order_and_a_replayed_one_are_the_same_order(HttpStatusCode status)
    {
        Zc.Respond(_ => ScriptedAirline.Json(status, OrderJson()));

        (await Gateway().CreateOrderAsync("ZC", new NewSupplierOrder("tok-1", "key-1", []), CancellationToken.None)).Case<OrderCreated>();
    }

    [Theory]
    [InlineData(HttpStatusCode.Conflict, "NO_AVAILABILITY", RefusalReason.SoldOut)]
    [InlineData(HttpStatusCode.Gone, "OFFER_EXPIRED", RefusalReason.OfferExpired)]
    [InlineData(HttpStatusCode.NotFound, "OFFER_NOT_FOUND", RefusalReason.OfferExpired)]
    [InlineData(HttpStatusCode.BadRequest, "INVALID_REQUEST", RefusalReason.InvalidPassengers)]
    [InlineData(HttpStatusCode.Conflict, "IDEMPOTENCY_KEY_REUSED", RefusalReason.Other)]
    public async Task A_refused_order_says_why(HttpStatusCode status, string code, RefusalReason reason)
    {
        Zc.Respond(_ => ScriptedAirline.Json(status, Error(code)));

        (await Gateway().CreateOrderAsync("ZC", new NewSupplierOrder("tok-1", "key-1", []), CancellationToken.None))
            .Case<OrderRefused>().Reason.ShouldBe(reason);
    }

    [Fact]
    public async Task A_timed_out_creation_is_a_failure_that_might_have_worked_and_is_never_retried()
    {
        using var slow = new AirlineGatewayTests(requestTimeout: "00:00:00.200");
        slow.Zc.RespondAsync(async cancellationToken =>
        {
            await Task.Delay(TimeSpan.FromSeconds(5), cancellationToken);
            return ScriptedAirline.Json(HttpStatusCode.Created, OrderJson());
        });

        CreateOrderOutcome outcome = await slow.Gateway().CreateOrderAsync("ZC", new NewSupplierOrder("tok-1", "key-1", []), CancellationToken.None);

        outcome.Case<SupplierFailure>().ShouldBe(new SupplierFailure(FailureKind.Timeout, MayHaveReachedAirline: true));
        slow.Zc.Requests.Count.ShouldBe(1);
    }

    [Fact]
    public async Task Finding_an_order_by_key_asks_with_the_key_and_reads_the_order_or_its_absence()
    {
        Zc.Respond(request => request.RequestUri!.Query.Contains("known", StringComparison.Ordinal)
            ? ScriptedAirline.Json(HttpStatusCode.OK, OrderJson())
            : ScriptedAirline.Json(HttpStatusCode.NotFound, Error("ORDER_NOT_FOUND")));

        (await Gateway().FindOrderAsync("ZC", "known-key", CancellationToken.None)).Case<OrderFound>();
        (await Gateway().FindOrderAsync("ZC", "other key&x", CancellationToken.None)).Case<NoSuchOrder>();

        Zc.Requests[0].Url.ShouldBe("http://zc.test/ndc/ZC/orders?idempotencyKey=known-key");
        Zc.Requests[1].Url.ShouldBe("http://zc.test/ndc/ZC/orders?idempotencyKey=other%20key%26x");
    }

    [Fact]
    public async Task A_lookup_that_cannot_be_answered_is_not_read_as_no_such_order()
    {
        Zc.Respond(_ => ScriptedAirline.Json(HttpStatusCode.InternalServerError, Error("BOOM")));

        (await Gateway().FindOrderAsync("ZC", "key", CancellationToken.None)).Case<SupplierFailure>();
    }

    [Fact]
    public async Task Ticketing_returns_the_ticket_numbers_or_the_airlines_reason()
    {
        Zc.Respond(_ => ScriptedAirline.Json(HttpStatusCode.OK, OrderJson("Ticketed")));
        (await Gateway().TicketAsync("ZC", "PPT4VZ", CancellationToken.None)).Case<TicketsIssued>().TicketNumbers.ShouldBe(["9011234567890"]);
        Zc.Requests.Single().Url.ShouldBe("http://zc.test/ndc/ZC/orders/PPT4VZ/ticket");

        Zc.Respond(_ => ScriptedAirline.Json(HttpStatusCode.UnprocessableEntity, Error("TICKETING_REFUSED", "The airline declined.")));
        (await Gateway().TicketAsync("ZC", "PPT4VZ", CancellationToken.None)).Case<TicketsRefused>().Reason.ShouldBe("TICKETING_REFUSED: The airline declined.");

        Zc.Respond(_ => ScriptedAirline.Json(HttpStatusCode.Conflict, Error("TICKETING_TIME_LIMIT_EXPIRED", "Too late.")));
        (await Gateway().TicketAsync("ZC", "PPT4VZ", CancellationToken.None)).Case<TicketsRefused>().Reason.ShouldStartWith("TICKETING_TIME_LIMIT_EXPIRED");
    }

    [Fact]
    public async Task Void_has_four_endings()
    {
        Zc.Respond(_ => ScriptedAirline.Json(HttpStatusCode.OK, OrderJson("Voided")));
        (await Gateway().VoidAsync("ZC", "PPT4VZ", CancellationToken.None)).Case<OrderVoided>();

        Zc.Respond(_ => ScriptedAirline.Json(HttpStatusCode.Conflict, Error("VOID_WINDOW_CLOSED")));
        (await Gateway().VoidAsync("ZC", "PPT4VZ", CancellationToken.None)).Case<VoidWindowClosed>();

        Zc.Respond(_ => ScriptedAirline.Json(HttpStatusCode.NotFound, Error("ORDER_NOT_FOUND")));
        (await Gateway().VoidAsync("ZC", "PPT4VZ", CancellationToken.None)).Case<OrderGone>();

        Zc.Respond(_ => ScriptedAirline.Json(HttpStatusCode.InternalServerError, Error("BOOM")));
        (await Gateway().VoidAsync("ZC", "PPT4VZ", CancellationToken.None)).Case<SupplierFailure>();
    }

    [Fact]
    public async Task Cancel_returns_the_airlines_refund_or_says_why_not()
    {
        Zc.Respond(_ => ScriptedAirline.Json(HttpStatusCode.OK, OrderJson("Cancelled")));
        (await Gateway().CancelAsync("ZC", "PPT4VZ", CancellationToken.None)).Case<OrderCancelled>().Refund.ShouldBe(Money.Eur(42.88m));

        Zc.Respond(_ => ScriptedAirline.Json(HttpStatusCode.Conflict, Error("ORDER_NOT_TICKETED")));
        (await Gateway().CancelAsync("ZC", "PPT4VZ", CancellationToken.None)).Case<NotTicketed>();

        Zc.Respond(_ => ScriptedAirline.Json(HttpStatusCode.Conflict, Error("ORDER_CLOSED")));
        (await Gateway().CancelAsync("ZC", "PPT4VZ", CancellationToken.None)).Case<OrderGone>();
    }

    // --- re-price ---

    [Fact]
    public async Task A_reprice_returns_the_new_offer_and_the_total_it_replaces()
    {
        Zc.Respond(_ => ScriptedAirline.Json(HttpStatusCode.OK, $$"""{ "Status": "Changed", "PreviousTotal": 240.00, "Offer": {{OfferJson}} }"""));

        RepriceOutcome outcome = await Gateway().RepriceAsync("ZC", "tok/with+odd=chars", CancellationToken.None);

        OfferRepriced repriced = outcome.Case<OfferRepriced>();
        repriced.PreviousTotal.ShouldBe(240.00m);
        repriced.Offer.Offer.Total.ShouldBe(Money.Eur(258.88m));
        Zc.Requests.Single().Url.ShouldBe("http://zc.test/ndc/ZC/offers/tok%2Fwith%2Bodd%3Dchars/price");
    }

    [Theory]
    [InlineData(HttpStatusCode.Gone, "OFFER_EXPIRED")]
    [InlineData(HttpStatusCode.NotFound, "OFFER_NOT_FOUND")]
    [InlineData(HttpStatusCode.Conflict, "NO_AVAILABILITY")]
    public async Task A_gone_offer_is_gone_whichever_way_the_airline_says_it(HttpStatusCode status, string code)
    {
        Zc.Respond(_ => ScriptedAirline.Json(status, Error(code)));

        (await Gateway().RepriceAsync("ZC", "tok", CancellationToken.None)).Case<OfferGone>();
    }
}

/// <summary>An airline that answers as scripted and remembers every request it was sent.</summary>
internal sealed class ScriptedAirline : HttpMessageHandler
{
    private Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> respond =
        (_, _) => Task.FromResult(new HttpResponseMessage(HttpStatusCode.NotImplemented));

    public List<Request> Requests { get; } = [];

    public static HttpResponseMessage Json(HttpStatusCode status, string body) =>
        new(status) { Content = new StringContent(body, Encoding.UTF8, "application/json") };

    public void Respond(Func<HttpRequestMessage, HttpResponseMessage> answer) => respond = (request, _) => Task.FromResult(answer(request));

    public void RespondAsync(Func<CancellationToken, Task<HttpResponseMessage>> answer) => respond = (_, cancellationToken) => answer(cancellationToken);

    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        string body = request.Content is null ? "" : await request.Content.ReadAsStringAsync(cancellationToken);
        lock (Requests)
        {
            Requests.Add(new Request(request.Method, request.RequestUri!.OriginalString, body));
        }

        return await respond(request, cancellationToken);
    }

    public sealed record Request(HttpMethod Method, string Url, string Body);
}
