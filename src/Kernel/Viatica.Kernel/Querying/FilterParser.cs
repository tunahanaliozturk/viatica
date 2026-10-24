using System.Linq.Expressions;
using System.Reflection;

namespace Viatica.Kernel.Querying;

/// <summary>
/// The allowlisted <c>$filter</c> and <c>$orderby</c> grammar:
/// <code>
/// filter  := predicate ( "and" predicate )*
/// predicate := field ( "eq" | "ne" | "gt" | "ge" | "lt" | "le" ) literal
///            | field "in" "(" literal ( "," literal )* ")"
/// literal := 'string' | number | true | false | 2026-12-01 | 2026-12-01T14:00:00Z
/// orderby := field [ "asc" | "desc" ] ( "," field [ "asc" | "desc" ] )*
/// </code>
/// Keywords are lower case and field names are matched exactly, as OData does. There is no <c>or</c>, no
/// <c>not</c>, no functions and no parentheses outside <c>in</c>: each of those would let a caller build a query
/// the indexes were not designed for.
/// </summary>
/// <remarks>
/// Values reach the expression tree as members of a closure object, not as constants, so EF Core sends them as
/// SQL parameters. That keeps one cached query plan per filter shape instead of one per value, and no caller text
/// is ever spliced into SQL.
/// </remarks>
internal static class FilterParser
{
    public const int MaxLength = 2048;
    public const int MaxPredicates = 16;
    public const int MaxInValues = 50;
    public const int MaxOrderByFields = 4;

    private static readonly MethodInfo StringCompare =
        typeof(string).GetMethod(nameof(string.Compare), [typeof(string), typeof(string)])!;

    private static readonly MethodInfo EnumerableContains =
        typeof(Enumerable).GetMethods().Single(static method =>
            method.Name == nameof(Enumerable.Contains) && method.GetParameters().Length == 2);

    public static Expression<Func<T, bool>> ParseFilter<T>(QueryFields<T> fields, string text)
    {
        var lexer = new Lexer(text, ApiErrorCode.InvalidFilter, "$filter");
        if (text.Length > MaxLength)
        {
            throw lexer.Fail($"The $filter is longer than {MaxLength} characters.");
        }

        Token token = lexer.Next();
        if (token.Kind == TokenKind.End)
        {
            throw lexer.Fail("The $filter is empty. Leave the parameter out to get every item.");
        }

        Expression? body = null;
        int predicates = 0;
        while (true)
        {
            if (++predicates > MaxPredicates)
            {
                throw lexer.Fail($"The $filter has more than {MaxPredicates} conditions.");
            }

            Expression predicate = ParsePredicate(fields, ref lexer, token);
            body = body is null ? predicate : Expression.AndAlso(body, predicate);

            token = lexer.Next();
            if (token.Kind == TokenKind.End)
            {
                break;
            }

            if (!token.Is("and"))
            {
                throw lexer.Fail($"Expected 'and' or the end of the filter at position {token.Position}, found {token}.");
            }

            token = lexer.Next();
        }

        return Expression.Lambda<Func<T, bool>>(body, fields.Parameter);
    }

