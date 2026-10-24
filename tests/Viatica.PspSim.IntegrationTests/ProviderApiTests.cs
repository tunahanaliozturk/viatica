using System.Net;
using System.Text.Json;

namespace Viatica.PspSim.IntegrationTests;

/// <summary>
/// The provider as a client sees it: Stripe shaped JSON over HTTP, the test cards, idempotency keys and the lookup of an
/// operation by key. One host per test, so a test never sees another's intents.
/// </summary>
public sealed class ProviderApiTests : IAsyncLifetime
{
    private readonly SimHost sim = new();

    public async ValueTask InitializeAsync() => await sim.QuietAsync();

    public ValueTask DisposeAsync()
    {
        sim.Dispose();
        return ValueTask.CompletedTask;
    }

    [Fact]
    public async Task A_full_flow_speaks_in_the_providers_shapes()
    {
        string token = await sim.TokeniseAsync("4242424242424242");
        token.ShouldStartWith("tok_");

        (HttpStatusCode status, JsonElement intent, _) =
            await sim.PostAsync("/v1/payment_intents", SimHost.Authorisation(token, 31_250), "auth-1");
        status.ShouldBe(HttpStatusCode.OK);
        string id = intent.GetProperty("id").GetString()!;
        id.ShouldStartWith("pi_");
        intent.GetProperty("object").GetString().ShouldBe("payment_intent");
        intent.GetProperty("status").GetString().ShouldBe("requires_capture");
        intent.GetProperty("amount").GetInt64().ShouldBe(31_250);
        intent.GetProperty("amount_capturable").GetInt64().ShouldBe(31_250);
        intent.GetProperty("currency").GetString().ShouldBe("eur");
        intent.GetProperty("metadata").GetProperty("trip_id").GetString().ShouldBe("trip-1");

        (status, JsonElement captured, _) = await sim.PostAsync($"/v1/payment_intents/{id}/capture", new { amount_to_capture = 20_000 }, "cap-1");
        status.ShouldBe(HttpStatusCode.OK);
        captured.GetProperty("status").GetString().ShouldBe("succeeded");
        captured.GetProperty("amount_received").GetInt64().ShouldBe(20_000);

        (status, JsonElement refund, _) = await sim.PostAsync("/v1/refunds", new { payment_intent = id, amount = 5_000 }, "ref-1");
        status.ShouldBe(HttpStatusCode.OK);
        refund.GetProperty("id").GetString()!.ShouldStartWith("re_");
        refund.GetProperty("amount").GetInt64().ShouldBe(5_000);

        (_, JsonElement read) = await sim.GetAsync($"/v1/payment_intents/{id}");
        read.GetProperty("amount_refunded").GetInt64().ShouldBe(5_000);
    }

    [Fact]
    public async Task An_authorisation_can_be_voided_and_then_nothing_can_be_captured()
    {
        string id = await sim.AuthoriseAsync("4242424242424242");

        (HttpStatusCode status, JsonElement cancelled, _) = await sim.PostAsync($"/v1/payment_intents/{id}/cancel", null, "void-1");
        (HttpStatusCode late, JsonElement lateBody, _) = await sim.PostAsync($"/v1/payment_intents/{id}/capture", new { }, "cap-late");

        status.ShouldBe(HttpStatusCode.OK);
        cancelled.GetProperty("status").GetString().ShouldBe("canceled");
        late.ShouldBe(HttpStatusCode.BadRequest);
        lateBody.GetProperty("error").GetProperty("code").GetString().ShouldBe("payment_intent_unexpected_state");
    }

    [Theory]
    [InlineData("4000000000000002", "generic_decline")]
    [InlineData("4000000000009995", "insufficient_funds")]
    public async Task A_declining_card_is_a_402_card_error_with_its_decline_code(string number, string declineCode)
    {
        string token = await sim.TokeniseAsync(number);

        (HttpStatusCode status, JsonElement body, _) = await sim.PostAsync("/v1/payment_intents", SimHost.Authorisation(token), "auth");

        status.ShouldBe(HttpStatusCode.PaymentRequired);
        JsonElement error = body.GetProperty("error");
        error.GetProperty("type").GetString().ShouldBe("card_error");
        error.GetProperty("code").GetString().ShouldBe("card_declined");
        error.GetProperty("decline_code").GetString().ShouldBe(declineCode);
    }

    [Fact]
    public async Task A_number_that_is_not_a_test_card_is_refused_and_the_answer_does_not_echo_it()
    {
        (HttpStatusCode status, JsonElement body, _) = await sim.PostAsync("/v1/tokens", SimHost.Card("4111111111111111"), "t");

        status.ShouldBe(HttpStatusCode.PaymentRequired);
        body.GetProperty("error").GetProperty("code").GetString().ShouldBe("incorrect_number");
        body.ToString().ShouldNotContain("4111");
    }

