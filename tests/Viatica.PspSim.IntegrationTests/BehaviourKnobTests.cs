using System.Diagnostics;
using System.Net;
using System.Text.Json;

namespace Viatica.PspSim.IntegrationTests;

/// <summary>
/// The two timeout cards and each knob, turned through the admin API, change what a caller of the provider sees. The
/// timeouts are the point: a call that failed to answer must be told apart from a call that did nothing, by lookup.
/// </summary>
public sealed class BehaviourKnobTests : IAsyncLifetime
{
    private readonly SimHost sim = new();

    public async ValueTask InitializeAsync() => await sim.QuietAsync(hangMs: 300);

    public ValueTask DisposeAsync()
    {
        sim.Dispose();
        return ValueTask.CompletedTask;
    }

    [Fact]
    public async Task The_timeout_card_that_performs_does_the_work_then_answers_504_and_a_repeat_gets_the_recorded_answer()
    {
        string token = await sim.TokeniseAsync(Cards.TimeoutPerformedNumber);

        (HttpStatusCode first, JsonElement firstBody, _) = await sim.PostAsync("/v1/payment_intents", SimHost.Authorisation(token), "auth");
        (HttpStatusCode lookup, JsonElement found) = await sim.GetAsync("/v1/operations?idempotency_key=auth");
        (HttpStatusCode repeat, JsonElement repeatBody, HttpResponseMessage repeatResponse) =
            await sim.PostAsync("/v1/payment_intents", SimHost.Authorisation(token), "auth");

        first.ShouldBe(HttpStatusCode.GatewayTimeout);
        firstBody.GetProperty("error").GetProperty("code").GetString().ShouldBe("gateway_timeout");

        // The answer was lost, the work was not: the lookup finds the authorisation the 504 hid.
        lookup.ShouldBe(HttpStatusCode.OK);
        found.GetProperty("http_status").GetInt32().ShouldBe(200);
        string intent = found.GetProperty("result").GetProperty("id").GetString()!;

        repeat.ShouldBe(HttpStatusCode.OK);
        repeatBody.GetProperty("id").GetString().ShouldBe(intent);
        repeatResponse.Headers.GetValues("Idempotent-Replayed").ShouldBe(["true"]);
        (await sim.IntentsAsync()).Count.ShouldBe(1, "the timeout and its repeat made one authorisation");
    }

    [Fact]
    public async Task The_performing_card_times_out_on_every_kind_of_operation_and_each_is_found_by_its_key()
    {
        string token = await sim.TokeniseAsync(Cards.TimeoutPerformedNumber);
        await sim.PostAsync("/v1/payment_intents", SimHost.Authorisation(token, 10_000), "auth");
        string intent = (await sim.IntentsAsync()).Single().GetProperty("id").GetString()!;

        (HttpStatusCode capture, _, _) = await sim.PostAsync($"/v1/payment_intents/{intent}/capture", new { amount_to_capture = 7_000 }, "cap");
        (HttpStatusCode refund, _, _) = await sim.PostAsync("/v1/refunds", new { payment_intent = intent, amount = 2_000 }, "ref");

        capture.ShouldBe(HttpStatusCode.GatewayTimeout);
        refund.ShouldBe(HttpStatusCode.GatewayTimeout);
        (_, JsonElement cap) = await sim.GetAsync("/v1/operations?idempotency_key=cap");
        (_, JsonElement ref1) = await sim.GetAsync("/v1/operations?idempotency_key=ref");
        cap.GetProperty("result").GetProperty("amount_received").GetInt64().ShouldBe(7_000);
        ref1.GetProperty("result").GetProperty("amount").GetInt64().ShouldBe(2_000);

        JsonElement final = (await sim.IntentsAsync()).Single();
        final.GetProperty("amount_received").GetInt64().ShouldBe(7_000);
        final.GetProperty("amount_refunded").GetInt64().ShouldBe(2_000);
    }

    [Fact]
    public async Task The_timeout_card_that_does_not_perform_leaves_no_trace_and_a_repeat_under_the_same_key_does_the_work()
    {
        string token = await sim.TokeniseAsync(Cards.TimeoutNotPerformedNumber);

        (HttpStatusCode first, _, _) = await sim.PostAsync("/v1/payment_intents", SimHost.Authorisation(token), "auth");
        (HttpStatusCode lookup, _) = await sim.GetAsync("/v1/operations?idempotency_key=auth");
        int intentsAfterTimeout = (await sim.IntentsAsync()).Count;
        (HttpStatusCode repeat, JsonElement body, HttpResponseMessage response) =
            await sim.PostAsync("/v1/payment_intents", SimHost.Authorisation(token), "auth");

        first.ShouldBe(HttpStatusCode.GatewayTimeout);
        lookup.ShouldBe(HttpStatusCode.NotFound);
        intentsAfterTimeout.ShouldBe(0);
        repeat.ShouldBe(HttpStatusCode.OK);
        body.GetProperty("status").GetString().ShouldBe("requires_capture");
        response.Headers.Contains("Idempotent-Replayed").ShouldBeFalse("this was the first time the work was done");
        (await sim.IntentsAsync()).Count.ShouldBe(1);
    }

    [Fact]
    public async Task A_timed_out_call_hangs_for_the_configured_time()
    {
        string token = await sim.TokeniseAsync(Cards.TimeoutNotPerformedNumber);
        await sim.SetKnobsAsync(new { TimeoutHangMs = 600 });

        Stopwatch watch = Stopwatch.StartNew();
        await sim.PostAsync("/v1/payment_intents", SimHost.Authorisation(token), "auth");

        watch.Elapsed.ShouldBeGreaterThanOrEqualTo(TimeSpan.FromMilliseconds(550));
    }

