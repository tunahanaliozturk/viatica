using System.Net;
using System.Net.Http.Headers;
using System.Text.Json;
using Viatica.Contracts;
using Viatica.Payments.Domain;
using Viatica.Testing;

namespace Viatica.Payments.IntegrationTests;

/// <summary>
/// PAY-2: authorise, capture, void and refund, driven by requests on booking.v1 and answered on payments.v1, against the
/// real simulator. Each test also reads the simulator back, because what the provider really holds is the thing the
/// money rules protect.
/// </summary>
public sealed class OperationTests(DefaultPayments payments) : IClassFixture<DefaultPayments>, IAsyncLifetime
{
    private readonly Scenario scenario = new(payments);

    public async ValueTask InitializeAsync() => await payments.Sim.QuietAsync();

    public ValueTask DisposeAsync() => ValueTask.CompletedTask;

    private static decimal Amount(JsonElement price) =>
        decimal.Parse(price.GetProperty("amount").GetString()!, System.Globalization.CultureInfo.InvariantCulture);

    private async Task<JsonElement> IntentOf(Guid tripId) => (await payments.Sim.IntentsOfAsync(tripId)).Single();

    [Fact]
    public async Task An_authorisation_holds_the_amount_at_the_provider_and_is_answered_with_PaymentAuthorized()
    {
        string token = await scenario.TokenAsync();
        PaymentAuthorizationRequested request = Scenario.AuthorizationFor(token, 312.50m);

        await scenario.SendAsync(request);
        ProbedMessage authorized = await scenario.NextAsync(nameof(PaymentAuthorized), request.TripId);

        authorized.Key.ShouldBe(request.TripId.ToString("N"), "keyed by trip, like every payment event");
        authorized.DataString("paymentId").ShouldBe(request.PaymentId.ToString());
        Amount(authorized.Data.GetProperty("amount")).ShouldBe(312.50m);

        JsonElement intent = await IntentOf(request.TripId);
        intent.GetProperty("status").GetString().ShouldBe("requires_capture");
        intent.GetProperty("amount").GetInt64().ShouldBe(31_250);
        intent.GetProperty("currency").GetString().ShouldBe("eur");

        Payment payment = await scenario.PaymentAsync(request.PaymentId);
        payment.Status.ShouldBe(PaymentStatus.Authorized);
        payment.Authorized.ShouldBe(Kernel.Money.Eur(312.50m));
        payment.ProviderIntentId.ShouldBe(intent.GetProperty("id").GetString());
        payment.PaymentMethodToken.ShouldBe(token);
    }

    [Fact]
    public async Task A_full_capture_takes_the_authorised_amount()
    {
        PlacedPayment payment = await scenario.AuthorizedAsync(200m);

        await scenario.SendAsync(new PaymentCaptureRequested(payment.TripId, payment.PaymentId, Scenario.Eur(200m)));
        ProbedMessage captured = await scenario.NextAsync(nameof(PaymentCaptured), payment.TripId);

        Amount(captured.Data.GetProperty("amount")).ShouldBe(200m);
        JsonElement intent = await IntentOf(payment.TripId);
        intent.GetProperty("status").GetString().ShouldBe("succeeded");
        intent.GetProperty("amount_received").GetInt64().ShouldBe(20_000);
        (await scenario.PaymentAsync(payment.PaymentId)).Status.ShouldBe(PaymentStatus.Captured);
    }

    [Fact]
    public async Task A_partial_capture_takes_less_and_releases_the_rest()
    {
        PlacedPayment payment = await scenario.AuthorizedAsync(200m);

        await scenario.SendAsync(new PaymentCaptureRequested(payment.TripId, payment.PaymentId, Scenario.Eur(140.25m)));
        ProbedMessage captured = await scenario.NextAsync(nameof(PaymentCaptured), payment.TripId);

        Amount(captured.Data.GetProperty("amount")).ShouldBe(140.25m);
        JsonElement intent = await IntentOf(payment.TripId);
        intent.GetProperty("amount_received").GetInt64().ShouldBe(14_025);
        intent.GetProperty("amount_capturable").GetInt64().ShouldBe(0);
        (await scenario.PaymentAsync(payment.PaymentId)).CapturedAmount.ShouldBe(140.25m);
    }