    public static SortKey[] ParseOrderBy<T>(QueryFields<T> fields, string? text)
    {
        QueryField key = fields.KeyField!;
        if (text is null)
        {
            return [new SortKey(key, Descending: false)];
        }

        var lexer = new Lexer(text, ApiErrorCode.InvalidOrderBy, "$orderby");
        if (text.Length > MaxLength)
        {
            throw lexer.Fail($"The $orderby is longer than {MaxLength} characters.");
        }

        var order = new List<SortKey>();
        Token token = lexer.Next();
        if (token.Kind == TokenKind.End)
        {
            throw lexer.Fail("The $orderby is empty. Leave the parameter out to get the default order.");
        }

        while (true)
        {
            if (token.Kind != TokenKind.Word)
            {
                throw lexer.Fail($"Expected a field name at position {token.Position}, found {token}.");
            }

            if (!fields.TryGet(token.Text, out QueryField field) || !field.Sortable)
            {
                throw lexer.Fail(
                    $"Cannot sort on '{Lexer.Clip(token.Text)}'. Fields you can sort on: {fields.Names(static f => f.Sortable)}.");
            }

            if (order.Exists(sort => sort.Field == field))
            {
                throw lexer.Fail($"Field '{field.Name}' appears twice in $orderby.");
            }

            if (order.Count == MaxOrderByFields)
            {
                throw lexer.Fail($"The $orderby has more than {MaxOrderByFields} fields.");
            }

            token = lexer.Next();
            bool descending = token.Is("desc");
            if (descending || token.Is("asc"))
            {
                token = lexer.Next();
            }

            order.Add(new SortKey(field, descending));

            if (token.Kind == TokenKind.End)
            {
                break;
            }

            if (token.Kind != TokenKind.Comma)
            {
                throw lexer.Fail($"Expected ',', 'asc', 'desc' or the end at position {token.Position}, found {token}.");
            }

            token = lexer.Next();
        }

        // The key last makes the ordering total, which keyset paging needs to never skip or repeat a row.
        if (!order.Exists(sort => sort.Field == key))
        {
            order.Add(new SortKey(key, Descending: false));
        }

        return [.. order];
    }

    /// <summary>Wraps a value so EF Core treats it as a parameter. See the class remarks.</summary>
    internal static Expression Parameterize(object? value, Type type)
    {
        object box = Activator.CreateInstance(typeof(Box<>).MakeGenericType(type), value)!;
        return Expression.Property(Expression.Constant(box), nameof(Box<>.Value));
    }

    /// <summary>
    /// <c>field op value</c>. Strings and ids have no C# comparison operators, so ordering on them goes through
    /// <c>Compare(a, b) op 0</c>, which EF Core translates to a plain SQL comparison.
    /// </summary>
    internal static Expression Compare(QueryField field, ExpressionType op, Expression value)
    {
        bool ordering = op is not (ExpressionType.Equal or ExpressionType.NotEqual);
        if (ordering && field.Kind == FieldKind.String)
        {
            return Expression.MakeBinary(op, Expression.Call(StringCompare, field.Member, value), Expression.Constant(0));
        }

        if (ordering && field.Kind == FieldKind.Guid)
        {
            MethodInfo compareTo = typeof(Guid).GetMethod(nameof(Guid.CompareTo), [typeof(Guid)])!;
            return Expression.MakeBinary(op, Expression.Call(field.Member, compareTo, value), Expression.Constant(0));
        }

        return Expression.MakeBinary(op, field.Member, value);
    }

    private static Expression ParsePredicate<T>(QueryFields<T> fields, ref Lexer lexer, Token token)
    {
        if (token.Kind != TokenKind.Word)
        {
            throw lexer.Fail($"Expected a field name at position {token.Position}, found {token}.");
        }

        if (!fields.TryGet(token.Text, out QueryField field))
        {
            throw lexer.Fail(
                $"Unknown field '{Lexer.Clip(token.Text)}' at position {token.Position}. Fields you can filter on: {fields.Names(static _ => true)}.");
        }

        Token op = lexer.Next();
        ExpressionType? comparison = op.Kind != TokenKind.Word ? null : op.Text switch
        {
            "eq" => ExpressionType.Equal,
            "ne" => ExpressionType.NotEqual,
            "gt" => ExpressionType.GreaterThan,
            "ge" => ExpressionType.GreaterThanOrEqual,
            "lt" => ExpressionType.LessThan,
            "le" => ExpressionType.LessThanOrEqual,
            _ => null,
        };

        if (op.Is("in"))
        {
            return ParseIn(field, ref lexer);
        }

        if (comparison is null)
        {
            throw op.Kind == TokenKind.End
                ? lexer.Fail($"Expected an operator after '{field.Name}'.")
                : lexer.Fail($"Unsupported operator {op} at position {op.Position}. Use eq, ne, gt, ge, lt, le or in.");
        }

        if (comparison is not (ExpressionType.Equal or ExpressionType.NotEqual) && !field.IsOrdered)
        {
            throw lexer.Fail($"Field '{field.Name}' is {field.Noun}; it supports eq, ne and in only.");
        }

        object value = ReadLiteral(field, ref lexer, lexer.Next());
        return Compare(field, comparison.Value, Parameterize(value, field.Type));
    }

