using System.Diagnostics;
using Microsoft.AspNetCore.Mvc;
using Viatica.PspSim.Behaviour;
using Viatica.PspSim.Ledger;

namespace Viatica.PspSim.Api;

internal static class ProviderApi
{
    public const string IdempotencyKeyHeader = "Idempotency-Key";
    public const string ReplayedHeader = "Idempotent-Replayed";

    private const int MaxKeyLength = 255;
    private const string LogCategory = "Viatica.PspSim.Provider";

    /// <summary>
    /// The provider API. Every call goes through the behaviour filter first, so outages and latency apply to all of
    /// them; the mutating calls then meet the fate their card or the knobs picked for them.
    /// </summary>
    public static void MapProviderApi(this IEndpointRouteBuilder app)
    {
        RouteGroupBuilder v1 = app.MapGroup("/v1").AddEndpointFilter(BehaveAsync);

        v1.MapPost("/tokens", TokeniseAsync);
        v1.MapPost("/payment_intents", AuthoriseAsync);
        v1.MapGet("/payment_intents", static (ProviderLedger ledger) =>
            TypedResults.Json(new ListView<PaymentIntentView>("list", ledger.Intents()), SimJson.Default.ListViewPaymentIntentView));
        v1.MapGet("/payment_intents/{id}", static (string id, ProviderLedger ledger) =>
            ledger.FindIntent(id) is { } intent
                ? TypedResults.Json(intent, SimJson.Default.PaymentIntentView)
                : ProviderError.Missing("payment intent").ToResult());
        v1.MapPost("/payment_intents/{id}/capture", CaptureAsync);
        v1.MapPost("/payment_intents/{id}/cancel", CancelAsync);
        v1.MapPost("/refunds", RefundAsync);
        v1.MapGet("/refunds", static (ProviderLedger ledger) =>
            TypedResults.Json(new ListView<RefundView>("list", ledger.Refunds()), SimJson.Default.ListViewRefundView));
        v1.MapGet("/operations", LookUpAsync);
    }

    private static Task<IResult> TokeniseAsync(
        HttpContext http, TokenRequest request, ProviderLedger ledger, ProviderState state, ILoggerFactory loggers) =>
        MutateAsync(
            http, ledger, state, "token",
            card: null, // a timeout card misbehaves when it is charged, not when it is turned into a token
            key => ledger.Tokenise(key, request),
            loggers);

    private static Task<IResult> AuthoriseAsync(
        HttpContext http, PaymentIntentRequest request, ProviderLedger ledger, ProviderState state, ILoggerFactory loggers) =>
        MutateAsync(http, ledger, state, "authorise", ledger.BehaviourOfToken(request.PaymentMethod), key => ledger.Authorise(key, request), loggers);

    private static Task<IResult> CaptureAsync(
        HttpContext http, string id, [FromBody] CaptureRequest? request, ProviderLedger ledger, ProviderState state, ILoggerFactory loggers) =>
        MutateAsync(http, ledger, state, "capture", ledger.BehaviourOfIntent(id), key => ledger.Capture(key, id, request ?? new CaptureRequest(null)), loggers);

    private static Task<IResult> CancelAsync(
        HttpContext http, string id, ProviderLedger ledger, ProviderState state, ILoggerFactory loggers) =>
        MutateAsync(http, ledger, state, "cancel", ledger.BehaviourOfIntent(id), key => ledger.Cancel(key, id), loggers);

    private static Task<IResult> RefundAsync(
        HttpContext http, RefundRequest request, ProviderLedger ledger, ProviderState state, ILoggerFactory loggers) =>
        MutateAsync(http, ledger, state, "refund", ledger.BehaviourOfIntent(request.PaymentIntent), key => ledger.Refund(key, request), loggers);