    [Fact]
    public async Task An_outage_answers_503_to_everything_until_it_is_switched_off()
    {
        string token = await sim.TokeniseAsync("4242424242424242");

        await sim.SetKnobsAsync(new { Outage = true });
        (HttpStatusCode mutate, _, _) = await sim.PostAsync("/v1/payment_intents", SimHost.Authorisation(token), "auth");
        (HttpStatusCode lookup, _) = await sim.GetAsync("/v1/operations?idempotency_key=auth");
        await sim.SetKnobsAsync(new { Outage = false });
        (HttpStatusCode back, _, _) = await sim.PostAsync("/v1/payment_intents", SimHost.Authorisation(token), "auth");

        mutate.ShouldBe(HttpStatusCode.ServiceUnavailable);
        lookup.ShouldBe(HttpStatusCode.ServiceUnavailable);
        back.ShouldBe(HttpStatusCode.OK);
        (await sim.IntentsAsync()).Count.ShouldBe(1, "the call made during the outage did nothing");
    }

    [Fact]
    public async Task A_lookup_outage_fails_only_the_lookup()
    {
        string token = await sim.TokeniseAsync("4242424242424242");
        await sim.PostAsync("/v1/payment_intents", SimHost.Authorisation(token), "auth");

        await sim.SetKnobsAsync(new { LookupOutage = true });
        (HttpStatusCode lookup, _) = await sim.GetAsync("/v1/operations?idempotency_key=auth");
        (HttpStatusCode mutate, _, _) = await sim.PostAsync("/v1/payment_intents", SimHost.Authorisation(token), "auth");

        lookup.ShouldBe(HttpStatusCode.ServiceUnavailable);
        mutate.ShouldBe(HttpStatusCode.OK);
    }

    [Fact]
    public async Task An_error_rate_of_one_answers_500_and_does_nothing()
    {
        string token = await sim.TokeniseAsync("4242424242424242");
        await sim.SetKnobsAsync(new { ErrorRate = 1.0 });

        (HttpStatusCode status, _, _) = await sim.PostAsync("/v1/payment_intents", SimHost.Authorisation(token), "auth");
        await sim.SetKnobsAsync(new { ErrorRate = 0.0 });

        status.ShouldBe(HttpStatusCode.InternalServerError);
        (await sim.IntentsAsync()).ShouldBeEmpty();
        (HttpStatusCode lookup, _) = await sim.GetAsync("/v1/operations?idempotency_key=auth");
        lookup.ShouldBe(HttpStatusCode.NotFound);
    }

    [Fact]
    public async Task A_random_timeout_that_performed_is_in_the_ledger_and_one_that_did_not_is_not()
    {
        string token = await sim.TokeniseAsync("4242424242424242");

        await sim.SetKnobsAsync(new { TimeoutRate = 1.0, PerformedRate = 1.0 });
        (HttpStatusCode performed, _, _) = await sim.PostAsync("/v1/payment_intents", SimHost.Authorisation(token), "performed");
        await sim.SetKnobsAsync(new { PerformedRate = 0.0 });
        (HttpStatusCode notPerformed, _, _) = await sim.PostAsync("/v1/payment_intents", SimHost.Authorisation(token), "not-performed");

        performed.ShouldBe(HttpStatusCode.GatewayTimeout);
        notPerformed.ShouldBe(HttpStatusCode.GatewayTimeout);
        (await sim.GetAsync("/v1/operations?idempotency_key=performed")).Status.ShouldBe(HttpStatusCode.OK);
        (await sim.GetAsync("/v1/operations?idempotency_key=not-performed")).Status.ShouldBe(HttpStatusCode.NotFound);
    }

    [Fact]
    public async Task Latency_follows_the_knob()
    {
        await sim.SetKnobsAsync(new { LatencyMedianMs = 400, LatencySigma = 0 });

        Stopwatch watch = Stopwatch.StartNew();
        await sim.TokeniseAsync("4242424242424242");

        watch.Elapsed.ShouldBeGreaterThanOrEqualTo(TimeSpan.FromMilliseconds(380));
    }

    [Fact]
    public async Task Bad_knobs_are_refused_and_reset_puts_the_defaults_back()
    {
        using HttpResponseMessage bad = await sim.Client.PatchAsync(
            "/admin/knobs", new StringContent("""{"ErrorRate": 3}""", System.Text.Encoding.UTF8, "application/json"));
        await sim.SetKnobsAsync(new { Outage = true, ErrorRate = 0.5 });
        (await sim.Client.PostAsync("/admin/knobs/reset", content: null)).EnsureSuccessStatusCode();
        (_, JsonElement knobs) = await sim.GetAsync("/admin/knobs");

        bad.StatusCode.ShouldBe(HttpStatusCode.BadRequest);
        knobs.GetProperty("Knobs").GetProperty("Outage").GetBoolean().ShouldBeFalse();
        knobs.GetProperty("Knobs").GetProperty("ErrorRate").GetDouble().ShouldBe(0);
    }

    [Fact]
    public async Task The_admin_api_lists_the_test_cards()
    {
        (HttpStatusCode status, JsonElement cards) = await sim.GetAsync("/admin/cards");

        status.ShouldBe(HttpStatusCode.OK);
        cards.GetProperty("data").EnumerateArray().Select(static c => c.GetProperty("number").GetString())
            .ShouldContain(Cards.TimeoutPerformedNumber);
    }

    [Fact]
    public async Task Health_is_ready_without_any_dependency()
    {
        (await sim.GetAsync("/health/ready")).Status.ShouldBe(HttpStatusCode.OK);
        (await sim.GetAsync("/health/live")).Status.ShouldBe(HttpStatusCode.OK);
    }
}
