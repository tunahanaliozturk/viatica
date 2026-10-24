using System.Text.Json;
using System.Text.Json.Serialization.Metadata;
using Viatica.PspSim.Api;

namespace Viatica.PspSim.Ledger;

/// <summary>An error in the provider's own style: a type, a code, a message and sometimes the card's decline code.</summary>
internal sealed record ProviderError(int Status, string Type, string Code, string Message, string? DeclineCode = null, string? Param = null)
{
    public static readonly ProviderError ServiceUnavailable =
        new(StatusCodes.Status503ServiceUnavailable, "api_error", "service_unavailable", "The provider is not available.");

    public static readonly ProviderError ServerError =
        new(StatusCodes.Status500InternalServerError, "api_error", "internal_error", "The provider hit an internal error.");

    public static readonly ProviderError GatewayTimeout =
        new(StatusCodes.Status504GatewayTimeout, "api_error", "gateway_timeout", "The provider did not answer in time.");

    public static readonly ProviderError KeyMissing =
        new(StatusCodes.Status400BadRequest, "invalid_request_error", "idempotency_key_missing",
            "Every mutating call needs an Idempotency-Key header of at most 255 characters.", Param: "Idempotency-Key");

    public static readonly ProviderError KeyReused =
        new(StatusCodes.Status400BadRequest, "idempotency_error", "idempotency_key_in_use",
            "That Idempotency-Key was used for a different request.");

    public static readonly ProviderError NoSuchOperation =
        new(StatusCodes.Status404NotFound, "invalid_request_error", "resource_missing", "No operation was recorded under that Idempotency-Key.");

    public static ProviderError Invalid(string code, string message, string? param = null) =>
        new(StatusCodes.Status400BadRequest, "invalid_request_error", code, message, Param: param);

    public static ProviderError Missing(string what, string? param = null) =>
        new(StatusCodes.Status404NotFound, "invalid_request_error", "resource_missing", $"No such {what}.", Param: param);

    /// <summary>A card refused by the issuer, which Stripe answers with 402.</summary>
    public static ProviderError Card(string code, string message, string? declineCode = null, string? param = null) =>
        new(StatusCodes.Status402PaymentRequired, "card_error", code, message, declineCode, param);

    public IResult ToResult() =>
        TypedResults.Json(
            new ErrorBody(new ErrorDetail(Type, Code, Message, DeclineCode, Param)),
            SimJson.Default.ErrorBody,
            statusCode: Status);
}

/// <summary>
/// An answer as the provider sent it, kept as a JSON document so that a replay under the same idempotency key, and
/// a lookup of the operation, return exactly what the first call returned.
/// </summary>
internal sealed record Reply(int Status, JsonElement Body)
{
    public static Reply Ok<T>(T body, JsonTypeInfo<T> info) =>
        new(StatusCodes.Status200OK, JsonSerializer.SerializeToElement(body, info));

    public static Reply Fail(ProviderError error) =>
        new(error.Status, JsonSerializer.SerializeToElement(
            new ErrorBody(new ErrorDetail(error.Type, error.Code, error.Message, error.DeclineCode, error.Param)),
            SimJson.Default.ErrorBody));

    public IResult ToResult() => TypedResults.Json(Body, SimJson.Default.JsonElement, statusCode: Status);
}
