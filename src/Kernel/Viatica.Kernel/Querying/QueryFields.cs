using System.Linq.Expressions;

namespace Viatica.Kernel.Querying;

/// <summary>
/// The fields one collection endpoint lets callers filter and sort on, with their types. Anything not declared
/// here is rejected by <see cref="Parse"/>, never ignored.
/// </summary>
/// <remarks>
/// <para>
/// Declare it once per endpoint, as a static field, and parse each request against it:
/// <code>
/// static readonly QueryFields&lt;StayRow&gt; Fields = new QueryFields&lt;StayRow&gt;()
///     .Key("id", s =&gt; s.Id)
///     .Field("city", s =&gt; s.City)
///     .Field("fromPrice", s =&gt; s.FromPrice, sortable: true);
/// </code>
/// </para>
/// <para>
/// This lives in the kernel, not in the web layer, because the query handler in Application applies the result
/// to its <c>DbContext</c>, and Application may not reference ASP.NET Core. The web layer only reads the query
/// string and the skiptoken.
/// </para>
/// </remarks>
public sealed class QueryFields<T>
{
    private readonly Dictionary<string, QueryField> fields = new(StringComparer.Ordinal);
    private QueryField? key;
    private string? defaultOrderBy;

    internal ParameterExpression Parameter { get; } = Expression.Parameter(typeof(T), "row");

    internal QueryField? KeyField => key;

    /// <summary>
    /// Declares a field callers may filter on and, when <paramref name="sortable"/>, sort on. Supported types are
    /// string, the integer types, decimal, double, bool, <see cref="DateOnly"/>, <see cref="DateTimeOffset"/>,
    /// <see cref="Guid"/> and enums, plus their nullable forms for filtering.
    /// </summary>
    public QueryFields<T> Field<TValue>(string name, Expression<Func<T, TValue>> selector, bool sortable = false)
    {
        Add(name, selector, sortable);
        return this;
    }

    /// <summary>
    /// Declares the unique key that breaks ties in every ordering. Keyset paging is only correct when the ordering
    /// is total, so the key is appended to any <c>$orderby</c> that does not already end with it.
    /// </summary>
    public QueryFields<T> Key<TValue>(string name, Expression<Func<T, TValue>> selector)
    {
        if (key is not null)
        {
            throw new InvalidOperationException($"The key is already '{key.Name}'.");
        }

        key = Add(name, selector, sortable: true);
        return this;
    }

    /// <summary>The ordering used when the request has no <c>$orderby</c>, in <c>$orderby</c> syntax.</summary>
    public QueryFields<T> DefaultOrderBy(string orderBy)
    {
        _ = FilterParser.ParseOrderBy(this, orderBy);
        defaultOrderBy = orderBy;
        return this;
    }

    /// <summary>
    /// Parses <c>$filter</c> and <c>$orderby</c> against the declared fields and binds the keyset position from a
    /// verified skiptoken.
    /// </summary>
    /// <exception cref="ApiErrorException">
    /// <c>InvalidFilter</c>, <c>InvalidOrderBy</c> or <c>InvalidSkipToken</c>, with a message that says what was
    /// wrong and where.
    /// </exception>
    public CollectionQuery<T> Parse(string? filter, string? orderBy, int top, IReadOnlyList<string>? after = null)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(top, 1);
        if (key is null)
        {
            throw new InvalidOperationException($"QueryFields<{typeof(T).Name}> needs a Key before it can page.");
        }

        Expression<Func<T, bool>>? predicate = filter is null ? null : FilterParser.ParseFilter(this, filter);
        SortKey[] order = FilterParser.ParseOrderBy(this, orderBy ?? defaultOrderBy);
        Expression<Func<T, bool>>? keyset = after is null ? null : Keyset.After(this, order, after);

        return new CollectionQuery<T>(top, predicate, keyset, order, Parameter);
    }

    internal bool TryGet(string name, out QueryField field) => fields.TryGetValue(name, out field!);

    internal string Names(Func<QueryField, bool> include)
    {
        string[] names = [.. fields.Values.Where(include).Select(static field => field.Name).Order(StringComparer.Ordinal)];
        return names.Length == 0 ? "none" : string.Join(", ", names);
    }

    private QueryField Add<TValue>(string name, Expression<Func<T, TValue>> selector, bool sortable)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        ArgumentNullException.ThrowIfNull(selector);

        if (!name.All(char.IsAsciiLetterOrDigit) || !char.IsAsciiLetter(name[0]))
        {
            throw new ArgumentException($"'{name}' is not a valid field name; use letters and digits.", nameof(name));
        }

        if (fields.ContainsKey(name))
        {
            throw new ArgumentException($"Field '{name}' is declared twice.", nameof(name));
        }

        Expression member = new Rebind(selector.Parameters[0], Parameter).Visit(selector.Body);
        QueryField field = QueryField.Create(name, member, typeof(TValue), sortable, Parameter);
        fields.Add(name, field);
        return field;
    }

    private sealed class Rebind(ParameterExpression from, ParameterExpression to) : ExpressionVisitor
    {
        protected override Expression VisitParameter(ParameterExpression node) => node == from ? to : node;
    }
}

