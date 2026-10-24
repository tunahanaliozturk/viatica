using System.Globalization;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Viatica.Kernel;

/// <summary>
/// Writes <see cref="Money"/> as <c>{ "amount": "312.00", "currency": "EUR" }</c> and reads only that shape.
/// </summary>
/// <remarks>
/// The amount is a string so no client parses it into a binary float. Reading is strict on purpose: a number
/// instead of a string, a missing or repeated property, an unknown property, or the wrong count of decimal places
/// for the currency is rejected. A lenient reader would accept <c>"312.1"</c> today and a client would start to
/// depend on it.
/// </remarks>
public sealed class MoneyJsonConverter : JsonConverter<Money>
{
    // Larger than any real price; stops a hostile payload from making the parser chew on a megabyte of digits.
    private const int MaxAmountLength = 40;

    public override Money Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
    {
        if (reader.TokenType != JsonTokenType.StartObject)
        {
            throw new JsonException("Money must be an object with 'amount' and 'currency'.");
        }

        string? amount = null;
        string? currency = null;

        while (reader.Read() && reader.TokenType != JsonTokenType.EndObject)
        {
            if (reader.TokenType != JsonTokenType.PropertyName)
            {
                throw new JsonException("Money must be an object with 'amount' and 'currency'.");
            }

            bool isAmount = reader.ValueTextEquals("amount"u8);
            bool isCurrency = !isAmount && reader.ValueTextEquals("currency"u8);
            if (!isAmount && !isCurrency)
            {
                throw new JsonException($"Money has no property '{reader.GetString()}'.");
            }

            reader.Read();
            if (reader.TokenType != JsonTokenType.String)
            {
                throw new JsonException(isAmount
                    ? "Money 'amount' must be a string such as \"312.00\", not a number."
                    : "Money 'currency' must be a string.");
            }

            if ((isAmount && amount is not null) || (isCurrency && currency is not null))
            {
                throw new JsonException("Money properties may appear only once.");
            }

            if (isAmount)
            {
                amount = reader.GetString();
            }
            else
            {
                currency = reader.GetString();
            }
        }

        if (amount is null || currency is null)
        {
            throw new JsonException("Money needs both 'amount' and 'currency'.");
        }

        if (currency.Length != 3 || !currency.All(char.IsAsciiLetterUpper))
        {
            throw new JsonException($"'{currency}' is not an ISO 4217 currency code.");
        }

        int minorUnits = Money.MinorUnitsOf(currency);
        if (!TryParseAmount(amount, minorUnits, out decimal value))
        {
            throw new JsonException(minorUnits == 0
                ? $"Money 'amount' for {currency} must be a whole number such as \"312\"."
                : $"Money 'amount' for {currency} must have exactly {minorUnits} decimal places, such as \"312.{new string('0', minorUnits)}\".");
        }

        return new Money(value, currency);
    }

    public override void Write(Utf8JsonWriter writer, Money value, JsonSerializerOptions options)
    {
        ArgumentNullException.ThrowIfNull(writer);
        ArgumentNullException.ThrowIfNull(value);

        writer.WriteStartObject();
        writer.WriteString("amount"u8, value.FormatAmount());
        writer.WriteString("currency"u8, value.Currency);
        writer.WriteEndObject();
    }

    /// <summary>
    /// Accepts <c>-?(0|[1-9][0-9]*)</c> followed, for a currency with minor units, by a point and exactly that many
    /// digits. No exponent, no grouping, no whitespace, no leading plus or zeros.
    /// </summary>
    internal static bool TryParseAmount(string text, int minorUnits, out decimal value)
    {
        value = 0m;
        if (text.Length is 0 or > MaxAmountLength)
        {
            return false;
        }

        ReadOnlySpan<char> span = text;
        int start = span[0] == '-' ? 1 : 0;
        int point = span.IndexOf('.');
        ReadOnlySpan<char> whole = point < 0 ? span[start..] : span[start..point];

        if (whole.Length == 0 || !whole.ContainsOnlyAsciiDigits() || (whole.Length > 1 && whole[0] == '0'))
        {
            return false;
        }

        if (minorUnits == 0 ? point >= 0 : point < 0)
        {
            return false;
        }

        if (point >= 0)
        {
            ReadOnlySpan<char> fraction = span[(point + 1)..];
            if (fraction.Length != minorUnits || !fraction.ContainsOnlyAsciiDigits())
            {
                return false;
            }
        }

        return decimal.TryParse(span, NumberStyles.AllowLeadingSign | NumberStyles.AllowDecimalPoint,
            CultureInfo.InvariantCulture, out value);
    }
}

file static class SpanDigits
{
    public static bool ContainsOnlyAsciiDigits(this ReadOnlySpan<char> span) =>
        !span.ContainsAnyExceptInRange('0', '9');
}
