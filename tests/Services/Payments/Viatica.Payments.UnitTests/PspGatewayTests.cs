using System.Net;
using System.Text;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Viatica.Kernel;
using Viatica.Payments.Application.Provider;
using Viatica.Payments.Domain.Cards;
using Viatica.Payments.Infrastructure.Provider;

namespace Viatica.Payments.UnitTests;

/// <summary>
/// The adapter against a scripted provider: what it sends, how it reads each answer, which failures it says might have
/// worked, and what the resilience pipeline does. The same adapter against the real simulator is exercised by the
/// integration tests; this is where the awkward statuses are easy to produce.
/// </summary>
public sealed class PspGatewayTests : IDisposable
{
    private readonly ServiceProvider services;
    private readonly ScriptedPsp psp = new();

    public PspGatewayTests()
        : this(requestTimeout: "00:00:05")
    {
    }

    private PspGatewayTests(string requestTimeout)
    {
        var config = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["Payments:Provider:BaseUrl"] = "http://psp.test/",
            ["Payments:Provider:RequestTimeout"] = requestTimeout,
            ["Payments:Provider:CircuitBreaker:MinimumThroughput"] = "4",
            ["Payments:Provider:CircuitBreaker:FailureRatio"] = "0.5",
        }).Build();

        var collection = new ServiceCollection();
        collection.AddLogging();
        collection.AddPspClient(config);
        collection.AddHttpClient(PspClient.Name).ConfigurePrimaryHttpMessageHandler(() => psp);
        services = collection.BuildServiceProvider();
    }

    public void Dispose() => services.Dispose();

    private PspGateway Gateway() => new(services.GetRequiredService<IHttpClientFactory>(), TimeProvider.System);

    private static Task<ProviderResult> CaptureAsync(PspGateway gateway) =>
        gateway.CaptureAsync("pi_1", Money.Eur(312.50m), "key-1", CancellationToken.None);

    [Fact]
    public async Task A_success_is_the_providers_id_and_the_request_is_in_minor_units_with_the_key()
    {
        psp.Reply(HttpStatusCode.OK, """{ "id": "pi_1", "status": "succeeded" }""");

        ProviderResult result = await CaptureAsync(Gateway());

        result.Case<ProviderSucceeded>().Reference.ShouldBe("pi_1");
        SentRequest sent = psp.Sent.Single();
        sent.Method.ShouldBe(HttpMethod.Post);
        sent.PathAndQuery.ShouldBe("/v1/payment_intents/pi_1/capture");
        sent.IdempotencyKey.ShouldBe("key-1");
        sent.Body.ShouldBe("""{"amount_to_capture":31250}""");
    }

    [Fact]
    public async Task Amounts_are_in_the_currencys_own_minor_unit()
    {
        psp.Reply(HttpStatusCode.OK, """{ "id": "re_1" }""");
        psp.Reply(HttpStatusCode.OK, """{ "id": "pi_2" }""");

        await Gateway().RefundAsync("pi_1", new Money(1200m, "JPY"), Guid.Empty, Guid.Empty, "k", CancellationToken.None);
        await Gateway().AuthoriseAsync("tok_1", new Money(1.234m, "KWD"), Guid.Empty, Guid.Empty, "k", CancellationToken.None);

        psp.Sent[0].Body.ShouldContain("\"amount\":1200,");
        psp.Sent[1].Body.ShouldContain("\"amount\":1234,");
        psp.Sent[1].Body.ShouldContain("\"currency\":\"kwd\"");
        psp.Sent[1].Body.ShouldContain("\"capture_method\":\"manual\"");
    }

    [Fact]
    public async Task A_card_error_is_a_refusal_with_the_decline_code_not_a_failure()
    {
        psp.Reply(HttpStatusCode.PaymentRequired, """{ "error": { "type": "card_error", "code": "card_declined", "decline_code": "insufficient_funds" } }""");

        ProviderResult result = await Gateway().AuthoriseAsync("tok_1", Money.Eur(10m), Guid.Empty, Guid.Empty, "k", CancellationToken.None);

        result.Case<ProviderRefused>().Code.ShouldBe("insufficient_funds");
    }

    [Theory]
    [InlineData(HttpStatusCode.BadRequest)]
    [InlineData(HttpStatusCode.NotFound)]
    [InlineData(HttpStatusCode.Conflict)]
    [InlineData(HttpStatusCode.UnprocessableEntity)]
    public async Task A_request_the_provider_rejects_is_a_refusal(HttpStatusCode status)
    {
        psp.Reply(status, """{ "error": { "type": "invalid_request_error", "code": "amount_too_large" } }""");

        (await CaptureAsync(Gateway())).Case<ProviderRefused>().Code.ShouldBe("amount_too_large");
    }

    [Fact]
    public async Task A_503_is_a_failure_that_certainly_did_nothing()
    {
        psp.Reply(HttpStatusCode.ServiceUnavailable, "");

        ProviderFailure failure = (await CaptureAsync(Gateway())).Case<ProviderFailure>();

        failure.MayHaveReachedProvider.ShouldBeFalse();
    }

    [Theory]
    [InlineData(HttpStatusCode.InternalServerError, FailureKind.Error)]
    [InlineData(HttpStatusCode.BadGateway, FailureKind.Error)]
    [InlineData(HttpStatusCode.GatewayTimeout, FailureKind.Timeout)]
    public async Task Every_other_server_failure_may_have_been_carried_out(HttpStatusCode status, FailureKind kind)
    {
        psp.Reply(status, "");

        ProviderFailure failure = (await CaptureAsync(Gateway())).Case<ProviderFailure>();

        failure.Kind.ShouldBe(kind);
        failure.MayHaveReachedProvider.ShouldBeTrue();
    }

    [Theory]
    [InlineData(HttpStatusCode.Unauthorized)]
    [InlineData(HttpStatusCode.Forbidden)]
    [InlineData(HttpStatusCode.TooManyRequests)]
    public async Task A_status_the_provider_says_before_doing_anything_is_a_failure_that_did_nothing(HttpStatusCode status)
    {
        psp.Reply(status, "");

        (await CaptureAsync(Gateway())).Case<ProviderFailure>().MayHaveReachedProvider.ShouldBeFalse();
    }

    [Fact]
    public async Task A_dropped_connection_may_have_reached_the_provider()
    {
        psp.Throw(new HttpRequestException("connection reset"));

        ProviderFailure failure = (await CaptureAsync(Gateway())).Case<ProviderFailure>();

        failure.MayHaveReachedProvider.ShouldBeTrue();
    }

    [Fact]
    public async Task An_answer_that_cannot_be_read_is_a_provider_in_trouble_not_a_yes_or_a_no()
    {
        psp.Reply(HttpStatusCode.OK, "<html>oops</html>");

        ProviderFailure failure = (await CaptureAsync(Gateway())).Case<ProviderFailure>();

        failure.MayHaveReachedProvider.ShouldBeTrue();
    }

    [Fact]
    public async Task A_call_slower_than_the_timeout_is_a_timeout_that_may_have_been_carried_out()
    {
        using var impatient = new PspGatewayTests(requestTimeout: "00:00:00.200");
        impatient.psp.Hang();

        ProviderFailure failure = (await CaptureAsync(impatient.Gateway())).Case<ProviderFailure>();

        failure.Kind.ShouldBe(FailureKind.Timeout);
        failure.MayHaveReachedProvider.ShouldBeTrue();
    }

    [Fact]
    public async Task A_failing_call_is_not_retried()
    {
        psp.Reply(HttpStatusCode.InternalServerError, "");

        await CaptureAsync(Gateway());

        psp.Sent.Count.ShouldBe(1, "a retry in the transport would hide the decision from the code that has to make it");
    }

    [Fact]
    public async Task A_circuit_opens_after_repeated_failures_and_the_provider_is_not_called_again_until_it_closes()
    {
        for (int i = 0; i < 4; i++)
        {
            psp.Reply(HttpStatusCode.InternalServerError, "");
            await CaptureAsync(Gateway());
        }

        int callsBefore = psp.Sent.Count;
        ProviderFailure failure = (await CaptureAsync(Gateway())).Case<ProviderFailure>();

        failure.Kind.ShouldBe(FailureKind.CircuitOpen);
        failure.MayHaveReachedProvider.ShouldBeFalse("nothing was sent");
        psp.Sent.Count.ShouldBe(callsBefore);
    }

    [Fact]
    public async Task A_refusal_is_not_a_failing_provider_so_it_never_opens_the_circuit()
    {
        for (int i = 0; i < 12; i++)
        {
            psp.Reply(HttpStatusCode.PaymentRequired, """{ "error": { "code": "card_declined", "decline_code": "generic_decline" } }""");
            await Gateway().AuthoriseAsync("tok_1", Money.Eur(10m), Guid.Empty, Guid.Empty, "k", CancellationToken.None);
        }

        psp.Reply(HttpStatusCode.OK, """{ "id": "pi_1" }""");
        (await CaptureAsync(Gateway())).Case<ProviderSucceeded>();
    }

    [Fact]
    public async Task The_lookup_reads_what_the_provider_recorded_the_way_the_first_answer_would_have_been_read()
    {
        psp.Reply(HttpStatusCode.OK, """{ "object": "operation", "type": "capture", "http_status": 200, "result": { "id": "pi_9", "status": "succeeded" } }""");
        psp.Reply(HttpStatusCode.OK, """{ "object": "operation", "type": "authorise", "http_status": 402, "result": { "error": { "code": "card_declined", "decline_code": "generic_decline" } } }""");

        LookupResult succeeded = await Gateway().LookUpAsync("key-1", CancellationToken.None);
        LookupResult declined = await Gateway().LookUpAsync("key-2", CancellationToken.None);

        succeeded.Case<OperationFound>().Answer.Case<ProviderSucceeded>().Reference.ShouldBe("pi_9");
        declined.Case<OperationFound>().Answer.Case<ProviderRefused>().Code.ShouldBe("generic_decline");
        psp.Sent[0].Method.ShouldBe(HttpMethod.Get);
        psp.Sent[0].PathAndQuery.ShouldBe("/v1/operations?idempotency_key=key-1");
        psp.Sent[0].IdempotencyKey.ShouldBeNull("a lookup is a read");
    }

    [Fact]
    public async Task The_lookup_of_an_unknown_key_is_no_such_operation_but_only_for_the_providers_own_missing_resource_code()
    {
        psp.Reply(HttpStatusCode.NotFound, """{ "error": { "type": "invalid_request_error", "code": "resource_missing" } }""");
        psp.Reply(HttpStatusCode.NotFound, "");
        psp.Reply(HttpStatusCode.NotFound, """{ "error": { "code": "route_not_found" } }""");

        (await Gateway().LookUpAsync("k", CancellationToken.None)).Case<NoSuchOperation>();
        (await Gateway().LookUpAsync("k", CancellationToken.None)).Case<ProviderFailure>();
        (await Gateway().LookUpAsync("k", CancellationToken.None)).Case<ProviderFailure>();
    }

    [Theory]
    [InlineData(HttpStatusCode.ServiceUnavailable)]
    [InlineData(HttpStatusCode.InternalServerError)]
    [InlineData(HttpStatusCode.GatewayTimeout)]
    public async Task A_lookup_that_fails_is_a_failure_and_not_an_answer(HttpStatusCode status)
    {
        psp.Reply(status, "");

        (await Gateway().LookUpAsync("k", CancellationToken.None)).Case<ProviderFailure>();
    }

    [Fact]
    public async Task A_recorded_operation_the_provider_never_finished_is_not_taken_as_an_answer()
    {
        psp.Reply(HttpStatusCode.OK, """{ "type": "capture", "http_status": 500, "result": { } }""");

        (await Gateway().LookUpAsync("k", CancellationToken.None)).Case<ProviderFailure>();
    }

    [Fact]
    public async Task Tokenising_sends_the_card_to_the_provider_once_and_reads_back_only_the_brand_and_last_four()
    {
        psp.Reply(HttpStatusCode.OK, """{ "id": "tok_1", "object": "token", "card": { "brand": "visa", "last4": "4242", "exp_month": 12, "exp_year": 2030 } }""");
        CardDetails card = CardDetails.TryCreate("4242 4242 4242 4242", 12, 2030, "123", new DateOnly(2026, 11, 2))!;

        TokeniseOutcome outcome = await Gateway().TokeniseAsync(card, "tokenise:1", CancellationToken.None);

        Tokenised tokenised = outcome.Case<Tokenised>();
        tokenised.ProviderToken.ShouldBe("tok_1");
        tokenised.Last4.ShouldBe("4242");
        psp.Sent.Single().Body.ShouldBe("""{"card":{"number":"4242424242424242","exp_month":12,"exp_year":2030,"cvc":"123"}}""");
        psp.Sent.Single().IdempotencyKey.ShouldBe("tokenise:1");
    }

    [Fact]
    public async Task A_card_the_provider_refuses_is_a_refusal_whose_code_does_not_repeat_the_card()
    {
        psp.Reply(HttpStatusCode.PaymentRequired, """{ "error": { "type": "card_error", "code": "incorrect_number", "message": "The card number is incorrect." } }""");
        CardDetails card = CardDetails.TryCreate("4111111111111111", 12, 2030, null, new DateOnly(2026, 11, 2))!;

        TokeniseOutcome outcome = await Gateway().TokeniseAsync(card, "tokenise:2", CancellationToken.None);

        outcome.Case<TokenRefused>().Code.ShouldBe("incorrect_number");
    }

    [Fact]
    public async Task The_card_number_is_in_the_tokenise_request_and_in_no_other_request()
    {
        psp.Reply(HttpStatusCode.OK, """{ "id": "tok_1", "card": { "brand": "visa", "last4": "4242", "exp_month": 12, "exp_year": 2030 } }""");
        psp.Reply(HttpStatusCode.OK, """{ "id": "pi_1" }""");
        psp.Reply(HttpStatusCode.OK, """{ "id": "pi_1" }""");
        psp.Reply(HttpStatusCode.OK, """{ "id": "pi_1" }""");
        psp.Reply(HttpStatusCode.OK, """{ "id": "re_1" }""");
        PspGateway gateway = Gateway();
        CardDetails card = CardDetails.TryCreate("4242424242424242", 12, 2030, "123", new DateOnly(2026, 11, 2))!;

        await gateway.TokeniseAsync(card, "t", CancellationToken.None);
        await gateway.AuthoriseAsync("tok_1", Money.Eur(10m), Guid.CreateVersion7(), Guid.CreateVersion7(), "a", CancellationToken.None);
        await gateway.CaptureAsync("pi_1", Money.Eur(10m), "c", CancellationToken.None);
        await gateway.VoidAsync("pi_1", "v", CancellationToken.None);
        await gateway.RefundAsync("pi_1", Money.Eur(1m), Guid.CreateVersion7(), Guid.CreateVersion7(), "r", CancellationToken.None);

        psp.Sent[0].Body.ShouldContain("4242424242424242");
        foreach (SentRequest sent in psp.Sent.Skip(1))
        {
            sent.Body.ShouldNotContain("4242424242424242");
            sent.PathAndQuery.ShouldNotContain("4242424242424242");
        }
    }
}

