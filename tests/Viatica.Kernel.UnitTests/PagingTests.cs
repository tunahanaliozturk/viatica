using System.Net;
using System.Text.Json;
using Microsoft.AspNetCore.WebUtilities;

namespace Viatica.Kernel.UnitTests;

public sealed class PagingTests(TestApi api) : IClassFixture<TestApi>
{
    private HttpClient Client => api.Client;

    private async Task<(int[] Ids, string? NextLink)> Page(string url)
    {
        var response = await Client.GetAsync(url);
        response.StatusCode.ShouldBe(HttpStatusCode.OK, await response.Content.ReadAsStringAsync());
        JsonElement body = await response.Body();
        int[] ids = [.. body.GetProperty("value").EnumerateArray().Select(static item => item.GetProperty("id").GetInt32())];
        return (ids, body.Str("nextLink"));
    }

    [Fact]
    public async Task The_default_page_is_20_items_with_an_absolute_next_link()
    {
        var (ids, nextLink) = await Page(Http.V("/stays"));

        ids.Length.ShouldBe(20);
        nextLink.ShouldNotBeNull();
        nextLink.ShouldStartWith("http://localhost/stays?");
        QueryHelpers.ParseQuery(new Uri(nextLink).Query)["api-version"].ToString().ShouldBe(TestApi.Version);
    }

    [Fact]
    public async Task Items_use_the_shared_representation()
    {
        var response = await Client.GetAsync(Http.V("/stays?$top=1"));
        JsonElement item = (await response.Body()).GetProperty("value")[0];

        item.GetRawText().ShouldBe("""{"id":1,"city":"porto","fromPrice":{"amount":"60.00","currency":"EUR"}}""");
    }

    [Theory]
    [InlineData("500")]
    [InlineData("101")]
    [InlineData("99999999999999999999")]
    public async Task A_top_above_the_maximum_is_clamped_to_100(string top)
    {
        var (ids, nextLink) = await Page(Http.V($"/stays?$top={top}"));

        ids.Length.ShouldBe(57);
        nextLink.ShouldBeNull();

        var big = await Page(Http.V($"/stays?$top={top}&$filter=id le 1000"));
        big.Ids.Length.ShouldBe(57);
    }

    [Theory]
    [InlineData("0")]
    [InlineData("-5")]
    [InlineData("abc")]
    [InlineData("1.5")]
    [InlineData("")]
    public async Task A_top_that_is_not_a_positive_number_is_a_400(string top) =>
        (await (await Client.GetAsync(Http.V($"/stays?$top={top}"))).ShouldBeError(HttpStatusCode.BadRequest, "InvalidRequest"))
            .Str("target").ShouldBe("$top");

    [Fact]
    public async Task Following_next_links_returns_every_match_once_and_keeps_the_query()
    {
        string url = Http.V("/stays?$filter=stars ge 2 and city ne 'faro'&$orderby=fromPrice desc&$top=6");
        var all = new List<int>();
        int pages = 0;

        for (string? next = url; next is not null; pages++)
        {
            var (ids, nextLink) = await Page(next);
            all.AddRange(ids);
            if (nextLink is not null)
            {
                var query = QueryHelpers.ParseQuery(new Uri(nextLink).Query);
                query["$filter"].ToString().ShouldBe("stars ge 2 and city ne 'faro'");
                query["$orderby"].ToString().ShouldBe("fromPrice desc");
                query["$top"].ToString().ShouldBe("6");
                query["api-version"].ToString().ShouldBe(TestApi.Version);
            }

            next = nextLink;
        }

        int[] expected = [.. Stays.Rows
            .Where(static s => s.Stars >= 2 && s.City != "faro")
            .OrderByDescending(static s => s.FromPrice).ThenBy(static s => s.Id)
            .Select(static s => s.Id)];
        all.ShouldBe(expected);
        pages.ShouldBe((expected.Length + 5) / 6);
    }

