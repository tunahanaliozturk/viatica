using Viatica.Kernel.Querying;

namespace Viatica.Kernel.UnitTests;

public sealed class FilterTests
{
    private static int[] Ids(string? filter, string? orderBy = null) =>
        [.. Stays.Fields.Parse(filter, orderBy, top: 1000).ApplyTo(Stays.Rows.AsQueryable()).Select(static s => s.Id)];

    private static int[] Expected(Func<StayRow, bool> predicate) =>
        [.. Stays.Rows.Where(predicate).Select(static s => s.Id)];

    [Fact]
    public void Compares_every_supported_type()
    {
        Ids("city eq 'lisbon'").ShouldBe(Expected(static s => s.City == "lisbon"));
        Ids("city ne 'lisbon'").ShouldBe(Expected(static s => s.City != "lisbon"));
        Ids("city gt 'lisbon'").ShouldBe(Expected(static s => string.CompareOrdinal(s.City, "lisbon") > 0));
        Ids("fromPrice ge 80.50").ShouldBe(Expected(static s => s.FromPrice >= 80.50m));
        Ids("stars lt 3").ShouldBe(Expected(static s => s.Stars < 3));
        Ids("stars le -1").ShouldBeEmpty();
        Ids("refundable eq true").ShouldBe(Expected(static s => s.Refundable));
        Ids("availableFrom gt 2026-12-05").ShouldBe(Expected(static s => s.AvailableFrom > new DateOnly(2026, 12, 5)));
        Ids("updatedAt lt 2026-11-01T05:00:00Z").ShouldBe([1, 2, 3, 4]);
        Ids("updatedAt lt 2026-11-01T07:00:00+02:00").ShouldBe([1, 2, 3, 4]);
        Ids($"partnerId eq '{Stays.Partner}'").ShouldBe(Expected(static s => s.PartnerId == Stays.Partner));
        Ids("kind eq 'Hostel'").ShouldBe(Expected(static s => s.Kind == StayKind.Hostel));
        Ids("discount eq 5").ShouldBe(Expected(static s => s.Discount == 5m));
    }

    [Fact]
    public void Combines_conditions_with_and_and_in()
    {
        Ids("city in ('porto', 'faro') and stars ge 3 and refundable eq false")
            .ShouldBe(Expected(static s => s.City is "porto" or "faro" && s.Stars >= 3 && !s.Refundable));
        Ids("stars in (1,5)").ShouldBe(Expected(static s => s.Stars is 1 or 5));
    }

    [Fact]
    public void Reads_a_doubled_quote_as_one_quote() =>
        Ids("city eq 'O''Brien Town'").ShouldBe(Expected(static s => s.City == "O'Brien Town"));

