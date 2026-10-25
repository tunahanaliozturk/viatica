using System.Text.Json;
using Viatica.Contracts;
using Viatica.Payments.Domain;
using Viatica.Testing;

namespace Viatica.Payments.IntegrationTests;

/// <summary>
/// PAY-3: a provider timeout is resolved by asking the provider what it recorded under the operation's idempotency
/// key, never by retrying blindly. The host here gives the provider one second; the simulator hangs for six, and its
/// two timeout cards decide whether the work was done before it went quiet.
/// </summary>
public sealed class TimeoutTests(DefaultPayments payments) : IClassFixture<DefaultPayments>, IAsyncLifetime
{
    private readonly Scenario scenario = new(payments);

    public async ValueTask InitializeAsync() => await payments.Sim.QuietAsync();

    public ValueTask DisposeAsync() => ValueTask.CompletedTask;

    private IEnumerable<WireEntry> OnWire(string key) =>
        payments.Host.Wire.Entries.Where(e => e.IdempotencyKey == key || e.Query.Contains(Uri.EscapeDataString(key), StringComparison.Ordinal));

    private int Posts(string key) => OnWire(key).Count(static e => e.Method == HttpMethod.Post);

    private int Lookups(string key) => OnWire(key).Count(static e => e.Method == HttpMethod.Get);

    // Between two copies of a call there is a lookup. Two calls in a row would be a blind retry, even if it worked out.
    private void NoCallIsRepeatedBeforeItsDoubtIsSettled(string key)
    {
        string[] methods = [.. OnWire(key).Select(static e => e.Method.Method)];
        methods.ShouldNotBeEmpty();
        for (int i = 1; i < methods.Length; i++)
        {
            (methods[i - 1] == "POST" && methods[i] == "POST").ShouldBeFalse($"two calls in a row on the wire: {string.Join(", ", methods)}");
        }
    }

    private async Task<OperationRecord> RecordOf(Guid tripId, OperationKind kind) =>
        (await scenario.OperationsAsync(tripId)).Single(o => o.Kind == kind);

    private async Task<JsonElement> IntentOf(Guid tripId) => (await payments.Sim.IntentsOfAsync(tripId)).Single();

    // The next four tests are the same story for each operation: the provider did the work, the answer was lost, and the
    // service must end with the one right outcome without asking the provider to do it again.

    [Fact]
    public async Task An_authorisation_that_timed_out_and_happened_ends_in_one_PaymentAuthorized_found_by_lookup()
    {
        string token = await scenario.TokenAsync(Cards.TimesOutButHappens);
        PaymentAuthorizationRequested request = Scenario.AuthorizationFor(token, 120m);
        string key = Scenario.KeyOf(request.TripId, OperationKind.Authorize, request.PaymentId);

        await scenario.SendAsync(request);
        await scenario.NextAsync(nameof(PaymentAuthorized), request.TripId);
        IReadOnlyList<ProbedMessage> events = await scenario.EventsOfAsync(request.TripId);
        scenario.HandlerFailures().Count.ShouldBe(0, "a timeout that is settled by the lookup is not a failure to retry");

        events.Select(static m => m.Type).ShouldBe([nameof(PaymentAuthorized)]);
        Posts(key).ShouldBe(1, "the call that happened was not made again");
        Lookups(key).ShouldBe(1);
        (await payments.Sim.IntentsOfAsync(request.TripId)).Count.ShouldBe(1);
        (await RecordOf(request.TripId, OperationKind.Authorize)).ResolvedByLookup.ShouldBeTrue();
        (await scenario.PaymentAsync(request.PaymentId)).Status.ShouldBe(PaymentStatus.Authorized);
    }

