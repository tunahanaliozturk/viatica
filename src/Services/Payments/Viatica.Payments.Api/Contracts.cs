using System.Text.Json;
using System.Text.Json.Serialization;
using Viatica.Payments.Application.Features.GetPayment;
using Viatica.Payments.Application.Features.TokeniseCard;

namespace Viatica.Payments.Api;

/// <summary>
/// A card to tokenise. Flat, with the field names the validator reports as <c>target</c>. It prints as its name and
/// nothing else, so the framework formatting a failed binding cannot put the card in a log.
/// </summary>
public sealed record TokeniseCardRequest(string Number, int ExpMonth, int ExpYear, string? Cvc = null)
{
    public override string ToString() => nameof(TokeniseCardRequest);

    internal TokeniseCardCommand ToCommand() => new(Number, ExpMonth, ExpYear, Cvc);
}

[JsonSourceGenerationOptions(
    JsonSerializerDefaults.Web,
    UseStringEnumConverter = true,
    DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    RespectNullableAnnotations = true,
    RespectRequiredConstructorParameters = true)]
[JsonSerializable(typeof(TokeniseCardRequest))]
[JsonSerializable(typeof(PaymentMethodView))]
[JsonSerializable(typeof(PaymentView))]
internal sealed partial class PaymentsJsonContext : JsonSerializerContext;