    [Fact]
    public async Task A_capture_above_the_authorisation_is_refused_by_us_and_never_reaches_the_provider()
    {
        PlacedPayment payment = await scenario.AuthorizedAsync(100m);
        int capturesBefore = payments.Host.Wire.Count(HttpMethod.Post, "/capture");

        Guid operation = payment.PaymentId;
        await scenario.SendAsync(new PaymentCaptureRequested(payment.TripId, payment.PaymentId, Scenario.Eur(100.01m)));
        ProbedMessage refused = await scenario.NextForOperationAsync(nameof(PaymentOperationRefused), payment.TripId, operation);

        refused.DataString("reason").ShouldBe("ExceedsAuthorization");
        refused.DataString("operation").ShouldBe("Capture");
        refused.DataString("paymentId").ShouldBe(payment.PaymentId.ToString());
        payments.Host.Wire.Count(HttpMethod.Post, "/capture").ShouldBe(capturesBefore, "a rule refused it, so the provider was not asked");
        (await IntentOf(payment.TripId)).GetProperty("status").GetString().ShouldBe("requires_capture");
        (await scenario.PaymentAsync(payment.PaymentId)).Status.ShouldBe(PaymentStatus.Authorized);
    }

    [Fact]
    public async Task A_void_releases_the_hold_and_a_capture_after_it_is_refused()
    {
        PlacedPayment payment = await scenario.AuthorizedAsync(100m);

        await scenario.SendAsync(new PaymentVoidRequested(payment.TripId, payment.PaymentId));
        await scenario.NextAsync(nameof(PaymentVoided), payment.TripId);
        int capturesBefore = payments.Host.Wire.Count(HttpMethod.Post, "/capture");
        await scenario.SendAsync(new PaymentCaptureRequested(payment.TripId, payment.PaymentId, Scenario.Eur(100m)));
        ProbedMessage refused = await scenario.NextAsync(nameof(PaymentOperationRefused), payment.TripId);

        refused.DataString("reason").ShouldBe("PaymentVoided");
        (await IntentOf(payment.TripId)).GetProperty("status").GetString().ShouldBe("canceled");
        payments.Host.Wire.Count(HttpMethod.Post, "/capture").ShouldBe(capturesBefore);
        (await scenario.PaymentAsync(payment.PaymentId)).Status.ShouldBe(PaymentStatus.Voided);
    }

    [Fact]
    public async Task A_captured_payment_cannot_be_voided_and_the_provider_is_not_asked()
    {
        PlacedPayment payment = await scenario.CapturedAsync(100m);
        int cancelsBefore = payments.Host.Wire.Count(HttpMethod.Post, "/cancel");

        await scenario.SendAsync(new PaymentVoidRequested(payment.TripId, payment.PaymentId));
        ProbedMessage refused = await scenario.NextAsync(nameof(PaymentOperationRefused), payment.TripId);

        refused.DataString("operation").ShouldBe("Void");
        refused.DataString("reason").ShouldBe("AlreadyCaptured");
        payments.Host.Wire.Count(HttpMethod.Post, "/cancel").ShouldBe(cancelsBefore);
        (await IntentOf(payment.TripId)).GetProperty("status").GetString().ShouldBe("succeeded");
    }

    [Fact]
    public async Task Refunds_in_parts_add_up_to_the_capture_and_one_more_cent_is_refused_without_a_provider_call()
    {
        PlacedPayment payment = await scenario.CapturedAsync(authorized: 100m, captured: 80m);
        Guid first = Guid.CreateVersion7();
        Guid second = Guid.CreateVersion7();
        Guid third = Guid.CreateVersion7();

        await scenario.SendAsync(new PaymentRefundRequested(payment.TripId, payment.PaymentId, first, Scenario.Eur(30m)));
        ProbedMessage firstDone = await scenario.NextForOperationAsync(nameof(PaymentRefunded), payment.TripId, first, "refundId");
        await scenario.SendAsync(new PaymentRefundRequested(payment.TripId, payment.PaymentId, second, Scenario.Eur(50m)));
        await scenario.NextForOperationAsync(nameof(PaymentRefunded), payment.TripId, second, "refundId");
        int refundsBefore = payments.Host.Wire.Count(HttpMethod.Post, "/v1/refunds");
        await scenario.SendAsync(new PaymentRefundRequested(payment.TripId, payment.PaymentId, third, Scenario.Eur(0.01m)));
        ProbedMessage refused = await scenario.NextForOperationAsync(nameof(PaymentOperationRefused), payment.TripId, third);

        Amount(firstDone.Data.GetProperty("amount")).ShouldBe(30m);
        refused.DataString("reason").ShouldBe("ExceedsCapture");
        refused.DataString("operation").ShouldBe("Refund");
        payments.Host.Wire.Count(HttpMethod.Post, "/v1/refunds").ShouldBe(refundsBefore);
        (await IntentOf(payment.TripId)).GetProperty("amount_refunded").GetInt64().ShouldBe(8_000);
        (await scenario.PaymentAsync(payment.PaymentId)).RefundedAmount.ShouldBe(80m);
    }

