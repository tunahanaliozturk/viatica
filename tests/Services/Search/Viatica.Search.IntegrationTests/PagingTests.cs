using System.Globalization;
using System.Text.Json;
using Viatica.Contracts;

namespace Viatica.Search.IntegrationTests;

/// <summary>Keyset paging over the index: every match once, in order, whatever happens between two pages.</summary>
public sealed class PagingTests(SearchApp app) : IClassFixture<SearchApp>, IAsyncLifetime
{
    private static string Stay => $"checkIn={SearchApp.Base.AddDays(2):yyyy-MM-dd}&checkOut={SearchApp.Base.AddDays(4):yyyy-MM-dd}";

    public async ValueTask InitializeAsync() => await app.ResetAsync();

    public ValueTask DisposeAsync() => ValueTask.CompletedTask;

    private static string[] Names(JsonElement body) =>
        [.. body.GetProperty("value").EnumerateArray().Select(static i => i.GetProperty("name").GetString()!)];

    private async Task<Guid> AddAsync(string name, decimal rate, int stars = 3, double lat = 38.72, double lon = -9.14)
    {
        var id = Guid.NewGuid();
        await app.SeedAsync(Seed.Property(id, name, "Lisbon", stars, lat, lon, ["wifi"], new RoomSpec("Standard", 2, 3, rate)));
        return id;
    }

    /// <summary>Follows every nextLink from the first page and returns the names in the order they came.</summary>
    private async Task<List<string>> AllPagesAsync(string firstUrl)
    {
        List<string> names = [];
        (int status, JsonElement page) = await app.GetAsync(firstUrl);
        status.ShouldBe(200);
        while (true)
        {
            names.AddRange(Names(page));
            if (!page.TryGetProperty("nextLink", out JsonElement next))
            {
                return names;
            }

            (status, page) = await app.FollowAsync(next.GetString()!);
            status.ShouldBe(200);
        }
    }

    [Fact]
    public async Task Following_the_next_links_returns_every_match_once_in_order()
    {
        for (int i = 0; i < 23; i++)
        {
            await AddAsync($"Hotel {i:00}", 50 + i);
        }

        List<string> names = await AllPagesAsync($"/stays/search?destination=lisbon&{Stay}&sort=price&$top=5");

        names.ShouldBe([.. Enumerable.Range(0, 23).Select(static i => $"Hotel {i:00}")]);
    }

    [Fact]
    public async Task Many_properties_with_the_same_price_page_without_repeats_or_gaps()
    {
        // 30 properties on three prices: the sort key ties constantly, so only the id tie-break keeps pages exact.
        for (int i = 0; i < 30; i++)
        {
            await AddAsync($"Tie {i:00}", 100 + (i % 3 * 10));
        }

        foreach (string sort in new[] { "price", "rating", "distance" })
        {
            List<string> names = await AllPagesAsync($"/stays/search?destination=lisbon&{Stay}&sort={sort}&lat=38.7&lon=-9.1&$top=4");

            names.Count.ShouldBe(30, sort);
            names.Distinct().Count().ShouldBe(30, sort);
        }
    }

    [Fact]
    public async Task Only_the_first_page_carries_the_total_and_the_facets_and_every_link_keeps_the_query()
    {
        for (int i = 0; i < 12; i++)
        {
            await AddAsync($"Hotel {i:00}", 60 + i);
        }

        (_, JsonElement first) = await app.GetAsync($"/stays/search?destination=lisbon&{Stay}&$top=5");
        first.GetProperty("totalCount").GetInt64().ShouldBe(12);
        first.TryGetProperty("facets", out _).ShouldBeTrue();
        string link = first.GetProperty("nextLink").GetString()!;
        link.ShouldContain("api-version=2026-11-01");
        link.ShouldContain("destination=lisbon");
        link.ShouldContain("$top=5");

        (int status, JsonElement second) = await app.FollowAsync(link);
        status.ShouldBe(200);
        second.GetProperty("value").GetArrayLength().ShouldBe(5);
        second.TryGetProperty("totalCount", out _).ShouldBeFalse();
        second.TryGetProperty("facets", out _).ShouldBeFalse();
    }

