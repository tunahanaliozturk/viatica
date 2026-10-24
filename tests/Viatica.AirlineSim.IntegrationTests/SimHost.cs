using System.Globalization;
using System.Net.Http.Json;
using System.Text.Json.Serialization.Metadata;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Time.Testing;
using Npgsql;
using Testcontainers.PostgreSql;
using Viatica.AirlineSim.Api;
using Viatica.AirlineSim.Behaviour;
using Viatica.AirlineSim.Orders;

[assembly: AssemblyFixture(typeof(Viatica.AirlineSim.IntegrationTests.Postgres))]

namespace Viatica.AirlineSim.IntegrationTests;

/// <summary>One Postgres for the whole assembly. Every sim host gets its own database in it.</summary>
public sealed class Postgres : IAsyncLifetime
{
    // Pinned by digest, so a run next month tests against the same Postgres build as today.
    private readonly PostgreSqlContainer _container =
        new PostgreSqlBuilder("postgres:18@sha256:86c951e05bf56c93d95d397747fb8820ac76cc3bedb78f43abd83eedbe3666ae").Build();

    public async ValueTask InitializeAsync() => await _container.StartAsync();

    public async ValueTask DisposeAsync() => await _container.DisposeAsync();

    internal async Task<string> CreateDatabaseAsync()
    {
        string name = $"airlinesim_{Guid.NewGuid():N}";
        await using (var connection = new NpgsqlConnection(_container.GetConnectionString()))
        {
            await connection.OpenAsync();
            await using NpgsqlCommand command = connection.CreateCommand();
            command.CommandText = $"create database \"{name}\"";
            await command.ExecuteNonQueryAsync();
        }

        return new NpgsqlConnectionStringBuilder(_container.GetConnectionString()) { Database = name }.ConnectionString;
    }
}

/// <summary>
/// The real sim host on its own database, with a fake clock for the business rules that are about time (offer
/// expiry, ticketing time limit, void window). Injected latency stays on the real clock: slow has to be slow.
/// </summary>
internal sealed class SimHost : WebApplicationFactory<Program>
{
    public const int DefaultSeed = 20261024;

    /// <summary>A Monday morning. Searches in the tests travel three weeks later.</summary>
    public static readonly DateTimeOffset Start = new(2026, 11, 2, 8, 0, 0, TimeSpan.Zero);

    public static readonly DateOnly Outbound = new(2026, 11, 23);

    public static readonly DateOnly Inbound = new(2026, 11, 27);

    private readonly string _connectionString;
    private readonly int _seed;
    private HttpClient? _client;

    private SimHost(string connectionString, int seed)
    {
        _connectionString = connectionString;
        _seed = seed;
    }

    public FakeTimeProvider Clock { get; } = new(Start);

    public HttpClient Client => _client ??= CreateClient();

    public Behaviours Behaviours => Services.GetRequiredService<Behaviours>();

    /// <summary>Starts a host with its schema in place. Latency is off unless asked for, to keep the suite fast.</summary>
    public static async Task<SimHost> StartAsync(Postgres postgres, int seed = DefaultSeed, bool latency = false)
    {
        var host = new SimHost(await postgres.CreateDatabaseAsync(), seed);
        await host.InDbAsync(static db => db.Database.EnsureCreatedAsync());

        if (!latency)
        {
            foreach (AirlineState airline in host.Behaviours.All)
            {
                airline.Set(airline.Knobs with { LatencyMedianMs = 0 });
            }
        }

        return host;
    }

    public async Task<T> InDbAsync<T>(Func<AirlineSimDb, Task<T>> work)
    {
        await using AsyncServiceScope scope = Services.CreateAsyncScope();
        return await work(scope.ServiceProvider.GetRequiredService<AirlineSimDb>());
    }

    /// <summary>Sets the seats left in one bucket, as if earlier sales had happened.</summary>
    public Task<int> SetSeatsAsync(string flightNumber, DateOnly date, char fareClass, int remaining) =>
        InDbAsync(db => db.Database.ExecuteSqlAsync(
            $"""
            insert into seat_buckets (airline, flight_number, flight_date, fare_class, remaining)
            values ({flightNumber[..2]}, {flightNumber}, {date}, {fareClass}, {remaining})
            on conflict (airline, flight_number, flight_date, fare_class) do update set remaining = excluded.remaining
            """));

