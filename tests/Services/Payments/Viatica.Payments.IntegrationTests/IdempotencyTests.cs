using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using Viatica.Contracts;
using Viatica.Kernel;
using Viatica.Payments.Application.Provider;
using Viatica.Payments.Domain;
using Viatica.Payments.Infrastructure.Provider;
using Viatica.Testing;

namespace Viatica.Payments.IntegrationTests;

/// <summary>
/// Every request gets exactly one outcome per operation id, and the provider never moves money twice, however the
/// request arrives: again under the same event id (Kafka redelivers), again under a new one (Booking asks twice), or
/// after a crash between the provider's answer and our commit.
/// </summary>
public sealed class IdempotencyTests(DefaultPayments payments) : IClassFixture<DefaultPayments>, IAsyncLifetime
{
    private readonly Scenario scenario = new(payments);

    public async ValueTask InitializeAsync() => await payments.Sim.QuietAsync();

    public ValueTask DisposeAsync() => ValueTask.CompletedTask;

    private async Task<JsonElement> IntentOf(Guid tripId) => (await payments.Sim.IntentsOfAsync(tripId)).Single();

    [Fact]
    public async Task The_same_event_delivered_twice_is_applied_once()
    {
        PlacedPayment payment = await scenario.AuthorizedAsync(100m);
        var capture = new PaymentCaptureRequested(payment.TripId, payment.PaymentId, Scenario.Eur(100m));
        Guid eventId = Guid.CreateVersion7();

        await payments.RawBooking.SendAsync(capture, eventId);
        await payments.RawBooking.SendAsync(capture, eventId);
        IReadOnlyList<ProbedMessage> events = await scenario.EventsOfAsync(payment.TripId);

        Scenario.OfType(events, nameof(PaymentCaptured)).Count.ShouldBe(1);
        payments.Host.Wire.Posts("/capture").Count(e => e.IdempotencyKey == Scenario.KeyOf(payment.TripId, OperationKind.Capture, payment.PaymentId)).ShouldBe(1);
        (await IntentOf(payment.TripId)).GetProperty("amount_received").GetInt64().ShouldBe(10_000);
    }

    [Fact]
    public async Task The_same_request_under_new_event_ids_captures_once_and_answers_once()
    {
        PlacedPayment payment = await scenario.AuthorizedAsync(100m);
        var capture = new PaymentCaptureRequested(payment.TripId, payment.PaymentId, Scenario.Eur(60m));

        await scenario.SendAsync(capture, capture, capture);
        await scenario.SendAsync(capture);
        IReadOnlyList<ProbedMessage> events = await scenario.EventsOfAsync(payment.TripId);

        Scenario.OfType(events, nameof(PaymentCaptured)).Count.ShouldBe(1, "one outcome per operation id");
        events.Count(static m => m.Type != nameof(PaymentAuthorized)).ShouldBe(1, "and nothing else");
        string intent = await IntentIdOf(payment);
        payments.Host.Wire.Posts("/capture").Count(e => e.Path.Contains(intent, StringComparison.Ordinal)).ShouldBe(1, "the provider was asked once");
        (await IntentOf(payment.TripId)).GetProperty("amount_received").GetInt64().ShouldBe(6_000);
        (await scenario.PaymentAsync(payment.PaymentId)).CapturedAmount.ShouldBe(60m);
        (await scenario.OperationsAsync(payment.TripId)).Count(static o => o.Kind == OperationKind.Capture).ShouldBe(1);
    }

    [Fact]
    public async Task The_same_authorisation_asked_twice_holds_the_amount_once()
    {
        string token = await scenario.TokenAsync();
        PaymentAuthorizationRequested request = Scenario.AuthorizationFor(token, 75m);

        await scenario.SendAsync(request, request);
        await scenario.SendAsync(request);
        IReadOnlyList<ProbedMessage> events = await scenario.EventsOfAsync(request.TripId);

        Scenario.OfType(events, nameof(PaymentAuthorized)).Count.ShouldBe(1);
        events.Count(static m => m.Type is nameof(PaymentDeclined)).ShouldBe(0);
        (await payments.Sim.IntentsOfAsync(request.TripId)).Count.ShouldBe(1, "one hold on the card, not three");
    }

    [Fact]
    public async Task The_same_void_asked_three_times_releases_once()
    {
        PlacedPayment payment = await scenario.AuthorizedAsync(100m);
        var @void = new PaymentVoidRequested(payment.TripId, payment.PaymentId);

        await scenario.SendAsync(@void, @void, @void);
        IReadOnlyList<ProbedMessage> events = await scenario.EventsOfAsync(payment.TripId);

        Scenario.OfType(events, nameof(PaymentVoided)).Count.ShouldBe(1);
        events.Count(static m => m.Type == nameof(PaymentOperationRefused)).ShouldBe(0, "a repeat is not a refusal either");
        payments.Host.Wire.Count(HttpMethod.Post, "/cancel").ShouldBeGreaterThanOrEqualTo(1);
        (await IntentOf(payment.TripId)).GetProperty("status").GetString().ShouldBe("canceled");
    }