    [Fact]
    public async Task Every_mutating_call_needs_an_idempotency_key()
    {
        string token = await sim.TokeniseAsync("4242424242424242");
        string id = await sim.AuthoriseAsync("4242424242424242");

        foreach ((string path, object? body) in new (string, object?)[]
        {
            ("/v1/tokens", SimHost.Card("4242424242424242")),
            ("/v1/payment_intents", SimHost.Authorisation(token)),
            ($"/v1/payment_intents/{id}/capture", new { }),
            ($"/v1/payment_intents/{id}/cancel", null),
            ("/v1/refunds", new { payment_intent = id }),
        })
        {
            (HttpStatusCode status, JsonElement error, _) = await sim.PostAsync(path, body, key: null);

            status.ShouldBe(HttpStatusCode.BadRequest, path);
            error.GetProperty("error").GetProperty("code").GetString().ShouldBe("idempotency_key_missing", path);
        }

        (await sim.IntentsAsync()).Count.ShouldBe(1, "refused calls change nothing");
    }

    [Fact]
    public async Task A_repeat_under_the_same_key_is_flagged_returns_the_same_body_and_captures_once()
    {
        string id = await sim.AuthoriseAsync("4242424242424242", 10_000);

        (HttpStatusCode first, JsonElement firstBody, HttpResponseMessage firstResponse) =
            await sim.PostAsync($"/v1/payment_intents/{id}/capture", new { amount_to_capture = 6_000 }, "cap");
        (HttpStatusCode second, JsonElement secondBody, HttpResponseMessage secondResponse) =
            await sim.PostAsync($"/v1/payment_intents/{id}/capture", new { amount_to_capture = 6_000 }, "cap");

        first.ShouldBe(HttpStatusCode.OK);
        second.ShouldBe(HttpStatusCode.OK);
        secondBody.ToString().ShouldBe(firstBody.ToString());
        firstResponse.Headers.Contains("Idempotent-Replayed").ShouldBeFalse();
        secondResponse.Headers.GetValues("Idempotent-Replayed").ShouldBe(["true"]);
        (await sim.IntentsAsync()).Single().GetProperty("amount_received").GetInt64().ShouldBe(6_000);
    }

    [Fact]
    public async Task The_same_key_for_a_different_request_is_a_400_and_the_first_result_stands()
    {
        string id = await sim.AuthoriseAsync("4242424242424242", 10_000);
        await sim.PostAsync($"/v1/payment_intents/{id}/capture", new { amount_to_capture = 6_000 }, "cap");

        (HttpStatusCode status, JsonElement body, _) =
            await sim.PostAsync($"/v1/payment_intents/{id}/capture", new { amount_to_capture = 9_000 }, "cap");

        status.ShouldBe(HttpStatusCode.BadRequest);
        body.GetProperty("error").GetProperty("type").GetString().ShouldBe("idempotency_error");
        (await sim.IntentsAsync()).Single().GetProperty("amount_received").GetInt64().ShouldBe(6_000);
    }

    [Fact]
    public async Task The_lookup_returns_what_the_key_did_including_a_decline()
    {
        string id = await sim.AuthoriseAsync("4242424242424242", 10_000);
        await sim.PostAsync($"/v1/payment_intents/{id}/capture", new { amount_to_capture = 4_000 }, "cap-key");
        string declined = await sim.TokeniseAsync("4000000000009995");
        await sim.PostAsync("/v1/payment_intents", SimHost.Authorisation(declined), "decline-key");

        (HttpStatusCode status, JsonElement capture) = await sim.GetAsync("/v1/operations?idempotency_key=cap-key");
        (_, JsonElement decline) = await sim.GetAsync("/v1/operations?idempotency_key=decline-key");

        status.ShouldBe(HttpStatusCode.OK);
        capture.GetProperty("object").GetString().ShouldBe("operation");
        capture.GetProperty("type").GetString().ShouldBe("capture");
        capture.GetProperty("http_status").GetInt32().ShouldBe(200);
        capture.GetProperty("result").GetProperty("amount_received").GetInt64().ShouldBe(4_000);
        decline.GetProperty("http_status").GetInt32().ShouldBe(402);
        decline.GetProperty("result").GetProperty("error").GetProperty("decline_code").GetString().ShouldBe("insufficient_funds");
    }

    [Fact]
    public async Task The_lookup_of_a_key_nobody_used_is_a_404_and_the_key_parameter_is_required()
    {
        (HttpStatusCode missing, JsonElement body) = await sim.GetAsync("/v1/operations?idempotency_key=never-used");
        (HttpStatusCode noKey, _) = await sim.GetAsync("/v1/operations");

        missing.ShouldBe(HttpStatusCode.NotFound);
        body.GetProperty("error").GetProperty("code").GetString().ShouldBe("resource_missing");
        noKey.ShouldBe(HttpStatusCode.BadRequest);
    }

    [Fact]
    public async Task The_card_number_is_in_no_log_line_of_a_full_flow()
    {
        string[] numbers = ["4242424242424242", "4000000000000002", "5555555555554444", "4111111111111111"];
        foreach (string number in numbers)
        {
            await sim.PostAsync("/v1/tokens", SimHost.Card(number), Guid.NewGuid().ToString());
        }

        await sim.AuthoriseAsync("4242424242424242");

        // The control: the capture sees entries at all, and sees the token requests it is about.
        sim.Logs.Entries.ShouldContain(static entry => entry.Contains("/v1/tokens", StringComparison.Ordinal));
        sim.Logs.Entries.Count.ShouldBeGreaterThan(10);
        foreach (string number in numbers)
        {
            sim.Logs.Entries.ShouldNotContain(entry => entry.Contains(number, StringComparison.Ordinal), $"{number} was logged");
        }
    }
}