    [Fact]
    public async Task A_refund_before_any_capture_is_refused()
    {
        PlacedPayment payment = await scenario.AuthorizedAsync(100m);
        Guid refund = Guid.CreateVersion7();

        await scenario.SendAsync(new PaymentRefundRequested(payment.TripId, payment.PaymentId, refund, Scenario.Eur(10m)));
        ProbedMessage refused = await scenario.NextForOperationAsync(nameof(PaymentOperationRefused), payment.TripId, refund);

        refused.DataString("reason").ShouldBe("NotCaptured");
    }

    [Theory]
    [InlineData(Cards.Declined, "generic_decline")]
    [InlineData(Cards.InsufficientFunds, "insufficient_funds")]
    public async Task A_declining_card_is_answered_with_PaymentDeclined_and_the_provider_code_and_nothing_can_follow(string card, string reason)
    {
        string token = await scenario.TokenAsync(card);
        PaymentAuthorizationRequested request = Scenario.AuthorizationFor(token, 100m);

        await scenario.SendAsync(request);
        ProbedMessage declined = await scenario.NextAsync(nameof(PaymentDeclined), request.TripId);
        await scenario.SendAsync(new PaymentCaptureRequested(request.TripId, request.PaymentId, Scenario.Eur(100m)));
        ProbedMessage refused = await scenario.NextAsync(nameof(PaymentOperationRefused), request.TripId);

        declined.DataString("reason").ShouldBe(reason);
        declined.DataString("paymentId").ShouldBe(request.PaymentId.ToString());
        refused.DataString("reason").ShouldBe("NotAuthorized");
        (await payments.Sim.IntentsOfAsync(request.TripId)).ShouldBeEmpty("a declined authorisation leaves nothing at the provider");
        Payment stored = await scenario.PaymentAsync(request.PaymentId);
        stored.Status.ShouldBe(PaymentStatus.Declined);
        stored.DeclineReason.ShouldBe(reason);
    }

    [Fact]
    public async Task A_token_this_service_never_issued_is_a_decline_and_the_provider_is_not_asked()
    {
        int authorisationsBefore = payments.Host.Wire.Count(HttpMethod.Post, "/v1/payment_intents");
        PaymentAuthorizationRequested request = Scenario.AuthorizationFor("pm_nobody", 50m);

        await scenario.SendAsync(request);
        ProbedMessage declined = await scenario.NextAsync(nameof(PaymentDeclined), request.TripId);

        declined.DataString("reason").ShouldBe("PaymentMethodInvalid");
        payments.Host.Wire.Count(HttpMethod.Post, "/v1/payment_intents").ShouldBe(authorisationsBefore);
    }

    [Fact]
    public async Task Requests_about_a_payment_this_service_has_no_record_of_are_refused_not_lost()
    {
        Guid trip = Guid.CreateVersion7();
        Guid unknown = Guid.CreateVersion7();
        Guid refund = Guid.CreateVersion7();

        await scenario.SendAsync(
            new PaymentCaptureRequested(trip, unknown, Scenario.Eur(10m)),
            new PaymentVoidRequested(trip, unknown),
            new PaymentRefundRequested(trip, unknown, refund, Scenario.Eur(10m)));
        IReadOnlyList<ProbedMessage> events = await scenario.EventsOfAsync(trip);

        events.Select(static m => (m.Type, m.DataString("operation"), m.DataString("reason"))).ShouldBe(
        [
            (nameof(PaymentOperationRefused), "Capture", "PaymentNotFound"),
            (nameof(PaymentOperationRefused), "Void", "PaymentNotFound"),
            (nameof(PaymentOperationRefused), "Refund", "PaymentNotFound"),
        ]);
    }

