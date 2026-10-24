using System.Net;
using System.Net.Http.Headers;
using System.Text.Json;
using Viatica.Search.Infrastructure.Indexing;
using Viatica.Testing;

namespace Viatica.Search.IntegrationTests;

/// <summary>The host around the search: index setup, readiness, API conventions, anonymous access.</summary>
public sealed class HostTests(SearchApp app) : IClassFixture<SearchApp>, IAsyncLifetime
{
    private static string Stay => $"destination=lisbon&checkIn={SearchApp.Base.AddDays(2):yyyy-MM-dd}&checkOut={SearchApp.Base.AddDays(4):yyyy-MM-dd}";

    public async ValueTask InitializeAsync() => await app.ResetAsync();

    public ValueTask DisposeAsync() => ValueTask.CompletedTask;

    [Fact]
    public async Task Setting_up_the_indices_twice_changes_nothing_and_each_alias_has_one_index_behind_it()
    {
        IReadOnlyList<string> stays = await app.Admin.BehindAsync("stays", CancellationToken.None);
        IReadOnlyList<string> cars = await app.Admin.BehindAsync("cars", CancellationToken.None);

        await app.Admin.EnsureAsync(CancellationToken.None);
        await app.Admin.EnsureAsync(CancellationToken.None);

        stays.Count.ShouldBe(1);
        cars.Count.ShouldBe(1);
        (await app.Admin.BehindAsync("stays", CancellationToken.None)).ShouldBe(stays);
        (await app.Admin.BehindAsync("cars", CancellationToken.None)).ShouldBe(cars);
    }

    [Fact]
    public async Task The_service_is_ready_only_while_both_aliases_exist()
    {
        HttpResponseMessage live = await app.Http.GetAsync(new Uri("/health/live", UriKind.Relative));
        HttpResponseMessage ready = await app.Http.GetAsync(new Uri("/health/ready", UriKind.Relative));
        live.StatusCode.ShouldBe(HttpStatusCode.OK);
        ready.StatusCode.ShouldBe(HttpStatusCode.OK);

        foreach (string index in await app.Admin.BehindAsync("cars", CancellationToken.None))
        {
            await app.Admin.DeleteAsync(index, CancellationToken.None);
        }

        HttpResponseMessage notReady = await app.Http.GetAsync(new Uri("/health/ready", UriKind.Relative));
        notReady.StatusCode.ShouldBe(HttpStatusCode.ServiceUnavailable);

        await app.Admin.EnsureAsync(CancellationToken.None);
        (await app.Http.GetAsync(new Uri("/health/ready", UriKind.Relative))).StatusCode.ShouldBe(HttpStatusCode.OK);
    }

    [Fact]
    public async Task A_request_without_a_known_api_version_is_refused_in_the_error_envelope()
    {
        using HttpResponseMessage missing = await app.Http.GetAsync(new Uri($"/stays/search?{Stay}", UriKind.Relative));
        using HttpResponseMessage unknown = await app.Http.GetAsync(new Uri($"/stays/search?{Stay}&api-version=2020-01-01", UriKind.Relative));

        missing.StatusCode.ShouldBe(HttpStatusCode.BadRequest);
        (await Code(missing)).ShouldBe("MissingApiVersionParameter");
        unknown.StatusCode.ShouldBe(HttpStatusCode.BadRequest);
        (await Code(unknown)).ShouldBe("UnsupportedApiVersionValue");
    }

    [Fact]
    public async Task Search_needs_no_token_and_a_token_that_does_not_validate_does_not_get_in_the_way()
    {
        (int anonymous, _) = await app.GetAsync($"/stays/search?{Stay}");

        using var request = new HttpRequestMessage(HttpMethod.Get, new Uri($"/stays/search?{Stay}&api-version={SearchApp.ApiVersion}", UriKind.Relative));
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", "not-a-token");
        using HttpResponseMessage withBadToken = await app.Http.SendAsync(request);

        using var traveller = new HttpRequestMessage(HttpMethod.Get, new Uri($"/stays/search?{Stay}&api-version={SearchApp.ApiVersion}", UriKind.Relative));
        traveller.Headers.Authorization = new AuthenticationHeaderValue("Bearer", TestTokens.Traveller("search-api"));
        using HttpResponseMessage withToken = await app.Http.SendAsync(traveller);

        anonymous.ShouldBe(200);
        withBadToken.StatusCode.ShouldBe(HttpStatusCode.OK);
        withToken.StatusCode.ShouldBe(HttpStatusCode.OK);
    }

    [Fact]
    public async Task Other_methods_and_unknown_routes_answer_in_the_error_envelope()
    {
        using HttpResponseMessage post = await app.Http.PostAsync(new Uri($"/stays/search?api-version={SearchApp.ApiVersion}", UriKind.Relative), content: null);
        (int status, JsonElement body) = await app.GetAsync("/hotels");

        post.StatusCode.ShouldBe(HttpStatusCode.MethodNotAllowed);
        (await Code(post)).ShouldBe("MethodNotAllowed");
        status.ShouldBe(404);
        body.GetProperty("error").GetProperty("code").GetString().ShouldBe("NotFound");
    }

    private static async Task<string> Code(HttpResponseMessage response)
    {
        using JsonDocument body = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        return body.RootElement.GetProperty("error").GetProperty("code").GetString()!;
    }
}
