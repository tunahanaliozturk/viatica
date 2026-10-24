using System.Net;
using System.Text.Json;

namespace Viatica.Kernel.UnitTests;

public sealed class VersioningTests(TestApi api) : IClassFixture<TestApi>
{
    private HttpClient Client => api.Client;

    [Fact]
    public async Task A_missing_version_is_named_with_the_supported_ones()
    {
        JsonElement error = await (await Client.GetAsync("/stays")).ShouldBeError(HttpStatusCode.BadRequest, "MissingApiVersionParameter");

        error.Str("target").ShouldBe("api-version");
        error.Str("message").ShouldBe("The api-version query parameter is required. Supported versions: 2026-11-01.");
    }

    [Theory]
    [InlineData("/stays?api-version=2025-01-01")]
    [InlineData("/stays?api-version=")]
    [InlineData("/stays?api-version=2026-11-01&api-version=2026-11-01")]
    [InlineData("/stays?api-version=2026-11-01%20")]
    public async Task An_unknown_or_repeated_version_is_refused(string url) =>
        (await (await Client.GetAsync(url)).ShouldBeError(HttpStatusCode.BadRequest, "UnsupportedApiVersionValue"))
            .Str("target").ShouldBe("api-version");

    [Fact]
    public async Task The_parameter_name_matches_without_regard_to_case() =>
        (await Client.GetAsync("/stays?API-Version=2026-11-01")).StatusCode.ShouldBe(HttpStatusCode.OK);

    [Fact]
    public async Task A_version_neutral_endpoint_answers_without_one() =>
        (await Client.GetAsync("/health")).StatusCode.ShouldBe(HttpStatusCode.OK);

    [Fact]
    public async Task An_unmatched_route_stays_a_404() =>
        await (await Client.GetAsync("/nowhere")).ShouldBeError(HttpStatusCode.NotFound, "NotFound");
}
