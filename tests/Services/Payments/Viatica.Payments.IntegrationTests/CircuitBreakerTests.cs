using Viatica.Contracts;
using Viatica.Payments.Domain;
using Viatica.Testing;

namespace Viatica.Payments.IntegrationTests;

/// <summary>
/// What an open circuit means for a payment. Nothing is sent while it is open, and what the request does about that
/// depends on what it asks for: an authorisation is declined at once, because nothing was held and the saga is waiting
/// with holds that expire; a capture, a void and a refund are tried again, because they cannot be declined
/// (<c>docs/services/payments.md</c>, "Unknown outcomes"). This host opens its breaker after a few calls and closes it
/// after three seconds, and its consumer is patient enough to outlast that.
/// </summary>
public sealed class CircuitBreakerTests(BreakerPayments payments) : IClassFixture<BreakerPayments>, IAsyncLifetime
{
    private readonly Scenario scenario = new(payments);

    public async ValueTask InitializeAsync() => await payments.Sim.QuietAsync();

    public ValueTask DisposeAsync() => ValueTask.CompletedTask;

    [Fact]
    public async Task With_the_circuit_open_an_authorisation_is_declined_at_once_without_a_call_and_a_capture_waits_and_happens_once()
    {
        PlacedPayment waiting = await scenario.AuthorizedAsync(100m);
        string captureKey = Scenario.KeyOf(waiting.TripId, OperationKind.Capture, waiting.PaymentId);
        string token = await scenario.TokenAsync();

        // The provider goes down. Each authorisation meets a 503 (nothing was done) until the breaker has seen enough.
        await payments.Sim.SetKnobsAsync(new { Outage = true });
        List<PaymentAuthorizationRequested> requests = [];
        for (int i = 0; i < 4; i++)
        {
            PaymentAuthorizationRequested request = Scenario.AuthorizationFor(token, 25m);
            requests.Add(request);
            await scenario.SendAsync(request);
            ProbedMessage declined = await scenario.NextAsync(nameof(PaymentDeclined), request.TripId);
            declined.DataString("reason").ShouldBe("ProviderUnavailable", "nothing was held, so the answer is a decline and not a wait");
        }

        // The breaker is open by now: the fourth request never left, and its decline did not wait for any timeout.
        string lastKey = Scenario.KeyOf(requests[^1].TripId, OperationKind.Authorize, requests[^1].PaymentId);
        payments.Host.Wire.Entries.Any(e => e.IdempotencyKey == lastKey).ShouldBeFalse("the circuit was open, so nothing was sent");
        payments.Host.Wire.Posts("/v1/payment_intents").Count(e => requests.Any(r => e.IdempotencyKey == Scenario.KeyOf(r.TripId, OperationKind.Authorize, r.PaymentId)))
            .ShouldBeLessThan(4, "the breaker spared the provider some of the calls");

        // A capture now is not declined. Its calls are held back by the open circuit and tried again.
        await scenario.SendAsync(new PaymentCaptureRequested(waiting.TripId, waiting.PaymentId, Scenario.Eur(100m)));
        await payments.Sim.SetKnobsAsync(new { Outage = false });
        (await payments.Sim.IntentsOfAsync(requests[^1].TripId)).ShouldBeEmpty("the declined authorisation left nothing at the provider");
        await scenario.NextAsync(nameof(PaymentCaptured), waiting.TripId);
        IReadOnlyList<ProbedMessage> events = await scenario.EventsOfAsync(waiting.TripId);

        Scenario.OfType(events, nameof(PaymentCaptured)).Count.ShouldBe(1);
        events.Count(static m => m.Type == nameof(PaymentOperationRefused)).ShouldBe(0);
        payments.Host.Wire.Posts("/capture").Count(e => e.IdempotencyKey == captureKey).ShouldBe(1, "while the circuit was open nothing reached the provider; once it closed, one call did");
        (await payments.Sim.IntentsOfAsync(waiting.TripId)).Single().GetProperty("amount_received").GetInt64().ShouldBe(10_000);
        (await scenario.PaymentAsync(waiting.PaymentId)).Status.ShouldBe(PaymentStatus.Captured);
    }
}