internal sealed record SentRequest(HttpMethod Method, string PathAndQuery, string? IdempotencyKey, string Body);

/// <summary>A provider that answers from a queue, records every request that reaches it, and can hang or fail on cue.</summary>
internal sealed class ScriptedPsp : HttpMessageHandler
{
    private readonly Queue<Func<CancellationToken, Task<HttpResponseMessage>>> script = [];

    public List<SentRequest> Sent { get; } = [];

    public void Reply(HttpStatusCode status, string body) =>
        script.Enqueue(_ => Task.FromResult(new HttpResponseMessage(status)
        {
            Content = new StringContent(body, Encoding.UTF8, "application/json"),
        }));

    public void Throw(Exception exception) => script.Enqueue(_ => throw exception);

    public void Hang() =>
        script.Enqueue(async cancellationToken =>
        {
            await Task.Delay(TimeSpan.FromMinutes(1), cancellationToken);
            return new HttpResponseMessage(HttpStatusCode.OK);
        });

    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        string body = request.Content is null ? "" : await request.Content.ReadAsStringAsync(cancellationToken);
        Sent.Add(new SentRequest(
            request.Method,
            request.RequestUri!.PathAndQuery,
            request.Headers.TryGetValues("Idempotency-Key", out IEnumerable<string>? values) ? values.Single() : null,
            body));

        return await script.Dequeue()(cancellationToken);
    }
}