    /// <summary>
    /// The operation recorded under an idempotency key. This is how a caller whose request timed out learns whether the
    /// provider did the work, without doing it a second time.
    /// </summary>
    private static IResult LookUpAsync(
        [FromQuery(Name = "idempotency_key")] string? idempotencyKey, ProviderLedger ledger, ProviderState state)
    {
        if (state.Knobs.LookupOutage)
        {
            Telemetry.Fault("lookup_outage");
            return ProviderError.ServiceUnavailable.ToResult();
        }

        if (string.IsNullOrEmpty(idempotencyKey))
        {
            return ProviderError.Invalid("parameter_missing", "The idempotency_key query parameter is required.", "idempotency_key").ToResult();
        }

        return ledger.Find(idempotencyKey) is { } recorded
            ? TypedResults.Json(
                new OperationView("operation", recorded.Key, recorded.Type, recorded.Reply.Status, recorded.Reply.Body, recorded.Created.ToUnixTimeSeconds()),
                SimJson.Default.OperationView)
            : ProviderError.NoSuchOperation.ToResult();
    }

    private static async Task<IResult> MutateAsync(
        HttpContext http,
        ProviderLedger ledger,
        ProviderState state,
        string operation,
        CardBehaviour? card,
        Func<string, Execution> run,
        ILoggerFactory loggers)
    {
        ILogger logger = loggers.CreateLogger(LogCategory);

        if (http.Request.Headers[IdempotencyKeyHeader].ToString() is not { Length: > 0 and <= MaxKeyLength } key)
        {
            return ProviderError.KeyMissing.ToResult();
        }

        int attempt = ledger.NextAttempt(key);
        Fate fate = state.Decide(card, attempt);
        Activity.Current?.SetTag("pspsim.operation", operation);
        Activity.Current?.SetTag("pspsim.fate", fate.ToString());

        switch (fate)
        {
            case Fate.ServerError:
                Telemetry.Fault("server_error");
                Log.ServerError(logger, operation);
                return ProviderError.ServerError.ToResult();

            case Fate.TimeoutNotPerformed:
                Telemetry.Fault("timeout");
                Log.Timeout(logger, operation, performed: false);
                await HangAsync(state, http.RequestAborted);
                return ProviderError.GatewayTimeout.ToResult();
        }

        Execution execution = run(key);
        Telemetry.Operations.Add(1, new("operation", operation), new("status", execution.Reply.Status), new("replayed", execution.Replayed));

        if (execution.Replayed)
        {
            http.Response.Headers[ReplayedHeader] = "true";
            Log.Replayed(logger, operation);
        }

        if (fate == Fate.TimeoutPerformed)
        {
            // The work is done and recorded; only the answer is lost. This is the case that makes a caller look up.
            Telemetry.Fault("timeout_performed");
            Log.Timeout(logger, operation, performed: true);
            await HangAsync(state, http.RequestAborted);
            return ProviderError.GatewayTimeout.ToResult();
        }

        return execution.Reply.ToResult();
    }

    // Long enough to outlast any sane client timeout. A client that gives up cancels the request and ends the wait.
    private static Task HangAsync(ProviderState state, CancellationToken cancellationToken) =>
        Task.Delay(state.Knobs.TimeoutHangMs, cancellationToken);

    private static async ValueTask<object?> BehaveAsync(EndpointFilterInvocationContext context, EndpointFilterDelegate next)
    {
        HttpContext http = context.HttpContext;
        ProviderState state = http.RequestServices.GetRequiredService<ProviderState>();

        if (state.Knobs.Outage)
        {
            Telemetry.Fault("outage");
            return ProviderError.ServiceUnavailable.ToResult();
        }

        TimeSpan delay = state.Latency();
        if (delay > TimeSpan.Zero)
        {
            // A span of its own, so a trace shows the wait was the sim's doing and not the network's.
            using (Activity? activity = Telemetry.Source.StartActivity("pspsim.injected_latency"))
            {
                activity?.SetTag("pspsim.latency_ms", delay.TotalMilliseconds);
                await Task.Delay(delay, http.RequestAborted);
            }

            Telemetry.InjectedLatency.Record(delay.TotalMilliseconds);
        }

        return await next(context);
    }
}