    [Fact]
    public async Task A_capture_that_timed_out_and_happened_ends_in_one_PaymentCaptured_found_by_lookup()
    {
        PlacedPayment payment = await scenario.AuthorizedAsync(100m, Cards.TimesOutButHappens);
        string key = Scenario.KeyOf(payment.TripId, OperationKind.Capture, payment.PaymentId);

        await scenario.SendAsync(new PaymentCaptureRequested(payment.TripId, payment.PaymentId, Scenario.Eur(80m)));
        await scenario.NextAsync(nameof(PaymentCaptured), payment.TripId);
        IReadOnlyList<ProbedMessage> events = await scenario.EventsOfAsync(payment.TripId);
        scenario.HandlerFailures().Count.ShouldBe(0, "a timeout that is settled by the lookup is not a failure to retry");

        Scenario.OfType(events, nameof(PaymentCaptured)).Count.ShouldBe(1);
        events.Count(static m => m.Type == nameof(PaymentOperationRefused)).ShouldBe(0);
        Posts(key).ShouldBe(1);
        Lookups(key).ShouldBe(1);
        (await IntentOf(payment.TripId)).GetProperty("amount_received").GetInt64().ShouldBe(8_000, "captured once");
        (await RecordOf(payment.TripId, OperationKind.Capture)).ResolvedByLookup.ShouldBeTrue();
        (await scenario.PaymentAsync(payment.PaymentId)).CapturedAmount.ShouldBe(80m);
    }

    [Fact]
    public async Task A_void_that_timed_out_and_happened_ends_in_one_PaymentVoided_found_by_lookup()
    {
        PlacedPayment payment = await scenario.AuthorizedAsync(100m, Cards.TimesOutButHappens);
        string key = Scenario.KeyOf(payment.TripId, OperationKind.Void, payment.PaymentId);

        await scenario.SendAsync(new PaymentVoidRequested(payment.TripId, payment.PaymentId));
        await scenario.NextAsync(nameof(PaymentVoided), payment.TripId);
        IReadOnlyList<ProbedMessage> events = await scenario.EventsOfAsync(payment.TripId);
        scenario.HandlerFailures().Count.ShouldBe(0, "a timeout that is settled by the lookup is not a failure to retry");

        Scenario.OfType(events, nameof(PaymentVoided)).Count.ShouldBe(1);
        Posts(key).ShouldBe(1);
        Lookups(key).ShouldBe(1);
        (await IntentOf(payment.TripId)).GetProperty("status").GetString().ShouldBe("canceled");
        (await scenario.PaymentAsync(payment.PaymentId)).Status.ShouldBe(PaymentStatus.Voided);
    }

    [Fact]
    public async Task A_refund_that_timed_out_and_happened_ends_in_one_PaymentRefunded_found_by_lookup()
    {
        PlacedPayment payment = await scenario.AuthorizedAsync(100m, Cards.TimesOutButHappens);
        await scenario.SendAsync(new PaymentCaptureRequested(payment.TripId, payment.PaymentId, Scenario.Eur(100m)));
        await scenario.NextAsync(nameof(PaymentCaptured), payment.TripId);
        // The capture timed out and was found already; that is not the refund. The refund's own calls are counted by its key.
        Guid refund = Guid.CreateVersion7();
        string key = Scenario.KeyOf(payment.TripId, OperationKind.Refund, refund);

        await scenario.SendAsync(new PaymentRefundRequested(payment.TripId, payment.PaymentId, refund, Scenario.Eur(35m)));
        await scenario.NextForOperationAsync(nameof(PaymentRefunded), payment.TripId, refund, "refundId");
        IReadOnlyList<ProbedMessage> events = await scenario.EventsOfAsync(payment.TripId);
        scenario.HandlerFailures().Count.ShouldBe(0, "a timeout that is settled by the lookup is not a failure to retry");

        Scenario.OfType(events, nameof(PaymentRefunded)).Count.ShouldBe(1);
        Posts(key).ShouldBe(1);
        Lookups(key).ShouldBe(1);
        (await IntentOf(payment.TripId)).GetProperty("amount_refunded").GetInt64().ShouldBe(3_500, "refunded once");
    }

