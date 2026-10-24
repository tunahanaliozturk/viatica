using System.Net;
using System.Text.Json;

namespace Viatica.Kernel.UnitTests;

public sealed class ConcurrencyAndOperationTests(TestApi api) : IClassFixture<TestApi>
{
    private HttpClient Client => api.Client;

    private async Task<HttpResponseMessage> Put(int room, string? ifMatch)
    {
        using var request = new HttpRequestMessage(HttpMethod.Put, Http.V($"/rooms/{room}")) { Content = Http.Json("""{"name":"Twin"}""") };
        if (ifMatch is not null)
        {
            request.Headers.TryAddWithoutValidation("If-Match", ifMatch);
        }

        return await Client.SendAsync(request);
    }

    [Fact]
    public async Task A_read_carries_the_row_version_as_a_strong_etag()
    {
        var response = await Client.GetAsync(Http.V("/rooms/100"));

        response.Headers.ETag!.Tag.ShouldBe("\"7\"");
        response.Headers.ETag.IsWeak.ShouldBeFalse();
    }

    [Fact]
    public async Task An_update_without_if_match_is_a_428() =>
        (await (await Put(101, null)).ShouldBeError(HttpStatusCode.PreconditionRequired, "PreconditionRequired"))
            .Str("target").ShouldBe("If-Match");

    [Fact]
    public async Task An_update_with_a_stale_etag_is_a_412()
    {
        (await Put(102, "\"7\"")).StatusCode.ShouldBe(HttpStatusCode.OK);

        await (await Put(102, "\"7\"")).ShouldBeError(HttpStatusCode.PreconditionFailed, "PreconditionFailed");
    }

    [Fact]
    public async Task An_update_with_the_current_etag_succeeds_and_returns_the_next_one()
    {
        var response = await Put(103, "\"7\"");

        response.StatusCode.ShouldBe(HttpStatusCode.OK);
        response.Headers.ETag!.Tag.ShouldBe("\"8\"");
    }

    [Theory]
    [InlineData("W/\"7\"")]          // weak tags never match under If-Match
    [InlineData("\"not-ours\"")]
    public async Task A_tag_that_cannot_match_is_a_412(string tag) =>
        await (await Put(104, tag)).ShouldBeError(HttpStatusCode.PreconditionFailed, "PreconditionFailed");

    [Theory]
    [InlineData("7")]
    [InlineData("\"7\", \"8\"")]
    public async Task A_malformed_if_match_is_a_400(string tag) =>
        await (await Put(105, tag)).ShouldBeError(HttpStatusCode.BadRequest, "InvalidRequest");

    [Fact]
    public async Task If_match_star_accepts_any_current_version() =>
        (await Put(106, "*")).StatusCode.ShouldBe(HttpStatusCode.OK);

    [Fact]
    public async Task A_long_running_operation_answers_202_with_an_operation_location()
    {
        var response = await Client.PostAsync(Http.V("/checkouts"), null);

        response.StatusCode.ShouldBe(HttpStatusCode.Accepted);
        response.Headers.GetValues("Operation-Location").Single()
            .ShouldBe($"http://localhost/operations/op-1?api-version={TestApi.Version}");
        (await response.Body()).GetRawText().ShouldBe("""{"id":"op-1","status":"NotStarted"}""");
    }

    [Fact]
    public async Task An_operation_reports_its_status_as_a_string_and_its_error_in_the_envelope_shape()
    {
        JsonElement body = await (await Client.GetAsync(Http.V("/operations/op-9"))).Body();

        body.GetRawText().ShouldBe(
            """{"id":"op-9","status":"Failed","error":{"code":"HoldExpired","message":"The hold on the room expired.","innererror":{"code":"StayHoldExpired"}}}""");
    }
}
