using System.Net;
using System.Text;
using System.Text.Json;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.Logging;
using Viatica.Kernel.Web;

namespace Viatica.Kernel.UnitTests;

public sealed class ErrorEnvelopeTests(TestApi api) : IClassFixture<TestApi>
{
    private HttpClient Client => api.Client;

    [Fact]
    public async Task Three_invalid_fields_give_one_400_with_three_details()
    {
        var response = await Client.PostAsync(Http.V("/trips"),
            Http.Json("""{"name":" ","guests":0,"checkIn":"2026-12-04","checkOut":"2026-12-01"}"""));

        JsonElement error = await response.ShouldBeError(HttpStatusCode.BadRequest, ApiErrorCode.InvalidRequest);
        JsonElement[] details = [.. error.GetProperty("details").EnumerateArray()];
        details.Select(static d => (d.Str("code"), d.Str("target"), d.Str("message"))).ShouldBe(
        [
            ("NameRequired", "name", "name is required."),
            ("GuestsOutOfRange", "guests", "guests must be between 1 and 9."),
            ("CheckOutBeforeCheckIn", "items[0].checkOut", "checkOut must be after checkIn."),
        ]);
    }

    [Fact]
    public async Task A_valid_request_passes_the_validation_behaviour_to_its_handler()
    {
        var response = await Client.PostAsync(Http.V("/trips"),
            Http.Json("""{"name":"Lisbon","guests":2,"checkIn":"2026-12-01","checkOut":"2026-12-04"}"""));

        response.StatusCode.ShouldBe(HttpStatusCode.OK);
        (await response.Body()).GetProperty("name").GetString().ShouldBe("Lisbon");
    }

    [Fact]
    public async Task A_domain_error_keeps_its_own_code_and_status()
    {
        var response = await Client.GetAsync(Http.V("/sold-out"));

        JsonElement error = await response.ShouldBeError(HttpStatusCode.Conflict, "SoldOut");
        error.Str("message").ShouldBe("No rooms left for those nights.");
    }

    [Fact]
    public async Task An_unhandled_exception_is_a_500_that_reveals_nothing()
    {
        var response = await Client.GetAsync(Http.V("/boom"));

        JsonElement error = await response.ShouldBeError(HttpStatusCode.InternalServerError, ApiErrorCode.InternalError);
        string raw = error.GetRawText();
        raw.ShouldNotContain("hunter2");
        raw.ShouldNotContain("InvalidOperationException");
        raw.ShouldNotContain("at Viatica");
        error.Str("message").ShouldBe("An internal error occurred.");
    }

    [Theory]
    [InlineData("/nowhere", HttpStatusCode.NotFound, ApiErrorCode.NotFound)]
    [InlineData("/me", HttpStatusCode.Unauthorized, ApiErrorCode.Unauthorized)]
    public async Task Framework_statuses_get_the_envelope_too(string url, HttpStatusCode status, string code) =>
        await (await Client.GetAsync(Http.V(url))).ShouldBeError(status, code);

    [Fact]
    public async Task A_wrong_method_is_a_405_not_a_versioning_error() =>
        await (await Client.DeleteAsync("/health")).ShouldBeError(HttpStatusCode.MethodNotAllowed, ApiErrorCode.MethodNotAllowed);

    [Fact]
    public async Task A_body_that_is_not_json_is_a_415() =>
        await (await Client.PostAsync(Http.V("/echo"), new StringContent("name=x", Encoding.UTF8, "text/plain")))
            .ShouldBeError(HttpStatusCode.UnsupportedMediaType, ApiErrorCode.UnsupportedMediaType);

    [Theory]
    [InlineData("""{"name":"x","count":"many"}""")]
    [InlineData("{\"name\":\"x\"")]
    [InlineData("""{"count":1}""")]
    [InlineData("""{"name":null,"count":1}""")]
    public async Task A_body_that_will_not_bind_is_a_400(string json)
    {
        var response = await Client.PostAsync(Http.V("/echo"), Http.Json(json));

        JsonElement error = await response.ShouldBeError(HttpStatusCode.BadRequest, ApiErrorCode.InvalidRequest);
        error.GetRawText().ShouldNotContain("Echo");
    }

    [Fact]
    public async Task Nothing_answers_with_problem_details()
    {
        string[] urls = [Http.V("/nowhere"), Http.V("/boom"), Http.V("/sold-out"), "/stays", Http.V("/stays?$top=x")];
        foreach (string url in urls)
        {
            var response = await Client.GetAsync(url);
            response.Content.Headers.ContentType?.MediaType.ShouldBe("application/json", url);
            (await response.Content.ReadAsStringAsync()).ShouldNotContain("\"title\"", Case.Sensitive, url);
        }
    }

    [Fact]
    public async Task The_host_refuses_to_start_without_a_skiptoken_key()
    {
        WebApplicationBuilder builder = WebApplication.CreateSlimBuilder();
        builder.WebHost.UseTestServer();
        builder.Logging.ClearProviders();
        builder.AddViaticaApi(static options => options.SupportedApiVersions.Add(TestApi.Version));
        await using WebApplication app = builder.Build();

        var error = await Should.ThrowAsync<Microsoft.Extensions.Options.OptionsValidationException>(() => app.StartAsync());
        error.Message.ShouldContain("SkipTokenKey");
    }
}
