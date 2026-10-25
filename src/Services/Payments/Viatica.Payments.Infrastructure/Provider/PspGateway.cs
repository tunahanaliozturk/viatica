using System.Diagnostics;
using System.Globalization;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Serialization.Metadata;
using Polly.CircuitBreaker;
using Polly.Timeout;
using Viatica.Kernel;
using Viatica.Payments.Application;
using Viatica.Payments.Application.Provider;
using Viatica.Payments.Domain.Cards;

namespace Viatica.Payments.Infrastructure.Provider;

/// <summary>
/// Talks to the payment provider over HTTP and turns its foreign contract into the platform's outcomes. Nothing outside
/// this class knows the provider's field names, status codes or error texts.
/// </summary>
/// <remarks>
/// <para>
/// Resilience sits in the client's pipeline (see <see cref="PspClient"/>): a timeout per call and a circuit breaker, and
/// no retry. What to do when a call gets no answer is the operation desk's decision, not the transport's.
/// </para>
/// <para>
/// A failure is classified by whether the provider might have acted on the request. A timeout, a dropped connection
/// and a 500 might have; an open circuit and a 503 certainly did not. The desk relies on that distinction.
/// </para>
/// <para>
/// The card number is read from <see cref="CardDetails"/> in exactly one place, <see cref="TokeniseAsync"/>, into a
/// request body that lives for that call. No log line here has the body, a header or an exception message built from
/// one, and the client's own logging records the URL and the status only.
/// </para>
/// </remarks>
public sealed class PspGateway(IHttpClientFactory clients, TimeProvider time) : IPaymentProvider
{
    private const string IdempotencyKeyHeader = "Idempotency-Key";

    public async Task<TokeniseOutcome> TokeniseAsync(CardDetails card, string idempotencyKey, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(card);

        var body = new TokenRequestDto(new CardDto(card.Number.Reveal(), card.ExpMonth, card.ExpYear, card.Cvc));
        Reply reply = await SendAsync(
            "tokenise", HttpMethod.Post, "v1/tokens", JsonContent.Create(body, PspJson.Default.TokenRequestDto), idempotencyKey, cancellationToken);

        if (reply.Failure is { } failure)
        {
            return failure;
        }

        if (reply.IsSuccess && Read(reply.Body, PspJson.Default.TokenResponseDto) is { } token)
        {
            return new Tokenised(token.Id, token.Card.Brand, token.Card.Last4, token.Card.ExpMonth, token.Card.ExpYear);
        }

        return IsRefusal(reply.Status)
            ? new TokenRefused(ErrorCode(reply.Body))
            : new ProviderFailure(FailureKind.Error, MayHaveReachedProvider: reply.IsSuccess || reply.Status >= 500);
    }

    public async Task<ProviderResult> AuthoriseAsync(
        string providerToken, Money amount, Guid tripId, Guid paymentId, string idempotencyKey, CancellationToken cancellationToken)
    {
        var body = new IntentRequestDto(
            ToMinorUnits(amount),
            amount.Currency.ToLowerInvariant(),
            providerToken,
            "manual",
            new Dictionary<string, string> { ["trip_id"] = tripId.ToString("N"), ["payment_id"] = paymentId.ToString("N") });

        return await MutateAsync(
            "authorise", "v1/payment_intents", JsonContent.Create(body, PspJson.Default.IntentRequestDto), idempotencyKey, cancellationToken);
    }

    public Task<ProviderResult> CaptureAsync(string intentId, Money amount, string idempotencyKey, CancellationToken cancellationToken) =>
        MutateAsync(
            "capture",
            $"v1/payment_intents/{Uri.EscapeDataString(intentId)}/capture",
            JsonContent.Create(new CaptureRequestDto(ToMinorUnits(amount)), PspJson.Default.CaptureRequestDto),
            idempotencyKey,
            cancellationToken);

    public Task<ProviderResult> VoidAsync(string intentId, string idempotencyKey, CancellationToken cancellationToken) =>
        MutateAsync("void", $"v1/payment_intents/{Uri.EscapeDataString(intentId)}/cancel", content: null, idempotencyKey, cancellationToken);

    public Task<ProviderResult> RefundAsync(
        string intentId, Money amount, Guid tripId, Guid refundId, string idempotencyKey, CancellationToken cancellationToken)
    {
        var body = new RefundRequestDto(
            intentId,
            ToMinorUnits(amount),
            new Dictionary<string, string> { ["trip_id"] = tripId.ToString("N"), ["refund_id"] = refundId.ToString("N") });

        return MutateAsync("refund", "v1/refunds", JsonContent.Create(body, PspJson.Default.RefundRequestDto), idempotencyKey, cancellationToken);
    }

    public async Task<LookupResult> LookUpAsync(string idempotencyKey, CancellationToken cancellationToken)
    {
        Reply reply = await SendAsync(
            "lookup", HttpMethod.Get, $"v1/operations?idempotency_key={Uri.EscapeDataString(idempotencyKey)}", content: null, idempotencyKey: null, cancellationToken);

        if (reply.Failure is { } failure)
        {
            return failure;
        }

        if (reply.Status == 404 && ErrorCode(reply.Body) == "resource_missing")
        {
            return new NoSuchOperation();
        }

        if (reply.IsSuccess && Read(reply.Body, PspJson.Default.OperationDto) is { } recorded)
        {
            // What the provider recorded is the answer it gave the first time, status and body, so it is read the way
            // that answer would have been read.
            Reply original = new(null, recorded.HttpStatus, recorded.Result.GetRawText());
            return Map(original) switch
            {
                ProviderSucceeded succeeded => new OperationFound(succeeded),
                ProviderRefused refused => new OperationFound(refused),
                _ => new ProviderFailure(FailureKind.Error, MayHaveReachedProvider: true),
            };
        }

        return new ProviderFailure(FailureKind.Error, MayHaveReachedProvider: true);
    }

