using System.Text.Json;
using System.Text.Json.Serialization;
using Viatica.PspSim.Behaviour;

namespace Viatica.PspSim.Api;

// The wire shapes of the simulated provider. snake_case, ids with a type prefix (tok_, pi_, re_), amounts as integers
// in the currency's minor unit, lowercase currency codes and Stripe's error object: they should read like somebody
// else's API, so the Payments adapter is written against a foreign contract, as it will be in production. One
// deliberate difference from Stripe: requests are JSON, not form encoded.

internal sealed record CardInput(string Number, int ExpMonth, int ExpYear, string? Cvc)
{
    // The number is the one thing in this process that must never reach a log, a trace tag or an exception message.
    public override string ToString() => "card";
}

internal sealed record TokenRequest(CardInput? Card);

internal sealed record CardView(string Brand, string Last4, int ExpMonth, int ExpYear, string Funding);

internal sealed record TokenView(string Id, string Object, CardView Card, long Created);

internal sealed record PaymentIntentRequest(
    long Amount,
    string? Currency,
    string? PaymentMethod,
    string? CaptureMethod,
    Dictionary<string, string>? Metadata);

internal sealed record CaptureRequest(long? AmountToCapture);

internal sealed record RefundRequest(string? PaymentIntent, long? Amount, Dictionary<string, string>? Metadata);

internal sealed record PaymentIntentView(
    string Id,
    string Object,
    long Amount,
    string Currency,
    string Status,
    long AmountCapturable,
    long AmountReceived,
    long AmountRefunded,
    string PaymentMethod,
    string CaptureMethod,
    IReadOnlyDictionary<string, string> Metadata,
    long Created);

internal sealed record RefundView(
    string Id,
    string Object,
    long Amount,
    string Currency,
    string PaymentIntent,
    string Status,
    IReadOnlyDictionary<string, string> Metadata,
    long Created);

/// <summary>
/// What the provider recorded for an idempotency key: the operation, the status it answered with and the body it
/// answered with. Stripe has no such endpoint; a caller whose request timed out needs one to learn the outcome.
/// </summary>
internal sealed record OperationView(string Object, string IdempotencyKey, string Type, int HttpStatus, JsonElement Result, long Created);

internal sealed record ListView<T>(string Object, IReadOnlyList<T> Data);

internal sealed record ErrorBody(ErrorDetail Error);

internal sealed record ErrorDetail(string Type, string Code, string Message, string? DeclineCode, string? Param);

internal sealed record KnobsView(Knobs Knobs, Knobs Defaults);

internal sealed record TestCardView(string Number, string Brand, string Behaviour);

[JsonSourceGenerationOptions(
    PropertyNamingPolicy = JsonKnownNamingPolicy.SnakeCaseLower,
    PropertyNameCaseInsensitive = true,
    DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull)]
[JsonSerializable(typeof(TokenRequest))]
[JsonSerializable(typeof(TokenView))]
[JsonSerializable(typeof(PaymentIntentRequest))]
[JsonSerializable(typeof(PaymentIntentView))]
[JsonSerializable(typeof(CaptureRequest))]
[JsonSerializable(typeof(RefundRequest))]
[JsonSerializable(typeof(RefundView))]
[JsonSerializable(typeof(OperationView))]
[JsonSerializable(typeof(ListView<PaymentIntentView>))]
[JsonSerializable(typeof(ListView<RefundView>))]
[JsonSerializable(typeof(ListView<TestCardView>))]
[JsonSerializable(typeof(ErrorBody))]
[JsonSerializable(typeof(JsonElement))]
internal sealed partial class SimJson : JsonSerializerContext;

/// <summary>The admin API's own shapes: PascalCase like the airline simulator's, since knobs are ours and not Stripe's.</summary>
[JsonSourceGenerationOptions(
    PropertyNameCaseInsensitive = true,
    UseStringEnumConverter = true)]
[JsonSerializable(typeof(KnobsPatch))]
[JsonSerializable(typeof(KnobsView))]
internal sealed partial class AdminJson : JsonSerializerContext;
