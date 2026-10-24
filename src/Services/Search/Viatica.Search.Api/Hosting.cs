using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.Extensions.Diagnostics.HealthChecks;
using Viatica.Search.Api.Endpoints;
using Viatica.Search.Infrastructure.Indexing;

namespace Viatica.Search.Api;

[JsonSourceGenerationOptions(
    JsonSerializerDefaults.Web,
    UseStringEnumConverter = true,
    DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull)]
[JsonSerializable(typeof(StaySearchResponse))]
[JsonSerializable(typeof(CarSearchResponse))]
[JsonSerializable(typeof(AutocompleteResponse))]
internal sealed partial class SearchJsonContext : JsonSerializerContext;

/// <summary>
/// Ready means OpenSearch answers and both aliases exist, which is exactly what a search needs. A fresh stack where
/// <c>migrate</c> has not run yet is therefore not ready, instead of answering every query with a 404 from the index.
/// </summary>
internal sealed class IndicesReadyCheck(OpenSearchRest rest) : IHealthCheck
{
    public async Task<HealthCheckResult> CheckHealthAsync(HealthCheckContext context, CancellationToken cancellationToken = default)
    {
        try
        {
            foreach (IndexKind kind in Enum.GetValues<IndexKind>())
            {
                if (!await rest.ExistsAsync($"_alias/{IndexDefinitions.AliasOf(kind)}", cancellationToken))
                {
                    return HealthCheckResult.Unhealthy($"The alias {IndexDefinitions.AliasOf(kind)} does not exist; run the migrate command.");
                }
            }

            return HealthCheckResult.Healthy();
        }
        catch (OpenSearchRequestException ex)
        {
            return HealthCheckResult.Unhealthy("OpenSearch did not answer.", ex);
        }
    }
}

/// <summary>What <c>healthcheck</c> does: ask the running server whether it is ready.</summary>
internal static class HealthProbe
{
    public static async Task<int> RunAsync()
    {
        string port = (Environment.GetEnvironmentVariable("ASPNETCORE_HTTP_PORTS") ?? "8080").Split(';')[0];
        using var client = new HttpClient { Timeout = TimeSpan.FromSeconds(3) };
        try
        {
            using HttpResponseMessage response = await client.GetAsync(new Uri($"http://localhost:{port}/health/ready"));
            return response.IsSuccessStatusCode ? 0 : 1;
        }
        catch (HttpRequestException)
        {
            return 1;
        }
        catch (TaskCanceledException)
        {
            return 1;
        }
    }
}

internal static partial class Log
{
    [LoggerMessage(Level = LogLevel.Information, Message = "Rebuilt from {Events} messages into {Stays} and {Cars} in {Elapsed}; removed {Removed}")]
    public static partial void Rebuilt(ILogger logger, long events, string stays, string cars, TimeSpan elapsed, string removed);
}