    [Fact]
    public async Task One_trip_has_one_payment_a_second_payment_id_is_declined_and_the_first_is_untouched()
    {
        PlacedPayment first = await scenario.AuthorizedAsync(100m);
        string token = await scenario.TokenAsync();
        PaymentAuthorizationRequested second = Scenario.AuthorizationFor(token, 100m, first.TripId);

        await scenario.SendAsync(second);
        ProbedMessage declined = await scenario.NextForOperationAsync(nameof(PaymentDeclined), first.TripId, second.PaymentId, "paymentId");

        declined.DataString("reason").ShouldBe("PaymentAlreadyExistsForTrip");
        (await payments.Sim.IntentsOfAsync(first.TripId)).Count.ShouldBe(1);
        (await scenario.PaymentAsync(first.PaymentId)).Status.ShouldBe(PaymentStatus.Authorized);
    }

    [Fact]
    public async Task Every_request_is_recorded_once_with_its_idempotency_key()
    {
        PlacedPayment payment = await scenario.CapturedAsync(100m, 90m);
        Guid refund = Guid.CreateVersion7();
        await scenario.SendAsync(new PaymentRefundRequested(payment.TripId, payment.PaymentId, refund, Scenario.Eur(10m)));
        await scenario.NextAsync(nameof(PaymentRefunded), payment.TripId);

        List<OperationRecord> records = await scenario.OperationsAsync(payment.TripId);

        records.Select(static r => (r.Kind, r.Result)).ShouldBe(
        [
            (OperationKind.Authorize, OperationResult.Succeeded),
            (OperationKind.Capture, OperationResult.Succeeded),
            (OperationKind.Refund, OperationResult.Succeeded),
        ]);
        records.Select(static r => r.IdempotencyKey).ShouldBe(
        [
            Scenario.KeyOf(payment.TripId, OperationKind.Authorize, payment.PaymentId),
            Scenario.KeyOf(payment.TripId, OperationKind.Capture, payment.PaymentId),
            Scenario.KeyOf(payment.TripId, OperationKind.Refund, refund),
        ]);
        (await payments.Sim.OperationAsync(records[1].IdempotencyKey)).ShouldNotBeNull("the provider knows the operation by the same key");
    }

    [Fact]
    public async Task An_operator_reads_a_payment_with_its_operations_and_no_card_data()
    {
        PlacedPayment payment = await scenario.CapturedAsync(100m, 90m);

        using HttpResponseMessage response = await Get($"/payments/{payment.PaymentId}", TestTokens.Admin(PaymentsHost.Audience));
        string text = await response.Content.ReadAsStringAsync();
        JsonElement body = JsonElement.Parse(text);

        response.StatusCode.ShouldBe(HttpStatusCode.OK);
        response.Headers.ETag.ShouldNotBeNull();
        body.GetProperty("status").GetString().ShouldBe("Captured");
        body.GetProperty("captured").GetProperty("amount").GetString().ShouldBe("90.00");
        body.GetProperty("operations").EnumerateArray().Select(static o => o.GetProperty("kind").GetString()).ShouldBe(["Authorize", "Capture"]);
        text.ShouldNotContain("4242");
        text.ShouldNotContain("pm_");
    }

    [Fact]
    public async Task An_unknown_payment_is_a_404_PaymentNotFound_and_only_an_admin_may_look()
    {
        PlacedPayment payment = await scenario.AuthorizedAsync(10m);

        using HttpResponseMessage missing = await Get($"/payments/{Guid.CreateVersion7()}", TestTokens.Admin(PaymentsHost.Audience));
        using HttpResponseMessage traveller = await Get($"/payments/{payment.PaymentId}", TestTokens.Traveller(PaymentsHost.Audience));
        using HttpResponseMessage anonymous = await Get($"/payments/{payment.PaymentId}", bearer: null);

        missing.StatusCode.ShouldBe(HttpStatusCode.NotFound);
        JsonElement.Parse(await missing.Content.ReadAsStringAsync()).GetProperty("error").GetProperty("code").GetString().ShouldBe("PaymentNotFound");
        traveller.StatusCode.ShouldBe(HttpStatusCode.Forbidden);
        anonymous.StatusCode.ShouldBe(HttpStatusCode.Unauthorized);
    }

    [Fact]
    public async Task Health_is_live_and_ready_without_an_api_version()
    {
        using HttpResponseMessage live = await payments.Client.GetAsync("/health/live");
        using HttpResponseMessage ready = await payments.Client.GetAsync("/health/ready");

        live.StatusCode.ShouldBe(HttpStatusCode.OK);
        ready.StatusCode.ShouldBe(HttpStatusCode.OK);
    }

    private async Task<HttpResponseMessage> Get(string path, string? bearer)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, Scenario.Version(path));
        if (bearer is not null)
        {
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", bearer);
        }

        return await payments.Client.SendAsync(request);
    }
}
