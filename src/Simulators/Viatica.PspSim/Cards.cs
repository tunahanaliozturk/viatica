using System.Collections.Frozen;

namespace Viatica.PspSim;

/// <summary>What the provider does with a card. The number alone decides it, as Stripe's test cards do.</summary>
internal enum CardBehaviour
{
    /// <summary>Every operation succeeds.</summary>
    Approve,

    /// <summary>The authorisation is declined as a generic decline.</summary>
    Decline,

    /// <summary>The authorisation is declined for insufficient funds.</summary>
    InsufficientFunds,

    /// <summary>
    /// The first attempt of every operation does its work and then never answers in time. A repeat under the same
    /// idempotency key gets the answer that was recorded. This is the case a caller cannot tell from a failure.
    /// </summary>
    TimeoutPerformed,

    /// <summary>
    /// The first attempt of every operation hangs without doing anything. A repeat under the same idempotency key
    /// does the work. This is the timeout that really did not happen.
    /// </summary>
    TimeoutNotPerformed,
}

/// <summary>A card number the sim accepts, and the behaviour it stands for.</summary>
internal sealed record TestCard(string Number, string Brand, string Funding, CardBehaviour Behaviour);

/// <summary>
/// The test cards. Only these numbers are accepted, as Stripe's test mode accepts only its own: a real card number
/// typed into a demo is refused, not remembered. The Stripe numbers keep their meaning; the two timeout cards are
/// ours, in a range Stripe does not use.
/// </summary>
internal static class Cards
{
    public const string TimeoutPerformedNumber = "4000009990000013";
    public const string TimeoutNotPerformedNumber = "4000009990000021";

    private static readonly FrozenDictionary<string, TestCard> ByNumber = new TestCard[]
    {
        new("4242424242424242", "visa", "credit", CardBehaviour.Approve),
        new("5555555555554444", "mastercard", "credit", CardBehaviour.Approve),
        new("378282246310005", "amex", "credit", CardBehaviour.Approve),
        new("4000000000000002", "visa", "credit", CardBehaviour.Decline),
        new("4000000000009995", "visa", "credit", CardBehaviour.InsufficientFunds),
        new(TimeoutPerformedNumber, "visa", "credit", CardBehaviour.TimeoutPerformed),
        new(TimeoutNotPerformedNumber, "visa", "credit", CardBehaviour.TimeoutNotPerformed),
    }.ToFrozenDictionary(static card => card.Number, StringComparer.Ordinal);

    public static IReadOnlyCollection<TestCard> All => ByNumber.Values;

    public static TestCard? Find(string number) => ByNumber.GetValueOrDefault(number);

    /// <summary>The Luhn checksum every card number carries. A typo almost always breaks it.</summary>
    public static bool PassesLuhn(string number)
    {
        if (number.Length < 12 || !number.All(char.IsAsciiDigit))
        {
            return false;
        }

        int sum = 0;
        bool doubleIt = false;
        for (int i = number.Length - 1; i >= 0; i--)
        {
            int digit = number[i] - '0';
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