    private async Task<ProviderResult> MutateAsync(
        string operation, string path, HttpContent? content, string idempotencyKey, CancellationToken cancellationToken)
    {
        Reply reply = await SendAsync(operation, HttpMethod.Post, path, content, idempotencyKey, cancellationToken);
        return Map(reply);
    }

    private static ProviderResult Map(Reply reply)
    {
        if (reply.Failure is { } failure)
        {
            return failure;
        }

        if (reply.IsSuccess)
        {
            return Read(reply.Body, PspJson.Default.IdDto) is { Id.Length: > 0 } ok
                ? new ProviderSucceeded(ok.Id)
                : new ProviderFailure(FailureKind.Error, MayHaveReachedProvider: true);
        }

        // The provider answered and said no: a card error, a bad request, a state the operation cannot follow.
        // Anything else (401, 403, 429) it said before doing anything, so nothing was done, and nothing is decided.
        return IsRefusal(reply.Status)
            ? new ProviderRefused(ErrorCode(reply.Body))
            : new ProviderFailure(FailureKind.Error, MayHaveReachedProvider: false);
    }

    private static bool IsRefusal(int status) => status is 400 or 402 or 404 or 409 or 422;

    /// <summary>One provider call: send, time it, classify the failure, and read the body whatever the status.</summary>
    private async Task<Reply> SendAsync(
        string operation, HttpMethod method, string path, HttpContent? content, string? idempotencyKey, CancellationToken cancellationToken)
    {
        using Activity? activity = PaymentsTelemetry.Source.StartActivity($"payments.provider.{operation}", ActivityKind.Client);

        long started = time.GetTimestamp();
        using var message = new HttpRequestMessage(method, path) { Content = content };
        if (idempotencyKey is not null)
        {
            message.Headers.TryAddWithoutValidation(IdempotencyKeyHeader, idempotencyKey);
        }

        string outcome = "error";
        try
        {
            using HttpClient client = clients.CreateClient(PspClient.Name);
            using HttpResponseMessage response = await client.SendAsync(message, HttpCompletionOption.ResponseContentRead, cancellationToken);

            int status = (int)response.StatusCode;
            outcome = status.ToString(CultureInfo.InvariantCulture);

            // 503 is the provider saying it is down before it did anything. Every other 5xx may have happened.
            if (status == 503)
            {
                return Failed(FailureKind.Error, mayHaveReached: false);
            }

            if (status == 504)
            {
                outcome = "timeout";
                return Failed(FailureKind.Timeout, mayHaveReached: true);
            }

            if (status >= 500)
            {
                return Failed(FailureKind.Error, mayHaveReached: true);
            }

            return new Reply(null, status, await response.Content.ReadAsStringAsync(cancellationToken));
        }
        catch (BrokenCircuitException)
        {
            outcome = "circuit_open";
            return Failed(FailureKind.CircuitOpen, mayHaveReached: false);
        }
        catch (TimeoutRejectedException)
        {
            outcome = "timeout";
            return Failed(FailureKind.Timeout, mayHaveReached: true);
        }
        catch (HttpRequestException)
        {
            outcome = "connection_error";
            return Failed(FailureKind.Error, mayHaveReached: true);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            // The client's own timeout, not the caller hanging up.
            outcome = "timeout";
            return Failed(FailureKind.Timeout, mayHaveReached: true);
        }
        catch (OperationCanceledException)
        {
            outcome = "cancelled";
            throw;
        }
        finally
        {
            double elapsedMs = time.GetElapsedTime(started).TotalMilliseconds;
            var operationTag = new KeyValuePair<string, object?>("operation", operation);
            PaymentsTelemetry.ProviderCalls.Add(1, operationTag, new KeyValuePair<string, object?>("outcome", outcome));
            PaymentsTelemetry.ProviderLatency.Record(elapsedMs, operationTag);
        }
    }

    private static Reply Failed(FailureKind kind, bool mayHaveReached) => new(new ProviderFailure(kind, mayHaveReached), 0, "");

    /// <summary>The code to report for a refusal: the card's decline code if there is one, else the error code.</summary>
    private static string ErrorCode(string body) =>
        Read(body, PspJson.Default.ErrorEnvelopeDto)?.Error is { } error
            ? error.DeclineCode ?? error.Code ?? "provider_error"
            : "provider_error";

    private static T? Read<T>(string body, JsonTypeInfo<T> type)
        where T : class
    {
        try
        {
            return JsonSerializer.Deserialize(body, type);
        }
        catch (JsonException)
        {
            // A provider that answers with something unreadable is a provider in trouble, not a "no".
            return null;
        }
    }

    private static long ToMinorUnits(Money money)
    {
        decimal scale = 1;
        for (int i = 0; i < money.MinorUnits; i++)
        {
            scale *= 10;
        }

        return decimal.ToInt64(money.Amount * scale);
    }

    private readonly record struct Reply(ProviderFailure? Failure, int Status, string Body)
    {
        public bool IsSuccess => Status is >= 200 and < 300;
    }
}