    [Fact]
    public async Task A_timeout_that_did_not_happen_is_sent_again_once_under_the_same_key_and_ends_in_one_outcome()
    {
        string token = await scenario.TokenAsync(Cards.TimesOutAndDoesNotHappen);
        PaymentAuthorizationRequested request = Scenario.AuthorizationFor(token, 90m);
        string key = Scenario.KeyOf(request.TripId, OperationKind.Authorize, request.PaymentId);

        await scenario.SendAsync(request);
        await scenario.NextAsync(nameof(PaymentAuthorized), request.TripId);
        IReadOnlyList<ProbedMessage> events = await scenario.EventsOfAsync(request.TripId);
        scenario.HandlerFailures().Count.ShouldBe(0, "a timeout that is settled by the lookup is not a failure to retry");

        events.Select(static m => m.Type).ShouldBe([nameof(PaymentAuthorized)]);
        // POST (timed out, did nothing), GET (nothing recorded), POST (same key, does the work): in that order.
        OnWire(key).Select(static e => e.Method.Method).ShouldBe(["POST", "GET", "POST"]);
        OnWire(key).Where(static e => e.Method == HttpMethod.Post).Select(static e => e.IdempotencyKey).Distinct().Count().ShouldBe(1);
        (await payments.Sim.IntentsOfAsync(request.TripId)).Count.ShouldBe(1);
        (await RecordOf(request.TripId, OperationKind.Authorize)).ResolvedByLookup.ShouldBeFalse();
    }

    [Fact]
    public async Task A_capture_that_timed_out_without_happening_is_sent_again_and_captured_once()
    {
        PlacedPayment payment = await scenario.AuthorizedAsync(100m, Cards.TimesOutAndDoesNotHappen);
        string key = Scenario.KeyOf(payment.TripId, OperationKind.Capture, payment.PaymentId);

        await scenario.SendAsync(new PaymentCaptureRequested(payment.TripId, payment.PaymentId, Scenario.Eur(100m)));
        await scenario.NextAsync(nameof(PaymentCaptured), payment.TripId);
        IReadOnlyList<ProbedMessage> events = await scenario.EventsOfAsync(payment.TripId);
        scenario.HandlerFailures().Count.ShouldBe(0, "a timeout that is settled by the lookup is not a failure to retry");

        Scenario.OfType(events, nameof(PaymentCaptured)).Count.ShouldBe(1);
        OnWire(key).Select(static e => e.Method.Method).ShouldBe(["POST", "GET", "POST"]);
        (await IntentOf(payment.TripId)).GetProperty("amount_received").GetInt64().ShouldBe(10_000);
    }

    [Fact]
    public async Task A_lookup_that_cannot_be_answered_decides_nothing_while_the_provider_may_have_captured_and_the_request_is_tried_again()
    {
        PlacedPayment payment = await scenario.AuthorizedAsync(100m);
        string key = Scenario.KeyOf(payment.TripId, OperationKind.Capture, payment.PaymentId);

        // Every call to the provider now does its work and loses the answer, and the lookup is down too: the service
        // cannot know, from here, whether anything was captured. (Captured it was, and every retry will say the same.)
        await payments.Sim.SetKnobsAsync(new { TimeoutRate = 1.0, PerformedRate = 1.0, LookupOutage = true });
        await scenario.SendAsync(new PaymentCaptureRequested(payment.TripId, payment.PaymentId, Scenario.Eur(100m)));
        await Eventually.MatchesAsync(() => Task.FromResult(Lookups(key)), lookups => lookups >= 3, TimeSpan.FromSeconds(30));

        // While the doubt stands, nothing is decided: no event, no record, no state change. The consumer keeps trying.
        await scenario.NothingPublishedAsync(payment.TripId, TimeSpan.FromSeconds(2), nameof(PaymentAuthorized));
        NoCallIsRepeatedBeforeItsDoubtIsSettled(key);
        (await scenario.OperationsAsync(payment.TripId)).ShouldNotContain(static o => o.Kind == OperationKind.Capture);
        (await scenario.PaymentAsync(payment.PaymentId)).Status.ShouldBe(PaymentStatus.Authorized);
        payments.Host.Logs.Entries.Count(static e => e.Contains("could not be resolved", StringComparison.Ordinal)).ShouldBeGreaterThanOrEqualTo(2);

        // The provider recovers. The next try, under the same key, settles it.
        await payments.Sim.SetKnobsAsync(new { TimeoutRate = 0.0, LookupOutage = false });
        await scenario.NextAsync(nameof(PaymentCaptured), payment.TripId);
        IReadOnlyList<ProbedMessage> events = await scenario.EventsOfAsync(payment.TripId);

        Scenario.OfType(events, nameof(PaymentCaptured)).Count.ShouldBe(1);
        (await IntentOf(payment.TripId)).GetProperty("amount_received").GetInt64().ShouldBe(10_000, "captured once, however often it was asked");
        (await scenario.PaymentAsync(payment.PaymentId)).Status.ShouldBe(PaymentStatus.Captured);
    }

