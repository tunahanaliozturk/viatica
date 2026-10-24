extern alias sim;

using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Time.Testing;
using sim::Viatica.AirlineSim.Orders;
using Viatica.Testing;

namespace Viatica.Flights.IntegrationTests;

/// <summary>
/// The real airline simulator, hosted in process on its own Postgres database. The Flights host talks to it over
/// HTTP exactly as it would to four airlines; the tests stage failures through the sim's admin API, the way the
/// system tests will.
/// </summary>
internal sealed class SimHost : WebApplicationFactory<sim::Program>
{
    public static readonly string[] Airlines = ["ZC", "YZ", "JG", "ZJ"];

    private readonly string connectionString;

    private SimHost(string connectionString) => this.connectionString = connectionString;

    /// <summary>
    /// The sim's clock. It starts at the real time, because the Flights host compares offer expiry with the real
    /// clock; a test that needs the void window to close moves it forward a day.
    /// </summary>
    public FakeTimeProvider Clock { get; } = new(DateTimeOffset.UtcNow);

    /// <summary>A handler that reaches the sim without a socket. The Flights host's airline clients use it.</summary>
    public HttpMessageHandler CreateAirlineHandler() => Server.CreateHandler();

    public static async Task<SimHost> StartAsync(PostgresFixture postgres)
    {
        var host = new SimHost(await postgres.CreateDatabaseAsync("airlinesim"));
        await host.InDbAsync(static db => db.Database.EnsureCreatedAsync());
        await host.QuietAsync();
        return host;
    }

    /// <summary>
    /// Every airline fast and well behaved, whatever an earlier test left behind: 20 ms with no spread, no outage, no
    /// refusals, no timeouts, no price changes. A test then switches on the one misbehaviour it is about.
    /// </summary>
    public async Task QuietAsync()
    {
        using HttpClient admin = CreateClient();
        (await admin.PostAsync("/admin/knobs/reset", content: null)).EnsureSuccessStatusCode();

        foreach (string airline in Airlines)
        {
            await SetKnobsAsync(airline, new
            {
                LatencyMedianMs = 20,
                LatencySigma = 0,
                Outage = false,
                TicketingRefusalRate = 0,
                OrderTimeoutRate = 0,
                CreateAnywayRate = 0,
                PriceChangeRate = 0,
            });
        }
    }

    public async Task SetKnobsAsync(string airline, object patch)
    {
        using HttpClient admin = CreateClient();
        (await admin.PatchAsJsonAsync($"/admin/airlines/{airline}/knobs", patch)).EnsureSuccessStatusCode();
    }

    /// <summary>The order the airline holds under our idempotency key, or null.</summary>
    public async Task<JsonElement?> FindOrderAsync(string airline, string idempotencyKey)
    {
        using HttpClient client = CreateClient();
        using HttpResponseMessage response = await client.GetAsync($"/ndc/{airline}/orders?idempotencyKey={Uri.EscapeDataString(idempotencyKey)}");
        return response.IsSuccessStatusCode ? JsonElement.Parse(await response.Content.ReadAsStringAsync()) : null;
    }

    public async Task<JsonElement> GetOrderAsync(string airline, string bookingReference)
    {
        using HttpClient client = CreateClient();
        return JsonElement.Parse(await client.GetStringAsync($"/ndc/{airline}/orders/{bookingReference}"));
    }

    public Task<int> CountOrdersAsync(string airline) =>
        InDbAsync(db => db.Orders.CountAsync(o => o.Airline == airline));

    /// <summary>Seats left in a bucket, or null when nothing has been sold from it yet.</summary>
    public Task<int?> SeatsLeftAsync(string flightNumber, DateOnly date, char fareClass) =>
        InDbAsync(db => db.SeatBuckets
            .Where(b => b.FlightNumber == flightNumber && b.FlightDate == date && b.FareClass == fareClass)
            .Select(b => (int?)b.Remaining)
            .FirstOrDefaultAsync());

    /// <summary>Sets the seats left in one bucket, as if earlier sales had happened.</summary>
    public Task<int> SetSeatsAsync(string flightNumber, DateOnly date, char fareClass, int remaining) =>
        InDbAsync(db => db.Database.ExecuteSqlAsync(
            $"""
            insert into seat_buckets (airline, flight_number, flight_date, fare_class, remaining)
            values ({flightNumber[..2]}, {flightNumber}, {date}, {fareClass}, {remaining})
            on conflict (airline, flight_number, flight_date, fare_class) do update set remaining = excluded.remaining
            """));

    private async Task<T> InDbAsync<T>(Func<AirlineSimDb, Task<T>> work)
    {
        await using AsyncServiceScope scope = Services.CreateAsyncScope();
        return await work(scope.ServiceProvider.GetRequiredService<AirlineSimDb>());
    }

    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        builder.UseEnvironment("Testing");
        builder.UseSetting("ConnectionStrings:AirlineSim", connectionString);
        builder.ConfigureLogging(static logging => logging.ClearProviders());
        builder.ConfigureTestServices(services => services.AddSingleton<TimeProvider>(Clock));
    }
}
