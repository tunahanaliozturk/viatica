using System.Text.Json;
using System.Text.Json.Serialization;

namespace Viatica.Payments.Infrastructure.Provider;

// The provider's wire shapes, snake_case, amounts as integers in the currency's minor unit. Nothing outside this folder
// knows them. Request bodies are built in memory, sent and dropped; none is ever logged.

internal sealed record CardDto(string Number, int ExpMonth, int ExpYear, string? Cvc)
{
    // The number is the one thing in this service that must never reach a log, a trace tag or an exception message.
    public override string ToString() => "card";
}

internal sealed record TokenRequestDto(CardDto Card);

internal sealed record CardInfoDto(string Brand, string Last4, int ExpMonth, int ExpYear);

internal sealed record TokenResponseDto(string Id, CardInfoDto Card);

internal sealed record IntentRequestDto(long Amount, string Currency, string PaymentMethod, string CaptureMethod, Dictionary<string, string> Metadata);

internal sealed record CaptureRequestDto(long AmountToCapture);

internal sealed record RefundRequestDto(string PaymentIntent, long Amount, Dictionary<string, string> Metadata);

/// <summary>Every success the provider sends has an <c>id</c>: the intent for an authorisation, a capture and a cancel, the refund for a refund.</summary>
internal sealed record IdDto(string Id);

internal sealed record ErrorDto(string? Type, string? Code, string? DeclineCode);

internal sealed record ErrorEnvelopeDto(ErrorDto? Error);

/// <summary>What the provider recorded for an idempotency key: the status it answered with and the body it answered with.</summary>
internal sealed record OperationDto(string? Type, int HttpStatus, JsonElement Result);

[JsonSourceGenerationOptions(
    PropertyNamingPolicy = JsonKnownNamingPolicy.SnakeCaseLower,
    PropertyNameCaseInsensitive = true,
    DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull)]
[JsonSerializable(typeof(TokenRequestDto))]
[JsonSerializable(typeof(TokenResponseDto))]
[JsonSerializable(typeof(IntentRequestDto))]
[JsonSerializable(typeof(CaptureRequestDto))]
[JsonSerializable(typeof(RefundRequestDto))]
[JsonSerializable(typeof(IdDto))]
[JsonSerializable(typeof(ErrorEnvelopeDto))]
[JsonSerializable(typeof(OperationDto))]
internal sealed partial class PspJson : JsonSerializerContext;
