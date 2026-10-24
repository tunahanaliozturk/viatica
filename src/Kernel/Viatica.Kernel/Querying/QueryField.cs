using System.Globalization;
using System.Linq.Expressions;

namespace Viatica.Kernel.Querying;

internal enum FieldKind
{
    String,
    Integer,
    Number,
    Boolean,
    Date,
    Instant,
    Guid,
    Enum,
}

/// <summary>One declared field: how to reach it on a row, its type, and how its values are written and read.</summary>
internal sealed class QueryField
{
    private QueryField(
        string name, Expression member, Type type, Type valueType, FieldKind kind, bool sortable,
        Func<object, object?>? getter)
    {
        Name = name;
        Member = member;
        Type = type;
        ValueType = valueType;
        Kind = kind;
        Sortable = sortable;
        Getter = getter;
    }

    public string Name { get; }

    /// <summary>The selector body, bound to the shared row parameter.</summary>
    public Expression Member { get; }

    /// <summary>The member type, nullable when the member is.</summary>
    public Type Type { get; }

    /// <summary>The member type without <see cref="Nullable{T}"/>.</summary>
    public Type ValueType { get; }

    public FieldKind Kind { get; }

    public bool Sortable { get; }

    /// <summary>Reads the field from a materialised row, to record the keyset position. Sortable fields only.</summary>
    public Func<object, object?>? Getter { get; }

    /// <summary>Booleans, ids and enums have no meaningful order for a caller, so they take equality only.</summary>
    public bool IsOrdered => Kind is not (FieldKind.Boolean or FieldKind.Guid or FieldKind.Enum);

    public static QueryField Create(
        string name, Expression member, Type type, bool sortable, ParameterExpression parameter)
    {
        Type valueType = Nullable.GetUnderlyingType(type) ?? type;
        FieldKind kind = KindOf(valueType)
            ?? throw new ArgumentException($"Field '{name}' has type {type.Name}, which cannot be filtered on.", nameof(type));

        if (sortable && (type != valueType || kind is FieldKind.Boolean or FieldKind.Enum))
        {
            // Keyset paging compares the last row's values with "greater than"; a NULL or an enum stored as text
            // breaks that comparison in ways the database and the caller would disagree on.
            throw new ArgumentException(
                $"Field '{name}' cannot be sortable: keyset paging needs a non-nullable string, number, date, instant or id.",
                nameof(sortable));
        }

        Func<object, object?>? getter = null;
        if (sortable)
        {
            ParameterExpression row = Expression.Parameter(typeof(object), "row");
            Expression body = new Replace(parameter, Expression.Convert(row, parameter.Type)).Visit(member);
            getter = Expression.Lambda<Func<object, object?>>(Expression.Convert(body, typeof(object)), row).Compile();
        }

        return new QueryField(name, member, type, valueType, kind, sortable, getter);
    }

    /// <summary>How a value of this field is described in an error message.</summary>
    public string Expected => Kind switch
    {
        FieldKind.String => "a quoted string such as 'lisbon'",
        FieldKind.Integer => "a whole number such as 42",
        FieldKind.Number => "a number such as 312.50",
        FieldKind.Boolean => "true or false",
        FieldKind.Date => "a date such as 2026-12-01",
        FieldKind.Instant => "an instant with an offset such as 2026-12-01T14:00:00Z",
        FieldKind.Guid => "a quoted id such as '0b5e3c2a-4f1d-4c8e-9a3b-2d7f6e1c0a94'",
        FieldKind.Enum => "one of " + string.Join(", ", Enum.GetNames(ValueType).Select(static name => $"'{name}'")),
        _ => throw new InvalidOperationException(),
    };

    /// <summary>The kind as a noun for messages: "Field 'checkIn' is a date".</summary>
    public string Noun => Kind switch
    {
        FieldKind.String => "a string",
        FieldKind.Integer => "a whole number",
        FieldKind.Number => "a number",
        FieldKind.Boolean => "a boolean",
        FieldKind.Date => "a date",
        FieldKind.Instant => "an instant",
        FieldKind.Guid => "an id",
        FieldKind.Enum => "an enumeration",
        _ => throw new InvalidOperationException(),
    };