    [Fact]
    public async Task A_page_size_above_the_maximum_is_clamped_and_a_bad_one_is_refused()
    {
        await AddAsync("One", 80);

        (int clamped, _) = await app.GetAsync($"/stays/search?destination=lisbon&{Stay}&$top=5000");
        (int zero, JsonElement error) = await app.GetAsync($"/stays/search?destination=lisbon&{Stay}&$top=0");

        clamped.ShouldBe(200);
        zero.ShouldBe(400);
        error.GetProperty("error").GetProperty("code").GetString().ShouldBe("InvalidRequest");
    }

    [Fact]
    public async Task A_skiptoken_used_with_another_query_or_altered_is_refused()
    {
        for (int i = 0; i < 6; i++)
        {
            await AddAsync($"Hotel {i}", 60 + i);
        }

        (_, JsonElement first) = await app.GetAsync($"/stays/search?destination=lisbon&{Stay}&$top=2");
        string link = first.GetProperty("nextLink").GetString()!;

        (int otherQuery, JsonElement refused) = await app.FollowAsync(link.Replace("destination=lisbon", "destination=porto", StringComparison.Ordinal));
        (int otherSort, _) = await app.FollowAsync(link + "&sort=rating");
        (int altered, _) = await app.FollowAsync(link[..^2] + "AA");

        otherQuery.ShouldBe(400);
        refused.GetProperty("error").GetProperty("code").GetString().ShouldBe("InvalidSkipToken");
        otherSort.ShouldBe(400);
        altered.ShouldBe(400);
    }

    [Fact]
    public async Task Paging_stays_exact_while_new_events_arrive_between_the_pages()
    {
        // Twenty properties at 100, 101, ... 119. Take the first page of five: 100 to 104.
        Guid[] ids = new Guid[20];
        for (int i = 0; i < 20; i++)
        {
            ids[i] = await AddAsync($"Hotel {i:00}", 100 + i);
        }

        (_, JsonElement first) = await app.GetAsync($"/stays/search?destination=lisbon&{Stay}&sort=price&$top=5");
        Names(first).ShouldBe([.. Enumerable.Range(0, 5).Select(static i => $"Hotel {i:00}")]);

        // Between the pages: two new properties cheaper than the cursor (they belong before it), one new one dearer
        // than the cursor (it belongs after it), one property after the cursor sells out, the property at the cursor
        // itself sells out, and one already shown gets dearer than everything.
        await AddAsync("Newcomer Cheap A", 10);
        await AddAsync("Newcomer Cheap B", 20);
        await AddAsync("Newcomer Dear", 107.5m);
        await app.SeedAsync(new StayAvailabilityChanged(ids[10], await RoomOf(ids[10]), 2, Seed.Range(2, 2, 0, 110m)));
        await app.SeedAsync(new StayAvailabilityChanged(ids[4], await RoomOf(ids[4]), 2, Seed.Range(2, 2, 0, 104m)));
        await app.SeedAsync(new StayAvailabilityChanged(ids[1], await RoomOf(ids[1]), 2, Seed.Range(2, 2, 3, 500m)));

        List<string> rest = [];
        (int status, JsonElement page) = await app.FollowAsync(first.GetProperty("nextLink").GetString()!);
        while (true)
        {
            status.ShouldBe(200);
            rest.AddRange(Names(page));
            if (!page.TryGetProperty("nextLink", out JsonElement next))
            {
                break;
            }

            (status, page) = await app.FollowAsync(next.GetString()!);
        }

        // Hotel 05..19 in price order, minus Hotel 10 (sold out), with the dearer newcomer where its price puts it.
        // The cheap newcomers sit before the cursor and do not appear. Hotel 01 was read already, but its price
        // moved it past the cursor, so a page that follows the price sees it again at the end: keyset paging walks
        // the current order, and the order is all that is promised to stay exact.
        string[] expected =
        [
            .. Enumerable.Range(5, 15).Where(static i => i != 10).Select(static i => $"Hotel {i:00}"),
        ];
        expected = [.. expected.Take(3), "Newcomer Dear", .. expected.Skip(3), "Hotel 01"];
        rest.ShouldBe(expected);
        rest.Distinct().Count().ShouldBe(rest.Count);
    }

    private async Task<Guid> RoomOf(Guid propertyId)
    {
        JsonElement document = (await app.StayDocumentAsync(propertyId))!.Value;
        return document.GetProperty("rooms")[0].GetProperty("id").GetGuid();
    }
}
