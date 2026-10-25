namespace Viatica.Payments.Domain.Cards;

/// <summary>One thing wrong with a card the traveller typed: a code to switch on and the request field it is about.</summary>
public sealed record CardProblem(string Code, string Field);

public static class CardProblemCodes
{
    public const string InvalidCardNumber = nameof(InvalidCardNumber);
    public const string InvalidExpiry = nameof(InvalidExpiry);
    public const string CardExpired = nameof(CardExpired);
    public const string InvalidCvc = nameof(InvalidCvc);
}

/// <summary>
/// A card as the traveller typed it, checked. Nothing in it prints: <see cref="ToString"/> says what it is and not
/// what it holds, so formatting it into a log or an exception cannot leak the number or the security code.
/// </summary>
public sealed class CardDetails
{
    private const int LatestExpiryYears = 20;

    private CardDetails(CardNumber number, int expMonth, int expYear, string? cvc)
    {
        Number = number;
        ExpMonth = expMonth;
        ExpYear = expYear;
        Cvc = cvc;
    }

    public CardNumber Number { get; }

    public int ExpMonth { get; }

    public int ExpYear { get; }

    /// <summary>The security code, or null when the card has none to give. Never stored.</summary>
    public string? Cvc { get; }

    /// <summary>
    /// Checks every field and reports every problem, so a form can mark them all at once. Messages built from these
    /// codes never repeat what was typed.
    /// </summary>
    public static IReadOnlyList<CardProblem> Check(string? number, int expMonth, int expYear, string? cvc, DateOnly today)
    {
        List<CardProblem> problems = [];

        if (!CardNumber.TryParse(number, out _))
        {
            problems.Add(new CardProblem(CardProblemCodes.InvalidCardNumber, "number"));
        }

        if (expMonth is < 1 or > 12)
        {
            problems.Add(new CardProblem(CardProblemCodes.InvalidExpiry, "expMonth"));
        }

        if (expYear < 2000 || expYear > today.Year + LatestExpiryYears)
        {
            problems.Add(new CardProblem(CardProblemCodes.InvalidExpiry, "expYear"));
        }
        else if (expMonth is >= 1 and <= 12 && (expYear, expMonth).CompareTo((today.Year, today.Month)) < 0)
        {
            // A card is good through the last day of its expiry month.
            problems.Add(new CardProblem(CardProblemCodes.CardExpired, "expYear"));
        }

        if (cvc is not null && (cvc.Length is < 3 or > 4 || !cvc.All(char.IsAsciiDigit)))
        {
            problems.Add(new CardProblem(CardProblemCodes.InvalidCvc, "cvc"));
        }

        return problems;
    }

    /// <summary>Builds the card, or returns null when <see cref="Check"/> would report anything.</summary>
    public static CardDetails? TryCreate(string? number, int expMonth, int expYear, string? cvc, DateOnly today) =>
        Check(number, expMonth, expYear, cvc, today).Count == 0 && CardNumber.TryParse(number, out CardNumber? parsed)
            ? new CardDetails(parsed!, expMonth, expYear, cvc)
            : null;

    public override string ToString() => "[card details]";
}