    /// <summary>Writes a keyset value in a form <see cref="TryParsePosition"/> reads back exactly.</summary>
    public string FormatPosition(object? value) => value switch
    {
        string text => text,
        DateOnly date => date.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture),
        DateTimeOffset instant => instant.UtcDateTime.ToString("O", CultureInfo.InvariantCulture),
        double number => number.ToString("R", CultureInfo.InvariantCulture),
        float number => number.ToString("R", CultureInfo.InvariantCulture),
        IFormattable formattable => formattable.ToString(null, CultureInfo.InvariantCulture),
        _ => throw new InvalidOperationException($"Field '{Name}' has no value to page from."),
    };

    public bool TryParsePosition(string text, out object value)
    {
        value = text;
        switch (Kind)
        {
            case FieldKind.String:
                return true;
            case FieldKind.Date:
                return Literals.TryDate(text, out value);
            case FieldKind.Instant:
                return Literals.TryInstant(text, out value);
            case FieldKind.Guid:
                return Literals.TryGuid(text, out value);
            case FieldKind.Number when ValueType != typeof(decimal):
                bool parsed = double.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out double number);
                value = ValueType == typeof(float) ? (float)number : number;
                return parsed;
            default:
                return Literals.TryNumber(text, ValueType, out value);
        }
    }

    private static FieldKind? KindOf(Type type) =>
        type == typeof(string) ? FieldKind.String
        : type == typeof(int) || type == typeof(long) || type == typeof(short) ? FieldKind.Integer
        : type == typeof(decimal) || type == typeof(double) || type == typeof(float) ? FieldKind.Number
        : type == typeof(bool) ? FieldKind.Boolean
        : type == typeof(DateOnly) ? FieldKind.Date
        : type == typeof(DateTimeOffset) ? FieldKind.Instant
        : type == typeof(Guid) ? FieldKind.Guid
        : type.IsEnum ? FieldKind.Enum
        : null;

    private sealed class Replace(ParameterExpression from, Expression to) : ExpressionVisitor
    {
        protected override Expression VisitParameter(ParameterExpression node) => node == from ? to : node;
    }
}

/// <summary>Literal forms shared by the filter grammar and the keyset position.</summary>
internal static class Literals
{
    private static readonly string[] InstantFormats =
    [
        "yyyy-MM-dd'T'HH:mm:ssK",
        "yyyy-MM-dd'T'HH:mm:ss.FFFFFFFK",
        "yyyy-MM-dd'T'HH:mmK",
    ];

    public static bool TryDate(string text, out object value)
    {
        bool parsed = DateOnly.TryParseExact(text, "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out DateOnly date);
        value = date;
        return parsed;
    }

    /// <summary>
    /// An RFC 3339 instant with an explicit offset, converted to UTC. A local time without an offset is refused:
    /// the server would have to guess the zone. UTC also matters to Npgsql, which only writes offset zero to a
    /// <c>timestamptz</c> parameter.
    /// </summary>
    public static bool TryInstant(string text, out object value)
    {
        value = default(DateTimeOffset);
        bool hasOffset = text.EndsWith('Z') || (text.Length > 6 && text[^6] is '+' or '-' && text[^3] == ':');
        if (!hasOffset || !DateTimeOffset.TryParseExact(
            text, InstantFormats, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal, out DateTimeOffset instant))
        {
            return false;
        }

        value = instant.ToUniversalTime();
        return true;
    }

    public static bool TryGuid(string text, out object value)
    {
        bool parsed = Guid.TryParseExact(text, "D", out Guid id);
        value = id;
        return parsed;
    }

    /// <summary>
    /// <c>-?[0-9]+</c> for the integer types, with an optional fraction for decimal, double and float. No exponent
    /// and no grouping, so one value has one spelling.
    /// </summary>
    public static bool TryNumber(string text, Type type, out object value)
    {
        value = 0;
        ReadOnlySpan<char> span = text;
        ReadOnlySpan<char> digits = span.StartsWith('-') ? span[1..] : span;
        int point = digits.IndexOf('.');
        bool integral = type == typeof(int) || type == typeof(long) || type == typeof(short);

        if (digits.IsEmpty || (integral && point >= 0) || point == 0 || point == digits.Length - 1)
        {
            return false;
        }

        foreach (char c in digits)
        {
            if (!char.IsAsciiDigit(c) && c != '.')
            {
                return false;
            }
        }

        if (digits.Count('.') > 1)
        {
            return false;
        }

        CultureInfo invariant = CultureInfo.InvariantCulture;
        const NumberStyles integer = NumberStyles.AllowLeadingSign;
        const NumberStyles fraction = NumberStyles.AllowLeadingSign | NumberStyles.AllowDecimalPoint;
        bool ok;
        if (type == typeof(int))
        {
            ok = int.TryParse(span, integer, invariant, out int parsed);
            value = parsed;
        }
        else if (type == typeof(long))
        {
            ok = long.TryParse(span, integer, invariant, out long parsed);
            value = parsed;
        }
        else if (type == typeof(short))
        {
            ok = short.TryParse(span, integer, invariant, out short parsed);
            value = parsed;
        }
        else if (type == typeof(decimal))
        {
            ok = decimal.TryParse(span, fraction, invariant, out decimal parsed);
            value = parsed;
        }
        else if (type == typeof(double))
        {
            ok = double.TryParse(span, fraction, invariant, out double parsed) && double.IsFinite(parsed);
            value = parsed;
        }
        else
        {
            ok = float.TryParse(span, fraction, invariant, out float parsed) && float.IsFinite(parsed);
            value = parsed;
        }

        return ok;
    }
}
