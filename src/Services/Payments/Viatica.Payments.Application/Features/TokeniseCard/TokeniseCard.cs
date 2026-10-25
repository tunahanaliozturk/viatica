using System.Diagnostics;
using Microsoft.Extensions.Logging;
using Moongazing.OrionGuard.DependencyInjection;
using Relevox;
using Viatica.Kernel;
using Viatica.Payments.Application.Provider;
using Viatica.Payments.Domain;
using Viatica.Payments.Domain.Cards;

namespace Viatica.Payments.Application.Features.TokeniseCard;

/// <summary>
/// Turns a test card into a token Booking can carry (PAY-1). The number goes to the provider and stops there: it is
/// not stored, not logged, not in an event, and not in any error this service answers with.
/// </summary>
/// <remarks>
/// Strings stay strings until the validator has looked at them. The command prints as its name and nothing else, so a
/// pipeline behaviour, a log scope or a failed assertion that formats it shows no card.
/// </remarks>
public sealed record TokeniseCardCommand(string Number, int ExpMonth, int ExpYear, string? Cvc) : IRequest<TokeniseCardResult>
{
    public override string ToString() => nameof(TokeniseCardCommand);
}

/// <summary>What a receipt shows about the card. The token is the only handle on it.</summary>
public sealed record PaymentMethodView(string Token, string Brand, string Last4, int ExpMonth, int ExpYear);

public sealed record CardTokenised(PaymentMethodView Method);

/// <summary>The provider will not take the card. <see cref="Code"/> is its error code, which never repeats the card.</summary>
public sealed record CardRejected(string Code);

/// <summary>The provider could not be asked. The card is not at fault, and trying again later is right.</summary>
public sealed record TokenisationUnavailable;

public union TokeniseCardResult(CardTokenised, CardRejected, TokenisationUnavailable);

public sealed class TokeniseCardValidator : AbstractValidator<TokeniseCardCommand>
{
    private static readonly (string Code, string Field, string Message)[] Rules =
    [
        (CardProblemCodes.InvalidCardNumber, "number", "number is not a valid card number."),
        (CardProblemCodes.InvalidExpiry, "expMonth", "expMonth must be between 1 and 12."),
        (CardProblemCodes.InvalidExpiry, "expYear", "expYear is not a valid year."),
        (CardProblemCodes.CardExpired, "expYear", "The card has expired."),
        (CardProblemCodes.InvalidCvc, "cvc", "cvc must be three or four digits."),
    ];

    public TokeniseCardValidator(TimeProvider time)
    {
        ArgumentNullException.ThrowIfNull(time);

        // The domain finds the problems; each rule here reports one code on one field. No message repeats the input.
        foreach ((string code, string field, string message) in Rules)
        {
            RuleFor(
                    command => !CardDetails.Check(command.Number, command.ExpMonth, command.ExpYear, command.Cvc, Today(time))
                        .Contains(new CardProblem(code, field)),
                    message,
                    char.ToUpperInvariant(field[0]) + field[1..])
                .WithErrorCode(code);
        }
    }

    private static DateOnly Today(TimeProvider time) => DateOnly.FromDateTime(time.GetUtcNow().UtcDateTime);
}

public sealed class TokeniseCardHandler(
    IPaymentsDb db,
    IPaymentProvider provider,
    TimeProvider time,
    ILogger<TokeniseCardHandler> logger) : IRequestHandler<TokeniseCardCommand, TokeniseCardResult>
{
    public async Task<TokeniseCardResult> Handle(TokeniseCardCommand request, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);

        // The validator ran first; a caller that skipped it is told its card is wrong, not why in detail.
        CardDetails card = CardDetails.TryCreate(request.Number, request.ExpMonth, request.ExpYear, request.Cvc, DateOnly.FromDateTime(time.GetUtcNow().UtcDateTime))
            ?? throw ApiErrorException.Invalid(PaymentErrorCodes.PaymentMethodInvalid, "The card is not valid.");

        // A new key per request: a tokenisation has no id of the client's to be idempotent on, and a token made twice
        // for one card costs nothing.
        string key = "tokenise:" + Guid.CreateVersion7().ToString("N");

        switch (await provider.TokeniseAsync(card, key, cancellationToken))
        {
            case Tokenised tokenised:
                PaymentMethod method = PaymentMethod.Create(
                    tokenised.ProviderToken, tokenised.Brand, tokenised.Last4, tokenised.ExpMonth, tokenised.ExpYear, time.GetUtcNow());
                db.Methods.Add(method);
                await db.SaveChangesAsync(cancellationToken);
                PaymentsTelemetry.Tokenisations.Add(1, new KeyValuePair<string, object?>("result", "tokenised"));
                return new CardTokenised(new PaymentMethodView(method.Token, method.Brand, method.Last4, method.ExpMonth, method.ExpYear));

            case TokenRefused refused:
                PaymentsTelemetry.Tokenisations.Add(1, new KeyValuePair<string, object?>("result", "rejected"));
                return new CardRejected(refused.Code);

            case ProviderFailure failure:
                PaymentsTelemetry.Tokenisations.Add(1, new KeyValuePair<string, object?>("result", "unavailable"));
                Unavailable(logger, failure.Kind);
                return new TokenisationUnavailable();

            default:
                throw new UnreachableException("A tokenise outcome was never set.");
        }
    }

    private static void Unavailable(ILogger logger, FailureKind kind) => TokeniseLog.Unavailable(logger, kind);
}

internal static partial class TokeniseLog
{
    [LoggerMessage(Level = LogLevel.Warning, Message = "The provider could not tokenise a card: {Kind}")]
    public static partial void Unavailable(ILogger logger, FailureKind kind);
}