    [Theory]
    // Classic injection shapes. Each must be refused by the grammar or end up as an inert string value.
    [InlineData("city eq 'lisbon' or 1 eq 1", "Expected 'and' or the end of the filter at position 17, found 'or'.")]
    [InlineData("city eq 'lisbon'; DROP TABLE stays", "Unexpected character ';' at position 16.")]
    [InlineData("city eq 'lisbon' -- ", "Expected 'and' or the end of the filter at position 17, found '--'.")]
    [InlineData("contains(city, 'lis')", "Unknown field 'contains' at position 0.")]
    [InlineData("city eq lisbon", "Field 'city' is a string; expected a quoted string such as 'lisbon' at position 8, found 'lisbon'.")]
    [InlineData("(city eq 'lisbon')", "Expected a field name at position 0, found '('.")]
    [InlineData("city eq 'lisbon')", "Expected 'and' or the end of the filter at position 16, found ')'.")]
    [InlineData("city eq 'lisbon", "The string starting at position 8 is not closed with a quote.")]
    [InlineData("not city eq 'lisbon'", "Unknown field 'not' at position 0.")]
    [InlineData("City eq 'lisbon'", "Unknown field 'City' at position 0.")]
    [InlineData("password eq 'x'", "Unknown field 'password' at position 0.")]
    [InlineData("city like 'l%'", "Unsupported operator 'like' at position 5. Use eq, ne, gt, ge, lt, le or in.")]
    [InlineData("city EQ 'lisbon'", "Unsupported operator 'EQ' at position 5. Use eq, ne, gt, ge, lt, le or in.")]
    [InlineData("city", "Expected an operator after 'city'.")]
    [InlineData("city eq", "Field 'city' is a string; expected a quoted string such as 'lisbon' at position 7, found the end of the filter.")]
    [InlineData("stars eq '3'", "Field 'stars' is a whole number; expected a whole number such as 42 at position 9, found '3'.")]
    [InlineData("stars eq 3.5", "Field 'stars' is a whole number; expected a whole number such as 42 at position 9, found '3.5'.")]
    [InlineData("stars eq 99999999999", "Field 'stars' is a whole number; expected a whole number such as 42 at position 9, found '99999999999'.")]
    [InlineData("fromPrice eq 1e3", "Field 'fromPrice' is a number; expected a number such as 312.50 at position 13, found '1e3'.")]
    [InlineData("availableFrom eq 2026-13-01", "Field 'availableFrom' is a date; expected a date such as 2026-12-01 at position 17, found '2026-13-01'.")]
    [InlineData("updatedAt gt 2026-11-01T05:00:00", "Field 'updatedAt' is an instant; expected an instant with an offset such as 2026-12-01T14:00:00Z at position 13, found '2026-11-01T05:00:00'.")]
    [InlineData("stars eq +5", "Unexpected character '+' at position 9.")]
    [InlineData("refundable gt false", "Field 'refundable' is a boolean; it supports eq, ne and in only.")]
    [InlineData("refundable eq 1", "Field 'refundable' is a boolean; expected true or false at position 14, found '1'.")]
    [InlineData("kind eq '1'", "Field 'kind' is an enumeration; expected one of 'Hotel', 'Apartment', 'Hostel' at position 8, found '1'.")]
    [InlineData("kind eq 'hotel'", "Field 'kind' is an enumeration; expected one of 'Hotel', 'Apartment', 'Hostel' at position 8, found 'hotel'.")]
    [InlineData("partnerId eq 'abc'", "Field 'partnerId' is an id; expected a quoted id such as '0b5e3c2a-4f1d-4c8e-9a3b-2d7f6e1c0a94' at position 13, found 'abc'.")]
    [InlineData("stars in 1, 2", "Expected '(' after 'in' at position 9, found '1'.")]
    [InlineData("stars in (1 2)", "Expected ',' or ')' at position 12, found '2'.")]
    [InlineData("stars in ()", "Field 'stars' is a whole number; expected a whole number such as 42 at position 10, found ')'.")]
    [InlineData("city eq 'a' and", "Expected a field name at position 15, found the end.")]
    [InlineData("", "The $filter is empty. Leave the parameter out to get every item.")]
    [InlineData("   ", "The $filter is empty. Leave the parameter out to get every item.")]
    public void Refuses_anything_outside_the_grammar_with_a_precise_message(string filter, string message)
    {
        var error = Should.Throw<ApiErrorException>(() => Stays.Fields.Parse(filter, null, top: 20));

        error.Category.ShouldBe(ErrorCategory.InvalidRequest);
        error.Error.Code.ShouldBe(ApiErrorCode.InvalidFilter);
        error.Error.Target.ShouldBe("$filter");
        error.Error.Message.ShouldStartWith(message);
    }

    [Fact]
    public void An_unknown_field_lists_the_ones_that_exist() =>
        Should.Throw<ApiErrorException>(() => Stays.Fields.Parse("nope eq 1", null, 20)).Error.Message.ShouldBe(
            "Unknown field 'nope' at position 0. Fields you can filter on: availableFrom, city, discount, fromPrice, id, kind, partnerId, refundable, stars, updatedAt.");

    [Fact]
    public void Bounds_the_size_of_a_filter()
    {
        string many = string.Join(" and ", Enumerable.Repeat("stars gt 1", 17));
        Should.Throw<ApiErrorException>(() => Stays.Fields.Parse(many, null, 20)).Error.Message
            .ShouldBe("The $filter has more than 16 conditions.");

        string longList = $"stars in ({string.Join(',', Enumerable.Range(1, 51))})";
        Should.Throw<ApiErrorException>(() => Stays.Fields.Parse(longList, null, 20)).Error.Message
            .ShouldBe("An 'in' list may hold at most 50 values.");

        Should.Throw<ApiErrorException>(() => Stays.Fields.Parse(new string('a', 2049), null, 20)).Error.Message
            .ShouldBe("The $filter is longer than 2048 characters.");
    }