    private static MethodCallExpression ParseIn(QueryField field, ref Lexer lexer)
    {
        Token token = lexer.Next();
        if (token.Kind != TokenKind.OpenParen)
        {
            throw lexer.Fail($"Expected '(' after 'in' at position {token.Position}, found {token}.");
        }

        var values = new List<object>();
        while (true)
        {
            if (values.Count == MaxInValues)
            {
                throw lexer.Fail($"An 'in' list may hold at most {MaxInValues} values.");
            }

            values.Add(ReadLiteral(field, ref lexer, lexer.Next()));

            token = lexer.Next();
            if (token.Kind == TokenKind.CloseParen)
            {
                break;
            }

            if (token.Kind != TokenKind.Comma)
            {
                throw lexer.Fail($"Expected ',' or ')' at position {token.Position}, found {token}.");
            }
        }

        var array = Array.CreateInstance(field.Type, values.Count);
        for (int i = 0; i < values.Count; i++)
        {
            array.SetValue(values[i], i);
        }

        return Expression.Call(
            EnumerableContains.MakeGenericMethod(field.Type),
            Parameterize(array, array.GetType()),
            field.Member);
    }

    private static object ReadLiteral(QueryField field, ref Lexer lexer, Token token)
    {
        object? value = null;
        bool ok = (field.Kind, token.Kind) switch
        {
            (FieldKind.String, TokenKind.String) => Accept(token.Text, out value),
            (FieldKind.Guid, TokenKind.String) => Literals.TryGuid(token.Text, out value!),
            (FieldKind.Enum, TokenKind.String) => TryEnum(field.ValueType, token.Text, out value),
            (FieldKind.Boolean, TokenKind.Word) when token.Is("true") || token.Is("false") => Accept(token.Is("true"), out value),
            (FieldKind.Integer or FieldKind.Number, TokenKind.Literal) => Literals.TryNumber(token.Text, field.ValueType, out value!),
            (FieldKind.Date, TokenKind.Literal) => Literals.TryDate(token.Text, out value!),
            (FieldKind.Instant, TokenKind.Literal) => Literals.TryInstant(token.Text, out value!),
            _ => false,
        };

        if (!ok)
        {
            string found = token.Kind == TokenKind.End ? "the end of the filter" : token.ToString();
            throw lexer.Fail($"Field '{field.Name}' is {field.Noun}; expected {field.Expected} at position {token.Position}, found {found}.");
        }

        return value!;
    }

    private static bool Accept(object input, out object? value)
    {
        value = input;
        return true;
    }

    // Enum.TryParse also accepts "1" and "A, B"; only an exact member name is a valid literal here.
    private static bool TryEnum(Type type, string text, out object? value)
    {
        value = null;
        if (!Enum.GetNames(type).Contains(text, StringComparer.Ordinal))
        {
            return false;
        }

        value = Enum.Parse(type, text);
        return true;
    }

    private sealed class Box<TValue>(TValue value)
    {
        public TValue Value { get; } = value;
    }
}

internal enum TokenKind
{
    End,
    Word,
    String,
    Literal,
    OpenParen,
    CloseParen,
    Comma,
}

internal readonly record struct Token(TokenKind Kind, string Text, int Position)
{
    public bool Is(string word) => Kind == TokenKind.Word && string.Equals(Text, word, StringComparison.Ordinal);

    public override string ToString() => Kind == TokenKind.End ? "the end" : $"'{Lexer.Clip(Text)}'";
}

internal struct Lexer(string text, string code, string target)
{
    private int position;

    public static string Clip(string text) => text.Length <= 40 ? text : string.Concat(text.AsSpan(0, 40), "...");

    public readonly ApiErrorException Fail(string message) => ApiErrorException.Invalid(code, message, target);

