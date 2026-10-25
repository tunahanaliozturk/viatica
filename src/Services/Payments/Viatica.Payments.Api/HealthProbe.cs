namespace Viatica.Payments.Api;

/// <summary>
/// The container health check: ask this service's own port whether it is ready. The aspnet runtime image carries no
/// curl or wget, and a check that needs them never passes.
/// </summary>
internal static class HealthProbe
{
    public static async Task<int> RunAsync()
    {
        string port = Environment.GetEnvironmentVariable("ASPNETCORE_HTTP_PORTS")?.Split(';')[0] ?? "8080";

        try
        {
            using var client = new HttpClient { Timeout = TimeSpan.FromSeconds(3) };
            using HttpResponseMessage response = await client.GetAsync(new Uri($"http://localhost:{port}/health/ready"));
            return response.IsSuccessStatusCode ? 0 : 1;
        }
        catch (Exception exception) when (exception is HttpRequestException or TaskCanceledException)
        {
            return 1;
        }
    }
}
