extern alias sim;

using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.Logging;

namespace Viatica.Payments.IntegrationTests;

/// <summary>
/// The real payment provider simulator, hosted in process. The Payments host talks to it over HTTP exactly as it would to
/// a provider; the tests stage failures through the sim admin API, the way the system tests will, and read the ledger
/// back to prove what the provider really did.
/// </summary>
internal sealed class SimHost : WebApplicationFactory<sim::Program>
{
    public SimHost() => Admin = CreateClient();

    private HttpClient Admin { get; }

    /// <summary>A handler that reaches the sim without a socket. The Payments host provider client uses it.</summary>
    public HttpMessageHandler CreateProviderHandler() => Server.CreateHandler();

    /// <summary>No latency worth the name, nothing failing, and a hang long enough to outlast the Payments request timeout.</summary>
    public async Task QuietAsync(int hangMs = 6_000)
    {
        (await Admin.PostAsync("/admin/knobs/reset", content: null)).EnsureSuccessStatusCode();
        await SetKnobsAsync(new { LatencyMedianMs = 0, TimeoutHangMs = hangMs });
    }

    public async Task SetKnobsAsync(object patch)
    {
        using HttpResponseMessage response = await Admin.PatchAsJsonAsync("/admin/knobs", patch);
        response.EnsureSuccessStatusCode();
    }

    public Task<IReadOnlyList<JsonElement>> IntentsAsync() => ListAsync("/v1/payment_intents");

    public Task<IReadOnlyList<JsonElement>> RefundsAsync() => ListAsync("/v1/refunds");

    /// <summary>The authorisations the provider holds for a trip, found by the metadata Payments sent with them.</summary>
    public async Task<IReadOnlyList<JsonElement>> IntentsOfAsync(Guid tripId) =>
        [.. (await IntentsAsync()).Where(i => i.GetProperty("metadata").GetProperty("trip_id").GetString() == tripId.ToString("N"))];

    /// <summary>The operation the provider recorded under a key, or null.</summary>
    public async Task<JsonElement?> OperationAsync(string idempotencyKey)
    {
        using HttpResponseMessage response = await Admin.GetAsync($"/v1/operations?idempotency_key={Uri.EscapeDataString(idempotencyKey)}");
        return response.IsSuccessStatusCode ? JsonElement.Parse(await response.Content.ReadAsStringAsync()) : null;
    }

    private async Task<IReadOnlyList<JsonElement>> ListAsync(string path)
    {
        JsonElement body = JsonElement.Parse(await Admin.GetStringAsync(path));
        return [.. body.GetProperty("data").EnumerateArray()];
    }

    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        builder.UseEnvironment("Testing");
        builder.ConfigureLogging(static logging => logging.ClearProviders());
    }
}