/// <summary>
/// A parsed collection request: filter, total ordering, keyset position and page size, ready to apply to an
/// <see cref="IQueryable{T}"/>.
/// </summary>
public sealed class CollectionQuery<T>
{
    private readonly SortKey[] order;
    private readonly ParameterExpression parameter;

    internal CollectionQuery(
        int top,
        Expression<Func<T, bool>>? filter,
        Expression<Func<T, bool>>? after,
        SortKey[] order,
        ParameterExpression parameter)
    {
        Top = top;
        Filter = filter;
        After = after;
        this.order = order;
        this.parameter = parameter;
    }

    /// <summary>The page size after clamping.</summary>
    public int Top { get; }

    /// <summary>The <c>$filter</c> as a predicate, or null when there was none.</summary>
    public Expression<Func<T, bool>>? Filter { get; }

    /// <summary>"Rows after the last one the caller saw", or null on the first page.</summary>
    public Expression<Func<T, bool>>? After { get; }

    /// <summary>The ordering as <c>field asc|desc</c> pairs, key last.</summary>
    public string OrderBy => string.Join(", ", order.Select(static sort => $"{sort.Field.Name} {(sort.Descending ? "desc" : "asc")}"));

    /// <summary>
    /// Filters, skips past the keyset position, orders and takes <see cref="Top"/> + 1 rows. The extra row is how
    /// <see cref="ToPage{TItem}"/> knows whether there is a next page without a second count query.
    /// </summary>
    public IQueryable<T> ApplyTo(IQueryable<T> source)
    {
        ArgumentNullException.ThrowIfNull(source);

        if (Filter is not null)
        {
            source = source.Where(Filter);
        }

        if (After is not null)
        {
            source = source.Where(After);
        }

        for (int i = 0; i < order.Length; i++)
        {
            SortKey sort = order[i];
            string method = (i == 0, sort.Descending) switch
            {
                (true, false) => nameof(Queryable.OrderBy),
                (true, true) => nameof(Queryable.OrderByDescending),
                (false, false) => nameof(Queryable.ThenBy),
                (false, true) => nameof(Queryable.ThenByDescending),
            };

            source = source.Provider.CreateQuery<T>(Expression.Call(
                typeof(Queryable),
                method,
                [typeof(T), sort.Field.Type],
                source.Expression,
                Expression.Quote(Expression.Lambda(sort.Field.Member, parameter))));
        }

        return source.Take(Top + 1);
    }

    /// <summary>Cuts the rows read through <see cref="ApplyTo"/> to one page and works out the next position.</summary>
    public KeysetPage<T> ToPage(IReadOnlyList<T> rows) => ToPage(rows, static row => row);

    /// <summary>
    /// Cuts the rows read through <see cref="ApplyTo"/> to one page, maps each to its response type, and works out
    /// the next position from the last row kept.
    /// </summary>
    public KeysetPage<TItem> ToPage<TItem>(IReadOnlyList<T> rows, Func<T, TItem> map)
    {
        ArgumentNullException.ThrowIfNull(rows);
        ArgumentNullException.ThrowIfNull(map);

        bool more = rows.Count > Top;
        int count = more ? Top : rows.Count;
        var items = new TItem[count];
        for (int i = 0; i < count; i++)
        {
            items[i] = map(rows[i]);
        }

        return new KeysetPage<TItem>(items, more ? Keyset.Position(order, rows[count - 1]!) : null);
    }
}

/// <summary>
/// One page of a collection and, when there is more, the keyset position of its last row. The web layer turns the
/// position into a signed skiptoken; it never reaches a client as is.
/// </summary>
public sealed record KeysetPage<T>(IReadOnlyList<T> Value, IReadOnlyList<string>? NextPosition);

internal sealed record SortKey(QueryField Field, bool Descending);
