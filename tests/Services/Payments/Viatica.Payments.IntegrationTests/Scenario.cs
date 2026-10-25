using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Viatica.Contracts;
using Viatica.Payments.Domain;
using Viatica.Testing;

namespace Viatica.Payments.IntegrationTests;

/// <summary>The test cards of the simulator, by what they do.</summary>
internal static class Cards
{
    public const string Visa = "4242424242424242";
    public const string Mastercard = "5555555555554444";
    public const string Declined = "4000000000000002";
    public const string InsufficientFunds = "4000000000009995";

    /// <summary>Every operation does its work at the provider and the answer is lost.</summary>
    public const string TimesOutButHappens = "4000009990000013";

    /// <summary>The first try of every operation hangs and does nothing.</summary>
    public const string TimesOutAndDoesNotHappen = "4000009990000021";
}

/// <summary>A payment Booking has asked for.</summary>
internal sealed record PlacedPayment(Guid TripId, Guid PaymentId, string Token, Price Amount);

/// <summary>
/// The steps every payment test takes, written once: tokenise a card over HTTP the way a traveller would, ask for
/// operations the way Booking would, and wait for the answer on the topic Booking would read.
/// </summary>
internal sealed class Scenario(PaymentsFixture payments)
{
    private static readonly TimeSpan Wait = TimeSpan.FromSeconds(45);

    // The host lives for the whole class and its log only grows. A scenario is made once per test, so what it counts as a
    // failure is what happened after it was made.
    private readonly int handlerFailuresBefore = CountHandlerFailures(payments.Host.Logs);

    public PaymentsHost Host => payments.Host;

    public SimHost Sim => payments.Sim;

    public static Price Eur(decimal amount) => new(amount, "EUR");

    public static string Version(string url) => url + (url.Contains('?', StringComparison.Ordinal) ? "&" : "?") + "api-version=" + PaymentsHost.ApiVersion;

    public async Task<string> TokenAsync(string number = Cards.Visa)
    {
        using HttpResponseMessage response = await TokeniseAsync(new { number, expMonth = 12, expYear = 2030, cvc = "123" });
        response.StatusCode.ShouldBe(HttpStatusCode.Created, await response.Content.ReadAsStringAsync());
        return JsonElement.Parse(await response.Content.ReadAsStringAsync()).GetProperty("token").GetString()!;
    }

    public async Task<HttpResponseMessage> TokeniseAsync(object body, string? bearer = null)
    {
        using var request = new HttpRequestMessage(HttpMethod.Post, Version("/payments/methods")) { Content = JsonContent.Create(body) };
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", bearer ?? TestTokens.Traveller(PaymentsHost.Audience));
        return await payments.Client.SendAsync(request);
    }

    public Task<ProbedMessage> NextAsync(string type, Guid tripId) =>
        payments.Probe.WaitForAsync(m => m.Type == type && m.DataString("tripId") == tripId.ToString(), Wait);

    public Task<ProbedMessage> NextForOperationAsync(string type, Guid tripId, Guid operationId, string idField = "operationId") =>
        payments.Probe.WaitForAsync(
            m => m.Type == type && m.DataString("tripId") == tripId.ToString() && m.DataString(idField) == operationId.ToString(), Wait);

    public static PaymentAuthorizationRequested AuthorizationFor(string token, decimal amount, Guid? tripId = null, Guid? paymentId = null) =>
        new(tripId ?? Guid.CreateVersion7(), paymentId ?? Guid.CreateVersion7(), Eur(amount), token);

    /// <summary>Asks for an authorisation of <paramref name="amount"/> on a new card and waits for it to be granted.</summary>
    public async Task<PlacedPayment> AuthorizedAsync(decimal amount = 100m, string card = Cards.Visa)
    {
        string token = await TokenAsync(card);
        PaymentAuthorizationRequested request = AuthorizationFor(token, amount);
        await payments.Booking.SendAsync(request);
        await NextAsync(nameof(PaymentAuthorized), request.TripId);
        return new PlacedPayment(request.TripId, request.PaymentId, token, request.Amount);
    }

    public async Task<PlacedPayment> CapturedAsync(decimal authorized = 100m, decimal? captured = null)
    {
        PlacedPayment payment = await AuthorizedAsync(authorized);
        await payments.Booking.SendAsync(new PaymentCaptureRequested(payment.TripId, payment.PaymentId, Eur(captured ?? authorized)));
        await NextAsync(nameof(PaymentCaptured), payment.TripId);
        return payment;
    }

    public Task SendAsync(params IIntegrationEvent[] events) => payments.Booking.SendAsync(events);

    /// <summary>
    /// Every event Payments published for a trip, up to a sentinel. The sentinel is a request about a payment that does
    /// not exist, on the same trip, so it shares the partition of everything before it: once its answer is on the topic,
    /// the answers to all earlier requests of the trip are there too, and a count is a count and not a guess.
    /// </summary>
    public async Task<IReadOnlyList<ProbedMessage>> EventsOfAsync(Guid tripId)
    {
        Guid ghost = Guid.CreateVersion7();
        await payments.Booking.SendAsync(new PaymentVoidRequested(tripId, ghost));
        ProbedMessage sentinel = await NextForOperationAsync(nameof(PaymentOperationRefused), tripId, ghost);

        return
        [
            .. payments.Probe.Received
                .Where(m => m.DataString("tripId") == tripId.ToString() && m.Id != sentinel.Id),
        ];
    }

    /// <summary>
    /// Proves nothing is published for a trip during <paramref name="window"/>, other than the types in
    /// <paramref name="except"/>. It reads the topic while it waits, so a quiet topic is observed and not assumed.
    /// </summary>
    public async Task NothingPublishedAsync(Guid tripId, TimeSpan window, params string[] except) =>
        await Should.ThrowAsync<TimeoutException>(() =>
            payments.Probe.WaitForAsync(m => m.DataString("tripId") == tripId.ToString() && !except.Contains(m.Type), window));

    /// <summary>
    /// The consumer logs every attempt of a handler that threw. A request that is a harmless repeat must not be one of
    /// them: a handler that throws on a duplicate is retried, parked on the dead letter topic, and holds its partition
    /// meanwhile, and the outcome events alone would never show it.
    /// </summary>
    public IReadOnlyList<string> HandlerFailures() => [.. HandlerFailureEntries(payments.Host.Logs).Skip(handlerFailuresBefore)];

    private static int CountHandlerFailures(CapturedLogs logs) => HandlerFailureEntries(logs).Count();

    private static IEnumerable<string> HandlerFailureEntries(CapturedLogs logs) =>
        logs.Entries.Where(static e => e.Contains("Handler for ", StringComparison.Ordinal) && e.Contains("failed on attempt", StringComparison.Ordinal));

    public static IReadOnlyList<ProbedMessage> OfType(IEnumerable<ProbedMessage> events, string type) =>
        [.. events.Where(m => m.Type == type)];

    public Task<Payment> PaymentAsync(Guid paymentId) =>
        payments.Host.InDbAsync(db => db.Payments.AsNoTracking().SingleAsync(p => p.Id == paymentId));

    public Task<List<OperationRecord>> OperationsAsync(Guid tripId) =>
        payments.Host.InDbAsync(db => db.Operations.AsNoTracking().Where(o => o.TripId == tripId).OrderBy(o => o.CreatedAt).ToListAsync());

    /// <summary>The key a request has at the provider, as the service derives it.</summary>
    public static string KeyOf(Guid tripId, OperationKind kind, Guid operationId) => OperationRecord.KeyFor(tripId, kind, operationId);
}