    [Fact]
    public async Task A_lookup_that_cannot_be_answered_when_nothing_happened_is_not_read_as_a_failure_either()
    {
        PlacedPayment payment = await scenario.AuthorizedAsync(100m);
        string key = Scenario.KeyOf(payment.TripId, OperationKind.Capture, payment.PaymentId);

        // The provider hangs without doing anything, and the lookup is down. "Nothing happened" would be right, and
        // the service must still not say so: it cannot know.
        await payments.Sim.SetKnobsAsync(new { TimeoutRate = 1.0, PerformedRate = 0.0, LookupOutage = true });
        await scenario.SendAsync(new PaymentCaptureRequested(payment.TripId, payment.PaymentId, Scenario.Eur(100m)));
        await Eventually.MatchesAsync(() => Task.FromResult(Lookups(key)), lookups => lookups >= 3, TimeSpan.FromSeconds(30));

        await scenario.NothingPublishedAsync(payment.TripId, TimeSpan.FromSeconds(2), nameof(PaymentAuthorized));
        NoCallIsRepeatedBeforeItsDoubtIsSettled(key);
        (await IntentOf(payment.TripId)).GetProperty("amount_received").GetInt64().ShouldBe(0);

        await payments.Sim.SetKnobsAsync(new { TimeoutRate = 0.0, LookupOutage = false });
        await scenario.NextAsync(nameof(PaymentCaptured), payment.TripId);

        (await IntentOf(payment.TripId)).GetProperty("amount_received").GetInt64().ShouldBe(10_000);
    }

    [Fact]
    public async Task A_provider_that_answers_500_is_not_a_decline_it_is_asked_again_under_the_same_key_until_it_answers()
    {
        string token = await scenario.TokenAsync();
        PaymentAuthorizationRequested request = Scenario.AuthorizationFor(token, 60m);
        string key = Scenario.KeyOf(request.TripId, OperationKind.Authorize, request.PaymentId);

        await payments.Sim.SetKnobsAsync(new { ErrorRate = 1.0 });
        await scenario.SendAsync(request);
        await Eventually.MatchesAsync(() => Task.FromResult(Posts(key)), posts => posts >= 4, TimeSpan.FromSeconds(30));

        await scenario.NothingPublishedAsync(request.TripId, TimeSpan.FromSeconds(2));
        (await payments.Sim.IntentsOfAsync(request.TripId)).ShouldBeEmpty();

        await payments.Sim.SetKnobsAsync(new { ErrorRate = 0.0 });
        await scenario.NextAsync(nameof(PaymentAuthorized), request.TripId);
        IReadOnlyList<ProbedMessage> events = await scenario.EventsOfAsync(request.TripId);

        events.Select(static m => m.Type).ShouldBe([nameof(PaymentAuthorized)]);
        OnWire(key).Where(static e => e.Method == HttpMethod.Post).Select(static e => e.IdempotencyKey).Distinct().ShouldBe([key]);
        (await payments.Sim.IntentsOfAsync(request.TripId)).Count.ShouldBe(1);
    }
}
