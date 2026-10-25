using System.Diagnostics;
using Microsoft.AspNetCore.Http.HttpResults;
using Relevox;
using Viatica.Kernel;
using Viatica.Kernel.Web;
using Viatica.Payments.Application;
using Viatica.Payments.Application.Features.GetPayment;
using Viatica.Payments.Application.Features.TokeniseCard;

namespace Viatica.Payments.Api;

/// <summary>
/// The HTTP surface. Each handler's union result is switched here, so every case has a status code and a body the
/// compiler makes sure exists (ADR 0001). Payments are not created over HTTP: Booking asks for them over Kafka, because
/// the checkout saga is event driven and every step of it must be repeatable by id.
/// </summary>
public static class PaymentEndpoints
{
    public static void MapPaymentEndpoints(this IEndpointRouteBuilder app)
    {
        RouteGroupBuilder payments = app.MapGroup("/payments").WithTags("Payments");

        payments.MapPost("/methods", TokeniseAsync)
            .RequireAuthorization(Policies.Traveller)
            .WithSummary("Tokenise a test card (PAY-1)")
            .WithDescription("The card goes to the payment provider and nowhere else. The answer is a token and the last four digits.");

        payments.MapGet("/{paymentId:guid}", GetPaymentAsync)
            .RequireAuthorization(Policies.Admin)
            .WithSummary("A payment as operators see it; no card data");
    }

    private static async Task<IResult> TokeniseAsync(TokeniseCardRequest body, ISender sender, CancellationToken cancellationToken)
    {
        TokeniseCardResult result = await sender.Send(body.ToCommand(), cancellationToken);

        return result switch
        {
            CardTokenised tokenised => TypedResults.Json(tokenised.Method, PaymentsJsonContext.Default.PaymentMethodView, statusCode: StatusCodes.Status201Created),
            CardRejected rejected => ErrorResult.Rejected(rejected.Code),
            TokenisationUnavailable => ErrorResult.Unavailable(PaymentErrorCodes.ProviderUnavailable, "The payment provider could not be asked. Try again in a moment."),
            null => throw new UnreachableException("A tokenise result was never set."),
        };
    }

    private static async Task<Ok<PaymentView>> GetPaymentAsync(Guid paymentId, HttpContext http, ISender sender, CancellationToken cancellationToken)
    {
        PaymentView payment = await sender.Send(new GetPaymentQuery(paymentId), cancellationToken);
        http.Response.SetETag(payment.Version);
        return TypedResults.Ok(payment);
    }
}

/// <summary>An error envelope with a status the shared categories do not cover (503), or a refusal that carries the provider's code.</summary>
internal sealed class ErrorResult(int statusCode, ApiError error, int? retryAfterSeconds = null) : IResult
{
    /// <summary>
    /// <c>400 PaymentMethodInvalid</c>. The provider's code goes in <c>innererror</c> (<c>incorrect_number</c>,
    /// <c>expired_card</c>), which is a code and never the card.
    /// </summary>
    public static ErrorResult Rejected(string providerCode) =>
        new(
            StatusCodes.Status400BadRequest,
            new ApiError(PaymentErrorCodes.PaymentMethodInvalid, "The card cannot be used.") { InnerError = new InnerError(providerCode) });

    public static ErrorResult Unavailable(string code, string message) =>
        new(StatusCodes.Status503ServiceUnavailable, new ApiError(code, message), retryAfterSeconds: 5);

    public Task ExecuteAsync(HttpContext httpContext)
    {
        ArgumentNullException.ThrowIfNull(httpContext);

        if (retryAfterSeconds is { } seconds)
        {
            httpContext.Response.Headers.RetryAfter = seconds.ToString(System.Globalization.CultureInfo.InvariantCulture);
        }

        return ErrorResponses.WriteAsync(httpContext, statusCode, error);
    }
}
