using System.Net;
using System.Text;
using System.Text.Json;

namespace Viatica.Kernel.UnitTests;

internal static class Http
{
    public static string V(string url) =>
        url + (url.Contains('?', StringComparison.Ordinal) ? "&" : "?") + "api-version=" + TestApi.Version;

    public static StringContent Json(string json) => new(json, Encoding.UTF8, "application/json");

    public static async Task<JsonElement> Body(this HttpResponseMessage response)
    {
        string text = await response.Content.ReadAsStringAsync();
        return JsonElement.Parse(text);
    }

    /// <summary>Asserts the status and the envelope, and returns the <c>error</c> object for further checks.</summary>
    public static async Task<JsonElement> ShouldBeError(this HttpResponseMessage response, HttpStatusCode status, string code)
    {
        response.StatusCode.ShouldBe(status);
        response.Content.Headers.ContentType?.MediaType.ShouldBe("application/json");

        JsonElement body = await response.Body();
        body.EnumerateObject().Select(static p => p.Name).ShouldBe(["error"]);

        JsonElement error = body.GetProperty("error");
        error.GetProperty("code").GetString().ShouldBe(code);
        error.GetProperty("message").GetString().ShouldNotBeNullOrWhiteSpace();
        return error;
    }

    public static string? Str(this JsonElement element, string name) =>
        element.TryGetProperty(name, out JsonElement value) ? value.GetString() : null;
}
