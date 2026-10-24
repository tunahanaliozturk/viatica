using System.Net;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.Logging;

namespace Viatica.PspSim.IntegrationTests;

/// <summary>The real sim host in process, with every log entry it writes kept for the tests that search them.</summary>
internal sealed class SimHost : WebApplicationFactory<Program>
{
    public SimHost()
    {
        Client = CreateClient();
    }

    public HttpClient Client { get; }

    public CapturedLogs Logs { get; } = new();

    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        builder.UseEnvironment("Testing");
        builder.ConfigureLogging(logging =>
        {
            logging.ClearProviders();
            logging.AddProvider(Logs);
            // Everything, from every category: appsettings.json quiets Microsoft.AspNetCore, and a log test has to
            // see what a debugging session would see.
            logging.SetMinimumLevel(LogLevel.Trace);
            logging.AddFilter<CapturedLogs>(null, LogLevel.Trace);
        });
    }

    /// <summary>Knobs for a quiet provider: no latency, nothing failing, a short hang so timeout tests are quick.</summary>
    public async Task QuietAsync(int hangMs = 400)
    {
        (await Client.PostAsync("/admin/knobs/reset", content: null)).EnsureSuccessStatusCode();
        await SetKnobsAsync(new { LatencyMedianMs = 0, TimeoutHangMs = hangMs });
    }

    public async Task SetKnobsAsync(object patch)
    {
        using HttpResponseMessage response = await Client.PatchAsJsonAsync("/admin/knobs", patch);
        response.EnsureSuccessStatusCode();
    }

    public async Task<(HttpStatusCode Status, JsonElement Body, HttpResponseMessage Response)> PostAsync(string path, object? body, string? key)
    {
        using var request = new HttpRequestMessage(HttpMethod.Post, path);
        if (body is not null)
        {
            request.Content = new StringContent(JsonSerializer.Serialize(body), Encoding.UTF8, "application/json");
        }

        if (key is not null)
        {
            request.Headers.Add("Idempotency-Key", key);
        }

        HttpResponseMessage response = await Client.SendAsync(request);
        string text = await response.Content.ReadAsStringAsync();
        return (response.StatusCode, text.Length == 0 ? default : JsonElement.Parse(text), response);
    }

    public async Task<(HttpStatusCode Status, JsonElement Body)> GetAsync(string path)
    {
        using HttpResponseMessage response = await Client.GetAsync(path);
        string text = await response.Content.ReadAsStringAsync();
        bool isJson = response.Content.Headers.ContentType?.MediaType?.Contains("json", StringComparison.Ordinal) == true;
        return (response.StatusCode, isJson ? JsonElement.Parse(text) : default);
    }

    public static object Card(string number, int month = 12, int year = 2030, string cvc = "123") =>
        new { card = new { number, exp_month = month, exp_year = year, cvc } };

    public async Task<string> TokeniseAsync(string number, string? key = null)
    {
        (HttpStatusCode status, JsonElement body, _) = await PostAsync("/v1/tokens", Card(number), key ?? Guid.NewGuid().ToString());
        status.ShouldBe(HttpStatusCode.OK, body.ToString());
        return body.GetProperty("id").GetString()!;
    }

    public static object Authorisation(string token, long amount = 10_000) =>
        new { amount, currency = "eur", payment_method = token, capture_method = "manual", metadata = new { trip_id = "trip-1" } };

    public async Task<string> AuthoriseAsync(string number, long amount = 10_000)
    {
        string token = await TokeniseAsync(number);
        (HttpStatusCode status, JsonElement body, _) = await PostAsync("/v1/payment_intents", Authorisation(token, amount), Guid.NewGuid().ToString());
        status.ShouldBe(HttpStatusCode.OK, body.ToString());
        return body.GetProperty("id").GetString()!;
    }

    public async Task<IReadOnlyList<JsonElement>> IntentsAsync() => await ListAsync("/v1/payment_intents");

    public async Task<IReadOnlyList<JsonElement>> RefundsAsync() => await ListAsync("/v1/refunds");

    private async Task<IReadOnlyList<JsonElement>> ListAsync(string path)
    {
        (_, JsonElement body) = await GetAsync(path);
        return [.. body.GetProperty("data").EnumerateArray()];
    }
}

/// <summary>Every log entry the host wrote, with its structured values and any exception, at every level.</summary>
internal sealed class CapturedLogs : ILoggerProvider
{
    private readonly List<string> entries = [];

    public IReadOnlyList<string> Entries
    {
        get
        {
            lock (entries)
            {
                return [.. entries];
            }
        }
    }

    public ILogger CreateLogger(string categoryName) => new Capture(this, categoryName);

    public void Dispose()
    {
    }

    private sealed class Capture(CapturedLogs owner, string category) : ILogger
    {
        public IDisposable? BeginScope<TState>(TState state)
            where TState : notnull => null;

        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
        {
            string structured = state is IEnumerable<KeyValuePair<string, object?>> pairs
                ? string.Join(" ", pairs.Select(static p => $"{p.Key}={p.Value}"))
                : "";

            lock (owner.entries)
            {
                owner.entries.Add($"{category} [{logLevel}] {formatter(state, exception)} | {structured} | {exception}");
            }
        }
    }
}
