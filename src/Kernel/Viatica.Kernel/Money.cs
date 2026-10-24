using System.Globalization;
using System.Text.Json.Serialization;
using Moongazing.OrionGuard.Core;

namespace Viatica.Kernel;

/// <summary>
/// An amount in one ISO 4217 currency. The platform prices everything in EUR for v1, but the type is general so
/// a second currency is a configuration change, not a new type.
/// </summary>
/// <remarks>
/// The amount may not carry more decimal places than the currency's minor unit, so <c>312.005 EUR</c> cannot
/// exist. That keeps the wire format lossless: what is written as <c>"312.00"</c> is exactly what is held.
/// Arithmetic between two currencies throws instead of guessing a rate, because a silent mix is a pricing bug.
/// </remarks>
[JsonConverter(typeof(MoneyJsonConverter))]
public sealed record Money : IComparable<Money>
{
    public const string Euro = "EUR";

    public Money(decimal amount, string currency)
    {
        Ensure.That(currency, nameof(currency)).NotNull().Matches("^[A-Z]{3}$");

        int minorUnits = MinorUnitsOf(currency);
        if (decimal.Round(amount, minorUnits) != amount)
        {
            throw new ArgumentException(
                $"{currency} has {minorUnits} decimal places; {amount.ToString(CultureInfo.InvariantCulture)} has more.",
                nameof(amount));
        }

        Amount = amount;
        Currency = currency;
    }

    public decimal Amount { get; }

    /// <summary>The ISO 4217 alphabetic code, upper case.</summary>
    public string Currency { get; }

    /// <summary>Decimal places of the currency's minor unit: 2 for EUR, 0 for JPY, 3 for KWD.</summary>
    public int MinorUnits => MinorUnitsOf(Currency);

    public static Money Eur(decimal amount) => new(amount, Euro);

    public static Money Zero(string currency) => new(0m, currency);

    public static Money operator +(Money left, Money right)
    {
        ArgumentNullException.ThrowIfNull(left);
        ArgumentNullException.ThrowIfNull(right);
        EnsureSameCurrency(left, right);
        return new Money(left.Amount + right.Amount, left.Currency);
    }

    public static Money operator -(Money left, Money right)
    {
        ArgumentNullException.ThrowIfNull(left);
        ArgumentNullException.ThrowIfNull(right);
        EnsureSameCurrency(left, right);
        return new Money(left.Amount - right.Amount, left.Currency);
    }

    // Multiplying by a count (nights, days, passengers) cannot add decimal places, so it stays exact. Division and
    // percentages need a rounding rule that belongs to the caller, so there is no operator for them.
    public static Money operator *(Money money, int count)
    {
        ArgumentNullException.ThrowIfNull(money);
        return new Money(money.Amount * count, money.Currency);
    }

    public static bool operator <(Money left, Money right) => Compare(left, right) < 0;

    public static bool operator >(Money left, Money right) => Compare(left, right) > 0;

    public static bool operator <=(Money left, Money right) => Compare(left, right) <= 0;

    public static bool operator >=(Money left, Money right) => Compare(left, right) >= 0;

    public static Money Add(Money left, Money right) => left + right;

    public static Money Subtract(Money left, Money right) => left - right;

    public static Money Multiply(Money money, int count) => money * count;

    public int CompareTo(Money? other)
    {
        if (other is null)
        {
            return 1;
        }

        EnsureSameCurrency(this, other);
        return Amount.CompareTo(other.Amount);
    }

    /// <summary>The amount with exactly the currency's minor unit digits, invariant culture: <c>312.00</c>.</summary>
    public string FormatAmount() => Amount.ToString(AmountFormat(MinorUnits), CultureInfo.InvariantCulture);

    public override string ToString() => $"{FormatAmount()} {Currency}";

    /// <summary>
    /// ISO 4217 minor units. Most currencies have two; the exceptions are listed. A code the table does not know
    /// gets two, which is right for every active currency not named here.
    /// </summary>
    public static int MinorUnitsOf(string currency) => currency switch
    {
        "BIF" or "CLP" or "DJF" or "GNF" or "ISK" or "JPY" or "KMF" or "KRW" or "PYG" or "RWF" or "UGX" or "UYI"
            or "VND" or "VUV" or "XAF" or "XOF" or "XPF" => 0,
        "BHD" or "IQD" or "JOD" or "KWD" or "LYD" or "OMR" or "TND" => 3,
        "CLF" or "UYW" => 4,
        _ => 2,
    };

    internal static string AmountFormat(int minorUnits) => minorUnits switch
    {
        0 => "F0",
        2 => "F2",
        3 => "F3",
        _ => "F" + minorUnits.ToString(CultureInfo.InvariantCulture),
    };

    private static int Compare(Money left, Money right)
    {
        ArgumentNullException.ThrowIfNull(left);
        return left.CompareTo(right);
    }

    private static void EnsureSameCurrency(Money left, Money right)
    {
        if (!string.Equals(left.Currency, right.Currency, StringComparison.Ordinal))
        {
            throw new InvalidOperationException(
                $"Cannot combine {left.Currency} and {right.Currency}. Convert one of them first.");
        }
    }
}
