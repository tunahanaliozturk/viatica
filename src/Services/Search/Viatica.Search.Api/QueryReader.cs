using System.Globalization;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Primitives;
using Viatica.Kernel;

namespace Viatica.Search.Api;

/// <summary>
/// Reads the query string of a search request into typed values and collects every problem instead of stopping at
/// the first, so a form learns about all its broken fields from one response. A parameter the endpoint does not
/// define is a problem too: a misspelt filter that is silently ignored returns results that look filtered and are
/// not (the conventions' rule that nothing is silently ignored).
/// </summary>
internal sealed class QueryReader
{
    private readonly IQueryCollection _query;
    private readonly List<ApiError> _problems = [];

    public QueryReader(IQueryCollection query, IReadOnlySet<string> known)
    {
        _query = query;
        foreach (string name in query.Keys.Where(name => !known.Contains(name)))
        {
            _problems.Add(new ApiError("UnknownParameter", $"{name} is not a parameter of this endpoint.") { Target = name });
        }
    }

    public string? Text(string name)
    {
        StringValues values = _query[name];
        if (values.Count > 1)
        {
            Problem(name, "InvalidValue", $"Send {name} once.");
            return null;
        }

        return values.Count == 0 || string.IsNullOrWhiteSpace(values[0]) ? null : values[0]!.Trim();
    }

    public DateOnly? Date(string name) =>
        Parse(name, static (string text, out DateOnly value) =>
            DateOnly.TryParseExact(text, "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out value),
            "a date as yyyy-MM-dd");

    public DateTimeOffset? Instant(string name) =>
        Parse(name, static (string text, out DateTimeOffset value) =>
            DateTimeOffset.TryParseExact(text, "O", CultureInfo.InvariantCulture, DateTimeStyles.None, out value)
            || DateTimeOffset.TryParseExact(text, "yyyy-MM-dd'T'HH:mm:ssK", CultureInfo.InvariantCulture, DateTimeStyles.None, out value)
            || DateTimeOffset.TryParseExact(text, "yyyy-MM-dd'T'HH:mmK", CultureInfo.InvariantCulture, DateTimeStyles.None, out value),
            "an RFC 3339 time with an offset, such as 2026-12-01T10:00:00+01:00");

    public int? Int(string name) =>
        Parse(name, static (string text, out int value) =>
            int.TryParse(text, NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture, out value), "a whole number");

    public decimal? Decimal(string name) =>
        Parse(name, static (string text, out decimal value) =>
            decimal.TryParse(text, NumberStyles.AllowLeadingSign | NumberStyles.AllowDecimalPoint, CultureInfo.InvariantCulture, out value), "a number such as 120.50");

    public double? Double(string name) =>
        Parse(name, static (string text, out double value) =>
            double.TryParse(text, NumberStyles.AllowLeadingSign | NumberStyles.AllowDecimalPoint, CultureInfo.InvariantCulture, out value)
            && double.IsFinite(value), "a number such as 38.72");

    public Guid? Id(string name) =>
        Parse(name, static (string text, out Guid value) => Guid.TryParse(text, out value), "a UUID");

    public TEnum? Enum<TEnum>(string name)
        where TEnum : struct, Enum =>
        Parse(name, static (string text, out TEnum value) => System.Enum.TryParse(text, ignoreCase: true, out value) && System.Enum.IsDefined(value),
            $"one of {string.Join(", ", System.Enum.GetNames<TEnum>().Select(static n => char.ToLowerInvariant(n[0]) + n[1..]))}");

    /// <summary>A comma separated list: <c>amenities=pool,wifi</c>.</summary>
    public IReadOnlyList<string> List(string name) =>
        Text(name) is { } text
            ? [.. text.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)]
            : [];

    public IReadOnlyList<int> Ints(string name)
    {
        List<int> values = [];
        foreach (string item in List(name))
        {
            if (int.TryParse(item, NumberStyles.None, CultureInfo.InvariantCulture, out int value))
            {
                values.Add(value);
            }
            else
            {
                Problem(name, "InvalidValue", $"{name} is a comma separated list of whole numbers.");
                break;
            }
        }

        return values;
    }

    /// <summary>The value of a parameter that must be present; a missing one is recorded and a default returned.</summary>
    public T Required<T>(T? value, string name)
        where T : struct
    {
        if (value is { } present)
        {
            return present;
        }

        if (_problems.TrueForAll(p => p.Target != name))
        {
            Problem(name, "ParameterRequired", $"{name} is required.");
        }

        return default;
    }

    public void Problem(string target, string code, string message) =>
        _problems.Add(new ApiError(code, message) { Target = target });

    /// <summary>Ends the request with one <c>400</c> listing every problem found while reading.</summary>
    public void ThrowIfInvalid()
    {
        if (_problems.Count > 0)
        {
            throw ApiErrorException.Validation(_problems);
        }
    }

    private delegate bool TryParse<T>(string text, out T value);

    private T? Parse<T>(string name, TryParse<T> tryParse, string expected)
        where T : struct
    {
        if (Text(name) is not { } text)
        {
            return null;
        }

        if (tryParse(text, out T value))
        {
            return value;
        }

        Problem(name, "InvalidValue", $"{name} must be {expected}.");
        return null;
    }
}