    [Theory]
    [InlineData("rating desc", "Cannot sort on 'rating'. Fields you can sort on: availableFrom, city, fromPrice, id, stars, updatedAt.")]
    [InlineData("refundable", "Cannot sort on 'refundable'.")]
    [InlineData("fromPrice desc, fromPrice", "Field 'fromPrice' appears twice in $orderby.")]
    [InlineData("fromPrice descending", "Expected ',', 'asc', 'desc' or the end at position 10, found 'descending'.")]
    [InlineData("fromPrice desc,", "Expected a field name at position 15, found the end.")]
    [InlineData("fromPrice, stars, city, availableFrom, updatedAt", "The $orderby has more than 4 fields.")]
    [InlineData("", "The $orderby is empty.")]
    public void Refuses_an_ordering_outside_the_allowlist(string orderBy, string message)
    {
        var error = Should.Throw<ApiErrorException>(() => Stays.Fields.Parse(null, orderBy, 20));

        error.Error.Code.ShouldBe(ApiErrorCode.InvalidOrderBy);
        error.Error.Target.ShouldBe("$orderby");
        error.Error.Message.ShouldStartWith(message);
    }

    [Fact]
    public void Appends_the_key_so_the_ordering_is_total()
    {
        Stays.Fields.Parse(null, "fromPrice desc, city", 20).OrderBy.ShouldBe("fromPrice desc, city asc, id asc");
        Stays.Fields.Parse(null, "id desc, fromPrice", 20).OrderBy.ShouldBe("id desc, fromPrice asc");
        Stays.Fields.Parse(null, null, 20).OrderBy.ShouldBe("id asc");
    }

    [Theory]
    [InlineData(null)]
    [InlineData("fromPrice desc")]
    [InlineData("city, fromPrice desc")]
    [InlineData("availableFrom desc, stars")]
    [InlineData("updatedAt desc")]
    [InlineData("id desc")]
    public void Keyset_paging_visits_every_row_exactly_once_in_order(string? orderBy)
    {
        const string filter = "stars ge 2";
        int[] all = Ids(filter, orderBy);
        var seen = new List<int>();
        IReadOnlyList<string>? after = null;

        do
        {
            CollectionQuery<StayRow> query = Stays.Fields.Parse(filter, orderBy, top: 7, after);
            KeysetPage<StayRow> page = query.ToPage([.. query.ApplyTo(Stays.Rows.AsQueryable())]);
            page.Value.Count.ShouldBeLessThanOrEqualTo(7);
            seen.AddRange(page.Value.Select(static s => s.Id));
            after = page.NextPosition;
        }
        while (after is not null);

        seen.ShouldBe(all);
        all.Length.ShouldBe(Stays.Rows.Count(static s => s.Stars >= 2));
    }

    [Fact]
    public void The_last_page_has_no_next_position()
    {
        CollectionQuery<StayRow> query = Stays.Fields.Parse(null, null, top: 57);
        query.ToPage([.. query.ApplyTo(Stays.Rows.AsQueryable())]).NextPosition.ShouldBeNull();
    }

    [Theory]
    [InlineData("abc")]         // wrong type for the key
    [InlineData("1", "2")]      // wrong arity for the ordering
    public void A_position_that_does_not_fit_the_ordering_is_an_invalid_skiptoken(params string[] position) =>
        Should.Throw<ApiErrorException>(() => Stays.Fields.Parse(null, null, 20, position)).Error.Code
            .ShouldBe(ApiErrorCode.InvalidSkipToken);

    [Fact]
    public void Refuses_to_declare_a_field_keyset_paging_cannot_sort()
    {
        Should.Throw<ArgumentException>(() => new QueryFields<StayRow>().Field("discount", static s => s.Discount, sortable: true));
        Should.Throw<ArgumentException>(() => new QueryFields<StayRow>().Field("kind", static s => s.Kind, sortable: true));
        Should.Throw<ArgumentException>(() => new QueryFields<StayRow>().Field("bad name", static s => s.City));
        Should.Throw<ArgumentException>(() => new QueryFields<StayRow>().Field("city", static s => s.City).Field("city", static s => s.City));
    }
}
