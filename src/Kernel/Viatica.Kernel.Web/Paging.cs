using System.Globalization;
using System.Text;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.HttpResults;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Primitives;
using Viatica.Kernel.Querying;

namespace Viatica.Kernel.Web;

/// <summary>A page of a collection: <c>{ "value": [...], "nextLink": "..." }</c>, with no <c>nextLink</c> on the last page.</summary>
public sealed record PagedResponse<T>(IReadOnlyList<T> Value, string? NextLink);

/// <summary>Reads <c>$top</c>, <c>$filter</c>, <c>$orderby</c> and <c>$skiptoken</c>, and writes a page with its <c>nextLink</c>.</summary>
public static class Paging
{
    public const string Top = "$top";
    public const string SkipToken = "$skiptoken";
    public const string Filter = "$filter";
    public const string OrderBy = "$orderby";
    public const int DefaultTop = 20;
    public const int MaxTop = 100;

    /// <summary>
    /// Binds the collection parameters of the current request against an endpoint's allowlist. Errors come back as
    /// <see cref="ApiErrorException"/> and so as the error envelope.
    /// </summary>
    public static CollectionQuery<T> BindCollectionQuery<T>(this HttpContext context, QueryFields<T> fields)
    {
        ArgumentNullException.ThrowIfNull(context);
        ArgumentNullException.ThrowIfNull(fields);

        IQueryCollection query = context.Request.Query;
        int top = ReadTop(query);
        string? filter = Single(query, Filter, ApiErrorCode.InvalidFilter);
        string? orderBy = Single(query, OrderBy, ApiErrorCode.InvalidOrderBy);
        string? token = Single(query, SkipToken, ApiErrorCode.InvalidSkipToken);

        string[]? after = null;
        if (token is not null)
        {
            SkipTokenCodec codec = context.RequestServices.GetRequiredService<SkipTokenCodec>();
            if (!codec.TryDecode(token, NormalizeQuery(context.Request), out after))
            {
                throw ApiErrorException.Invalid(
                    ApiErrorCode.InvalidSkipToken,
                    "The $skiptoken is not valid for this query. Follow nextLink exactly as returned, or start again without it.",
                    SkipToken);
            }
        }

        return fields.Parse(filter, orderBy, top, after);
    }

    /// <summary>A <c>200</c> with the page and, when there is more, a <c>nextLink</c> carrying a signed skiptoken.</summary>
    public static Ok<PagedResponse<T>> Page<T>(this HttpContext context, KeysetPage<T> page)
    {
        ArgumentNullException.ThrowIfNull(context);
        ArgumentNullException.ThrowIfNull(page);

        string? nextLink = null;
        if (page.NextPosition is { } position)
        {
            SkipTokenCodec codec = context.RequestServices.GetRequiredService<SkipTokenCodec>();
            nextLink = NextLink(context.Request, codec.Encode(position, NormalizeQuery(context.Request)));
        }

        return TypedResults.Ok(new PagedResponse<T>(page.Value, nextLink));
    }

    internal static string NormalizeQuery(HttpRequest request) =>
        SkipTokenCodec.NormalizeQuery(string.Concat(request.PathBase.Value, request.Path.Value), request.Query);

    /// <summary>
    /// <c>$top</c>: absent is the default, above the maximum is clamped (the guidelines say a server may return
    /// fewer items than asked), anything that is not a positive whole number is a 400.
    /// </summary>
    internal static int ReadTop(IQueryCollection query)
    {
        string? text = Single(query, Top, ApiErrorCode.InvalidRequest);
        if (text is null)
        {
            return DefaultTop;
        }

        if (text.Length == 0 || text.AsSpan().ContainsAnyExceptInRange('0', '9'))
        {
            throw ApiErrorException.Invalid(ApiErrorCode.InvalidRequest, "$top must be a positive whole number.", Top);
        }

        // All digits: a value too large for an int is still just "more than the maximum".
        if (!int.TryParse(text, NumberStyles.None, CultureInfo.InvariantCulture, out int top) || top > MaxTop)
        {
            return MaxTop;
        }

        return top > 0
            ? top
            : throw ApiErrorException.Invalid(ApiErrorCode.InvalidRequest, "$top must be a positive whole number.", Top);
    }

    private static string? Single(IQueryCollection query, string name, string code)
    {
        StringValues values = query[name];
        return values.Count switch
        {
            0 => null,
            1 => values[0] ?? string.Empty,
            _ => throw ApiErrorException.Invalid(code, $"Send {name} once.", name),
        };
    }

    /// <summary>The request URL as the client sent it, with <c>$skiptoken</c> replaced, so <c>api-version</c>,
    /// the filter and the page size carry over.</summary>
    private static string NextLink(HttpRequest request, string token)
    {
        var url = new StringBuilder()
            .Append(request.Scheme).Append("://").Append(request.Host.ToUriComponent())
            .Append(request.PathBase.ToUriComponent()).Append(request.Path.ToUriComponent());

        char separator = '?';
        foreach (var (name, values) in request.Query)
        {
            if (name.Equals(SkipToken, StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            foreach (string? value in values)
            {
                url.Append(separator).Append(Escape(name)).Append('=').Append(Escape(value ?? string.Empty));
                separator = '&';
            }
        }

        return url.Append(separator).Append(SkipToken).Append('=').Append(token).ToString();
    }

    // RFC 3986 allows '$' unescaped in a query, and "$filter" reads better in a log than "%24filter".
    private static string Escape(string value) =>
        Uri.EscapeDataString(value).Replace("%24", "$", StringComparison.Ordinal);
}
