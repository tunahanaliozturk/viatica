namespace Viatica.Payments.Domain.Cards;

/// <summary>
/// A card number, held only for as long as it takes to hand it to the provider. It prints as nothing, so a log line,
/// an exception message or a debugger watch that formats it shows no digits. The only way to read the digits is
/// <see cref="Reveal"/>, and the only caller that has a reason to is the provider adapter.
/// </summary>
public sealed class CardNumber
{
    private const int MinLength = 12;
    private const int MaxLength = 19;

    private readonly string _digits;

    private CardNumber(string digits) => _digits = digits;

    /// <summary>The last four digits, which are what a statement and a receipt show.</summary>
    public string Last4 => _digits[^4..];

    /// <summary>Visa, Mastercard, American Express, or <c>unknown</c>, from the leading digits.</summary>
    public string Brand => _digits switch
    {
        ['4', ..] => "visa",
        ['3', '4' or '7', ..] => "amex",
        ['5', >= '1' and <= '5', ..] => "mastercard",
        ['2', ..] when int.TryParse(_digits.AsSpan(0, 4), out int prefix) && prefix is >= 2221 and <= 2720 => "mastercard",
        _ => "unknown",
    };

    /// <summary>
    /// Reads a number as people type it: spaces and hyphens between groups are allowed, nothing else but digits is.
    /// Fails for a length no card has and for a number that breaks the Luhn check, which catches almost every typo.
    /// </summary>
    public static bool TryParse(string? text, out CardNumber? number)
    {
        number = null;
        if (string.IsNullOrWhiteSpace(text))
        {
            return false;
        }

        string digits = string.Concat(text.Where(static c => c is not (' ' or '-')));
        if (digits.Length is < MinLength or > MaxLength || !digits.All(char.IsAsciiDigit) || !PassesLuhn(digits))
        {
            return false;
        }

        number = new CardNumber(digits);
        return true;
    }

    /// <summary>The digits. Call it only to put them on the wire to the provider.</summary>
    public string Reveal() => _digits;

    public override string ToString() => "[card number]";

    private static bool PassesLuhn(string digits)
    {
        int sum = 0;
        bool doubleIt = false;
        for (int i = digits.Length - 1; i >= 0; i--)
        {
            int digit = digits[i] - '0';
            if (doubleIt)
            {
                digit *= 2;
                if (digit > 9)
                {
                    digit -= 9;
                }
            }

            sum += digit;
            doubleIt = !doubleIt;
        }

        return sum % 10 == 0;
    }
}