    [Fact]
    public async Task The_same_refund_asked_twice_refunds_once_and_a_different_refund_for_the_rest_is_a_second_refund()
    {
        PlacedPayment payment = await scenario.CapturedAsync(100m);
        Guid refund = Guid.CreateVersion7();
        Guid other = Guid.CreateVersion7();
        var request = new PaymentRefundRequested(payment.TripId, payment.PaymentId, refund, Scenario.Eur(40m));

        await scenario.SendAsync(request, request);
        await scenario.SendAsync(new PaymentRefundRequested(payment.TripId, payment.PaymentId, other, Scenario.Eur(60m)), request);
        IReadOnlyList<ProbedMessage> events = await scenario.EventsOfAsync(payment.TripId);

        IReadOnlyList<ProbedMessage> refunded = Scenario.OfType(events, nameof(PaymentRefunded));
        refunded.Select(static m => m.DataString("refundId")).Order().ShouldBe([refund.ToString(), other.ToString()], ignoreOrder: true);
        events.Count(static m => m.Type == nameof(PaymentOperationRefused)).ShouldBe(0);
        string intent = await IntentIdOf(payment);
        (await payments.Sim.RefundsAsync()).Where(r => r.GetProperty("payment_intent").GetString() == intent)
            .Sum(static r => r.GetProperty("amount").GetInt64()).ShouldBe(10_000, "40 + 60, never 40 + 40 + 60");
        (await scenario.PaymentAsync(payment.PaymentId)).RefundedAmount.ShouldBe(100m);
    }

    [Fact]
    public async Task A_refused_request_asked_again_is_refused_once()
    {
        PlacedPayment payment = await scenario.AuthorizedAsync(100m);
        var tooMuch = new PaymentCaptureRequested(payment.TripId, payment.PaymentId, Scenario.Eur(500m));

        await scenario.SendAsync(tooMuch, tooMuch, tooMuch);
        IReadOnlyList<ProbedMessage> events = await scenario.EventsOfAsync(payment.TripId);

        Scenario.OfType(events, nameof(PaymentOperationRefused)).Count.ShouldBe(1);
    }

    [Fact]
    public async Task A_crash_after_the_provider_captured_and_before_the_commit_ends_in_one_capture_and_one_answer()
    {
        PlacedPayment payment = await scenario.AuthorizedAsync(100m);
        string key = Scenario.KeyOf(payment.TripId, OperationKind.Capture, payment.PaymentId);
        payments.Host.Faults.CrashAfterNextCapture();

        await scenario.SendAsync(new PaymentCaptureRequested(payment.TripId, payment.PaymentId, Scenario.Eur(100m)));
        await scenario.NextAsync(nameof(PaymentCaptured), payment.TripId);
        IReadOnlyList<ProbedMessage> events = await scenario.EventsOfAsync(payment.TripId);

        Scenario.OfType(events, nameof(PaymentCaptured)).Count.ShouldBe(1);
        // The first attempt reached the provider and was lost with the crash; the redelivery sent the same key again
        // and got the answer the provider had already recorded.
        payments.Host.Wire.Posts("/capture").Count(e => e.IdempotencyKey == key).ShouldBe(2);
        (await IntentOf(payment.TripId)).GetProperty("amount_received").GetInt64().ShouldBe(10_000, "captured once, not twice");
        (await scenario.PaymentAsync(payment.PaymentId)).CapturedAmount.ShouldBe(100m);
        (await scenario.OperationsAsync(payment.TripId)).Count(static o => o.Kind == OperationKind.Capture).ShouldBe(1);
    }

    [Fact]
    public async Task The_provider_answering_the_same_capture_twice_is_one_capture_and_a_second_key_cannot_capture_again()
    {
        PlacedPayment payment = await scenario.AuthorizedAsync(100m);
        PspGateway provider = payments.Host.Services.GetRequiredService<PspGateway>();
        string intent = (await IntentOf(payment.TripId)).GetProperty("id").GetString()!;

        ProviderResult first = await provider.CaptureAsync(intent, Money.Eur(100m), "dup-key", CancellationToken.None);
        ProviderResult again = await provider.CaptureAsync(intent, Money.Eur(100m), "dup-key", CancellationToken.None);
        ProviderResult otherKey = await provider.CaptureAsync(intent, Money.Eur(100m), "another-key", CancellationToken.None);

        again.Case<ProviderSucceeded>().Reference.ShouldBe(first.Case<ProviderSucceeded>().Reference);
        otherKey.Case<ProviderRefused>().Code.ShouldBe("payment_intent_unexpected_state");
        (await IntentOf(payment.TripId)).GetProperty("amount_received").GetInt64().ShouldBe(10_000);
    }

    private async Task<string> IntentIdOf(PlacedPayment payment) => (await IntentOf(payment.TripId)).GetProperty("id").GetString()!;
}