    public Task<int> SeatsLeftAsync(string flightNumber, DateOnly date, char fareClass) =>
        InDbAsync(db => db.SeatBuckets
            .Where(b => b.FlightNumber == flightNumber && b.FlightDate == date && b.FareClass == fareClass)
            .Select(b => b.Remaining)
            .FirstAsync());

    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        builder.UseEnvironment("Testing");
        builder.UseSetting("ConnectionStrings:AirlineSim", _connectionString);
        builder.UseSetting("AirlineSim:Seed", _seed.ToString(CultureInfo.InvariantCulture));
        builder.ConfigureLogging(static logging => logging.SetMinimumLevel(LogLevel.Warning));
        builder.ConfigureTestServices(services => services.AddSingleton<TimeProvider>(Clock));
    }

    protected override void Dispose(bool disposing)
    {
        _client?.Dispose();
        base.Dispose(disposing);
    }
}

/// <summary>Calls the sim the way a supplier adapter would.</summary>
internal static class SimCalls
{
    public static Task<HttpResponseMessage> SearchAsync(
        this HttpClient client, string airline, string origin, string destination, DateOnly? returnDate = null,
        int adults = 1, int children = 0, int infants = 0, string cabin = "Economy") =>
        client.PostAsJsonAsync($"/ndc/{airline}/search", new
        {
            Origin = origin,
            Destination = destination,
            DepartureDate = SimHost.Outbound,
            ReturnDate = returnDate,
            Passengers = new { Adults = adults, Children = children, Infants = infants },
            Cabin = cabin,
        });

    public static async Task<OfferView> OfferAsync(
        this HttpClient client, string airline, string origin, string destination, char fareClass, DateOnly? returnDate = null, int adults = 1)
    {
        using HttpResponseMessage response = await client.SearchAsync(airline, origin, destination, returnDate, adults,
            cabin: fareClass == 'J' ? "Business" : "Economy");
        SearchResponse search = await response.ReadAsync(SimJson.Default.SearchResponse);
        return search.Offers.First(offer => offer.FareClass == fareClass.ToString());
    }

    public static Task<HttpResponseMessage> CreateOrderAsync(this HttpClient client, string airline, string offerId, string idempotencyKey, int adults = 1) =>
        client.PostAsJsonAsync($"/ndc/{airline}/orders", new
        {
            OfferId = offerId,
            IdempotencyKey = idempotencyKey,
            Passengers = Enumerable.Range(0, adults)
                .Select(i => new { Type = "ADT", GivenName = "Test", Surname = $"Passenger {(char)('A' + i)}", BirthDate = new DateOnly(1985, 6, 1) }),
        });

    public static async Task<OrderView> BookAsync(this HttpClient client, string airline, OfferView offer, int adults = 1)
    {
        using HttpResponseMessage response = await client.CreateOrderAsync(airline, offer.OfferId, Guid.NewGuid().ToString(), adults);
        response.StatusCode.ShouldBe(System.Net.HttpStatusCode.Created);
        return await response.ReadAsync(SimJson.Default.OrderView);
    }

    public static Task<HttpResponseMessage> OrderActionAsync(this HttpClient client, string airline, string bookingReference, string action) =>
        client.PostAsync($"/ndc/{airline}/orders/{bookingReference}/{action}", content: null);

    public static Task<HttpResponseMessage> PatchKnobsAsync(this HttpClient client, string airline, object patch) =>
        client.PatchAsJsonAsync($"/admin/airlines/{airline}/knobs", patch);

    public static async Task<T> ReadAsync<T>(this HttpResponseMessage response, JsonTypeInfo<T> type) =>
        (await response.Content.ReadFromJsonAsync(type))!;

    public static async Task<string> ErrorCodeAsync(this HttpResponseMessage response) =>
        (await response.ReadAsync(SimJson.Default.ErrorResponse)).Errors[0].Code;
}