    public Token Next()
    {
        while (position < text.Length && text[position] == ' ')
        {
            position++;
        }

        int start = position;
        if (position >= text.Length)
        {
            return new Token(TokenKind.End, string.Empty, start);
        }

        char c = text[position];
        switch (c)
        {
            case '(':
                position++;
                return new Token(TokenKind.OpenParen, "(", start);
            case ')':
                position++;
                return new Token(TokenKind.CloseParen, ")", start);
            case ',':
                position++;
                return new Token(TokenKind.Comma, ",", start);
            case '\'':
                return ReadString(start);
        }

        if (char.IsAsciiLetter(c))
        {
            while (position < text.Length && char.IsAsciiLetterOrDigit(text[position]))
            {
                position++;
            }

            return new Token(TokenKind.Word, text[start..position], start);
        }

        if (char.IsAsciiDigit(c) || c == '-')
        {
            // Numbers, dates and instants: everything up to a delimiter, classified by the field's type later.
            while (position < text.Length && text[position] is not (' ' or '(' or ')' or ',' or '\''))
            {
                position++;
            }

            return new Token(TokenKind.Literal, text[start..position], start);
        }

        throw Fail($"Unexpected character '{c}' at position {start}.");
    }

    // OData strings: single quotes, with a doubled quote standing for one quote.
    private Token ReadString(int start)
    {
        var value = new System.Text.StringBuilder();
        position++;
        while (position < text.Length)
        {
            char c = text[position++];
            if (c != '\'')
            {
                value.Append(c);
                continue;
            }

            if (position < text.Length && text[position] == '\'')
            {
                value.Append('\'');
                position++;
                continue;
            }

            return new Token(TokenKind.String, value.ToString(), start);
        }

        throw Fail($"The string starting at position {start} is not closed with a quote.");
    }
}

/// <summary>Keyset ("seek") paging: where the next page starts, expressed as a predicate the database can index.</summary>
internal static class Keyset
{
    /// <summary>Reads the ordering fields off the last row of a page.</summary>
    public static string[] Position<T>(SortKey[] order, T row)
    {
        var position = new string[order.Length];
        for (int i = 0; i < order.Length; i++)
        {
            QueryField field = order[i].Field;
            position[i] = field.FormatPosition(field.Getter!(row!));
        }

        return position;
    }

    /// <summary>
    /// For ordering <c>a asc, b desc, id asc</c> and last values <c>(x, y, z)</c>:
    /// <c>a &gt; x OR (a = x AND b &lt; y) OR (a = x AND b = y AND id &gt; z)</c>.
    /// </summary>
    public static Expression<Func<T, bool>> After<T>(QueryFields<T> fields, SortKey[] order, IReadOnlyList<string> position)
    {
        if (position.Count != order.Length)
        {
            throw Invalid();
        }

        var values = new Expression[order.Length];
        for (int i = 0; i < order.Length; i++)
        {
            QueryField field = order[i].Field;
            if (!field.TryParsePosition(position[i], out object value))
            {
                throw Invalid();
            }

            values[i] = FilterParser.Parameterize(value, field.Type);
        }

        Expression? any = null;
        for (int i = 0; i < order.Length; i++)
        {
            ExpressionType beyond = order[i].Descending ? ExpressionType.LessThan : ExpressionType.GreaterThan;
            Expression term = FilterParser.Compare(order[i].Field, beyond, values[i]);
            for (int j = i - 1; j >= 0; j--)
            {
                term = Expression.AndAlso(FilterParser.Compare(order[j].Field, ExpressionType.Equal, values[j]), term);
            }

            any = any is null ? term : Expression.OrElse(any, term);
        }

        return Expression.Lambda<Func<T, bool>>(any!, fields.Parameter);
    }

    // A verified token whose position does not fit the ordering can only come from a server change between pages
    // (a field renamed, an ordering changed); the caller restarts from the first page either way.
    private static ApiErrorException Invalid() =>
        ApiErrorException.Invalid(ApiErrorCode.InvalidSkipToken, "The $skiptoken is not valid for this query.", "$skiptoken");
}