    [Fact]
    public async Task A_skiptoken_replayed_against_a_different_query_is_refused()
    {
        var (_, nextLink) = await Page(Http.V("/stays?$filter=city eq 'lisbon'&$top=2"));
        string token = QueryHelpers.ParseQuery(new Uri(nextLink!).Query)["$skiptoken"]!;

        var response = await Client.GetAsync(Http.V($"/stays?$filter=city eq 'porto'&$top=2&$skiptoken={token}"));

        JsonElement error = await response.ShouldBeError(HttpStatusCode.BadRequest, "InvalidSkipToken");
        error.Str("target").ShouldBe("$skiptoken");
    }

    [Fact]
    public async Task A_skiptoken_without_its_filter_is_refused()
    {
        var (_, nextLink) = await Page(Http.V("/stays?$filter=city eq 'lisbon'&$top=2"));
        string token = QueryHelpers.ParseQuery(new Uri(nextLink!).Query)["$skiptoken"]!;

        await (await Client.GetAsync(Http.V($"/stays?$skiptoken={token}"))).ShouldBeError(HttpStatusCode.BadRequest, "InvalidSkipToken");
    }

    [Fact]
    public async Task A_tampered_skiptoken_is_refused()
    {
        var (_, nextLink) = await Page(Http.V("/stays?$top=2"));
        string token = QueryHelpers.ParseQuery(new Uri(nextLink!).Query)["$skiptoken"]!;
        char last = token[^1];
        string tampered = token[..^1] + (last == 'A' ? 'B' : 'A');

        await (await Client.GetAsync(Http.V($"/stays?$top=2&$skiptoken={tampered}"))).ShouldBeError(HttpStatusCode.BadRequest, "InvalidSkipToken");
        await (await Client.GetAsync(Http.V("/stays?$skiptoken=eyJrIjpbIjEiXX0"))).ShouldBeError(HttpStatusCode.BadRequest, "InvalidSkipToken");
    }

    [Fact]
    public async Task The_page_size_may_change_between_pages()
    {
        var (first, nextLink) = await Page(Http.V("/stays?$top=5"));
        string token = QueryHelpers.ParseQuery(new Uri(nextLink!).Query)["$skiptoken"]!;

        var (second, _) = await Page(Http.V($"/stays?$top=10&$skiptoken={token}"));

        first.ShouldBe([1, 2, 3, 4, 5]);
        second.ShouldBe([6, 7, 8, 9, 10, 11, 12, 13, 14, 15]);
    }

    [Theory]
    [InlineData("$filter=nope eq 1", "InvalidFilter", "$filter")]
    [InlineData("$filter=city eq 'a' or city eq 'b'", "InvalidFilter", "$filter")]
    [InlineData("$filter=city eq 'a'&$filter=city eq 'b'", "InvalidFilter", "$filter")]
    [InlineData("$orderby=refundable", "InvalidOrderBy", "$orderby")]
    public async Task Filter_and_ordering_errors_reach_the_client_as_envelopes(string query, string code, string target) =>
        (await (await Client.GetAsync(Http.V($"/stays?{query}"))).ShouldBeError(HttpStatusCode.BadRequest, code))
            .Str("target").ShouldBe(target);

    [Fact]
    public async Task A_next_link_over_a_long_sort_value_leads_to_the_next_page()
    {
        var response = await Client.GetAsync(Http.V("/long-stays/1400?$orderby=city&$top=1"));
        response.StatusCode.ShouldBe(HttpStatusCode.OK);
        string nextLink = (await response.Body()).Str("nextLink")!;

        var next = await Client.GetAsync(nextLink);

        next.StatusCode.ShouldBe(HttpStatusCode.OK, await next.Content.ReadAsStringAsync());
        (await next.Body()).GetProperty("value")[0].GetProperty("id").GetInt32().ShouldBe(2);
    }

    // The link would be longer than the decoder accepts. Failing the request is honest; a link that always answers
    // InvalidSkipToken is not.
    [Fact]
    public async Task A_sort_value_too_long_to_page_on_fails_the_request_instead_of_emitting_a_dead_link()
    {
        var response = await Client.GetAsync(Http.V("/long-stays/1600?$orderby=city&$top=1"));

        await response.ShouldBeError(HttpStatusCode.InternalServerError, "InternalError");
    }
}
