using System.Buffers.Text;
using System.Globalization;
using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using Confluent.Kafka;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Time.Testing;
using Viatica.Booking.Infrastructure.Persistence;
using Viatica.Contracts;
using Viatica.Kernel.Pii;
using Viatica.Testing;

namespace Viatica.Booking.IntegrationTests;

/// <summary>
/// The real Booking service: its own Postgres database (migrated by its own migration), Kafka topics of its own, a clock
/// the test moves, and every log entry kept. The saga's clock, the scheduled check and the consumers are the
/// production ones.
/// </summary>
internal sealed class BookingHost : WebApplicationFactory<Program>
{
    public const string Version = "2026-11-01";
    public const string Audience = "booking-api";

    private readonly Dictionary<string, string?> settings;

    private BookingHost(Dictionary<string, string?> settings, FakeTimeProvider clock, CapturedLogs logs)
    {
        this.settings = settings;
        Clock = clock;
        Logs = logs;
    }

    public FakeTimeProvider Clock { get; }

    public CapturedLogs Logs { get; }

    public byte[] PassengerKey { get; } = RandomNumberGenerator.GetBytes(PiiSealer.KeyLength);

    public byte[] StorageKey { get; } = RandomNumberGenerator.GetBytes(PiiSealer.KeyLength);

    public static async Task<BookingHost> StartAsync(PostgresFixture postgres, FakeTimeProvider clock, BookingTopics topics, string bootstrap, byte[] passengerKey, byte[] storageKey)
    {
        string connection = new Npgsql.NpgsqlConnectionStringBuilder(await postgres.CreateDatabaseAsync("booking")) { MaxPoolSize = 20 }.ConnectionString;

        await using (var db = new BookingDbContext(new DbContextOptionsBuilder<BookingDbContext>().UseNpgsql(connection).Options))
        {
            await db.Database.MigrateAsync();
        }

        var settings = new Dictionary<string, string?>
        {
            ["ConnectionStrings:Booking"] = connection,
            ["Kafka:BootstrapServers"] = bootstrap,
            ["Kafka:BookingTopic"] = topics.Booking,
            ["Kafka:StaysTopic"] = topics.Stays,
            ["Kafka:CarsTopic"] = topics.Cars,
            ["Kafka:FlightsTopic"] = topics.Flights,
            ["Kafka:PaymentsTopic"] = topics.Payments,
            ["Viatica:Api:SupportedApiVersions:0"] = Version,
            ["Viatica:Api:SkipTokenKey"] = Convert.ToBase64String(RandomNumberGenerator.GetBytes(32)),
            ["Viatica:Auth:TestSigningKey"] = TestTokens.SigningKeyBase64,
            ["Viatica:Pii:PassengerKey"] = Convert.ToBase64String(passengerKey),
            ["Viatica:Pii:StorageKey"] = Convert.ToBase64String(storageKey),
            // Fast enough that a test waits for the host, never so fast that the host is the test.
            ["Booking:CheckInterval"] = "00:00:00.100",
            ["Booking:ProgressPollInterval"] = "00:00:00.100",
        };

        var host = new BookingHost(settings, clock, new CapturedLogs());
        _ = host.Services; // starts the host, and with it the relay, the four consumers and the scheduled check
        return host;
    }

    public async Task<T> InDbAsync<T>(Func<BookingDbContext, Task<T>> work)
    {
        await using AsyncServiceScope scope = Services.CreateAsyncScope();
        return await work(scope.ServiceProvider.GetRequiredService<BookingDbContext>());
    }

    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        builder.UseEnvironment("Testing");
        foreach ((string key, string? value) in settings)
        {
            builder.UseSetting(key, value);
        }

        builder.ConfigureLogging(logging =>
        {
            logging.ClearProviders();
            logging.AddProvider(Logs);
            // Everything, from every category: the log test must see what a debugging session would see.
            logging.SetMinimumLevel(LogLevel.Trace);
            logging.AddFilter<CapturedLogs>(null, LogLevel.Trace);
        });

        builder.ConfigureTestServices(services =>
        {
            services.RemoveAll<TimeProvider>();
            services.AddSingleton<TimeProvider>(Clock);
        });
    }
}

/// <summary>
/// Booking, the four owners as stand-ins, a probe on <c>booking.v1</c> and the calls a client makes. One per test class.
/// </summary>
internal sealed class BookingWorld : IAsyncDisposable
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web)
    {
        Converters = { new JsonStringEnumConverter() },
    };

    private HttpClient? client;

    private BookingWorld(BookingHost host, Owners owners, EventProbe probe, BookingTopics topics)
    {
        Host = host;
        Owners = owners;
        Probe = probe;
        Topics = topics;
    }

    public BookingHost Host { get; }

    public Owners Owners { get; }

    /// <summary>Reads what Booking published, from the start, the way any other service would.</summary>
    public EventProbe Probe { get; }

    public BookingTopics Topics { get; }

    public FakeTimeProvider Clock => Host.Clock;

    public Guid TravellerId { get; } = Guid.CreateVersion7();

    public string Traveller => TestTokens.Traveller(BookingHost.Audience, TravellerId);

    public static async Task<BookingWorld> StartAsync(PostgresFixture postgres, KafkaFixture kafka)
    {
        var topics = new BookingTopics(
            await kafka.CreateTopicAsync("booking"),
            await kafka.CreateTopicAsync("stays"),
            await kafka.CreateTopicAsync("cars"),
            await kafka.CreateTopicAsync("flights"),
            await kafka.CreateTopicAsync("payments"));

        var clock = new FakeTimeProvider(DateTimeOffset.UtcNow);
        byte[] passengerKey = RandomNumberGenerator.GetBytes(PiiSealer.KeyLength);
        byte[] storageKey = RandomNumberGenerator.GetBytes(PiiSealer.KeyLength);

        BookingHost host = await BookingHost.StartAsync(postgres, clock, topics, kafka.BootstrapServers, passengerKey, storageKey);
        Owners owners = await Owners.StartAsync(postgres, kafka, topics, clock, passengerKey);
        return new BookingWorld(host, owners, kafka.Probe(topics.Booking), topics) { Bootstrap = kafka.BootstrapServers };
    }

    private HttpClient Client => client ??= Host.CreateClient();

    public static string TravellerToken(Guid? subject = null) => TestTokens.Traveller(BookingHost.Audience, subject);

    public static string PartnerToken(Guid partnerId) => TestTokens.Partner(BookingHost.Audience, partnerId);

    public static string AdminToken() => TestTokens.Admin(BookingHost.Audience);

    // The calls.

    public Task<HttpResponseMessage> SendAsync(
        HttpMethod method, string path, object? body = null, string? token = null, IDictionary<string, string>? headers = null, bool withVersion = true)
    {
        string url = withVersion ? path + (path.Contains('?', StringComparison.Ordinal) ? "&" : "?") + "api-version=" + BookingHost.Version : path;
        var request = new HttpRequestMessage(method, url);

        if (body is not null)
        {
            request.Content = JsonContent.Create(body, options: Json);
        }

        if (token is not null)
        {
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        }

        foreach ((string name, string value) in headers ?? new Dictionary<string, string>())
        {
            request.Headers.TryAddWithoutValidation(name, value);
        }

        return Client.SendAsync(request);
    }

    public static async Task<JsonElement> JsonAsync(HttpResponseMessage response)
    {
        using JsonDocument document = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        return document.RootElement.Clone();
    }

    public static async Task<string> ErrorCodeAsync(HttpResponseMessage response) =>
        (await JsonAsync(response)).GetProperty("error").GetProperty("code").GetString()!;

    /// <summary>Starts a draft trip for the signed-in traveller and returns its id.</summary>
    public async Task<Guid> NewTripAsync(string? token = null)
    {
        Guid tripId = Guid.CreateVersion7();
        HttpResponseMessage created = await SendAsync(HttpMethod.Put, $"/trips/{tripId}", token: token ?? Traveller);
        created.StatusCode.ShouldBe(HttpStatusCode.Created);
        return tripId;
    }

    public async Task<Guid> AddStayAsync(Guid tripId, ItemPlan? plan = null, string? token = null, IDictionary<string, string>? headers = null)
    {
        Guid itemId = Guid.CreateVersion7();
        Owners.Plan(itemId, plan ?? new ItemPlan { Total = 300m });
        (await SendAsync(HttpMethod.Put, $"/trips/{tripId}/items/{itemId}", new { kind = "Stay", quoteToken = StayQuote((plan?.Total ?? 300m)) }, token ?? (headers is null ? Traveller : null), headers))
            .StatusCode.ShouldBe(HttpStatusCode.Created);
        return itemId;
    }

    public async Task<Guid> AddCarAsync(Guid tripId, ItemPlan? plan = null, string? token = null)
    {
        Guid itemId = Guid.CreateVersion7();
        Owners.Plan(itemId, plan ?? new ItemPlan { Total = 90m });
        (await SendAsync(HttpMethod.Put, $"/trips/{tripId}/items/{itemId}", new { kind = "Car", quoteToken = CarQuote((plan?.Total ?? 90m)) }, token ?? Traveller))
            .StatusCode.ShouldBe(HttpStatusCode.Created);
        return itemId;
    }

    public async Task<Guid> AddFlightAsync(Guid tripId, ItemPlan? plan = null, string? token = null)
    {
        Guid itemId = Guid.CreateVersion7();
        decimal total = plan?.Total ?? 150m;
        Owners.Plan(itemId, plan ?? new ItemPlan { Total = total });
        (await SendAsync(
            HttpMethod.Put,
            $"/trips/{tripId}/items/{itemId}",
            new { kind = "Flight", offerId = "offer-" + itemId.ToString("N")[..12], expectedTotal = new { amount = total.ToString("0.00", CultureInfo.InvariantCulture), currency = "EUR" }, description = "IST to LHR" },
            token ?? Traveller)).StatusCode.ShouldBe(HttpStatusCode.Created);
        return itemId;
    }

    /// <summary>The stay, the car and the flight of a typical trip, with the prices the stand-ins will confirm.</summary>
    public async Task<(Guid Trip, Guid Stay, Guid Car, Guid Flight)> BasketAsync(
        ItemPlan? stay = null, ItemPlan? car = null, ItemPlan? flight = null)
    {
        Guid trip = await NewTripAsync();
        Guid s = await AddStayAsync(trip, stay);
        Guid c = await AddCarAsync(trip, car);
        Guid f = await AddFlightAsync(trip, flight);
        return (trip, s, c, f);
    }

    public static object Details(string given = "Zeynebnur", string payment = "pm_test_visa") => new
    {
        contact = new { email = "zeynebnur@example.test", phone = "+905551112233" },
        passengers = new[]
        {
            new { type = "Adult", givenName = given, surname = "Karabulutoglu", birthDate = "1987-05-17", documentNumber = "TR8675309X" },
        },
        paymentMethodToken = payment,
    };

    public Task<HttpResponseMessage> CheckoutAsync(Guid tripId, object? details = null, string? token = null) =>
        SendAsync(HttpMethod.Put, $"/trips/{tripId}/checkout", details ?? Details(), token ?? Traveller);

    public async Task<JsonElement> TripAsync(Guid tripId, string? token = null) =>
        await JsonAsync(await SendAsync(HttpMethod.Get, $"/trips/{tripId}", token: token ?? Traveller));

    public async Task<JsonElement> WaitForTripAsync(Guid tripId, string status, string? token = null) =>
        await Eventually.MatchesAsync(
            async () => await TripAsync(tripId, token),
            trip => trip.GetProperty("status").GetString() == status,
            TimeSpan.FromSeconds(45));

    public static string ItemStatus(JsonElement trip, Guid itemId) =>
        trip.GetProperty("items").EnumerateArray().Single(i => i.GetProperty("id").GetGuid() == itemId).GetProperty("status").GetString()!;

    /// <summary>Waits for an event of the type about the trip on Booking's own topic.</summary>
    public Task<ProbedMessage> NextAsync(string type, Guid tripId, Func<ProbedMessage, bool>? where = null) =>
        Probe.WaitForAsync(m => m.Type == type && m.Key == tripId.ToString("N") && (where is null || where(m)));

    /// <summary>Waits until the event has been published at least <paramref name="count"/> times, and returns every one.</summary>
    public Task<IReadOnlyList<ProbedMessage>> AllAsync(string type, Guid tripId, int count = 1) =>
        Probe.WaitForCountAsync(count, m => m.Type == type && m.Key == tripId.ToString("N"));

    /// <summary>Reads the server sent events of a trip until the server closes the stream. Each is the event id and the trip it carried.</summary>
    public async Task<IReadOnlyList<(int Id, JsonElement Trip)>> ReadProgressAsync(Guid tripId, int? lastEventId = null, string? token = null)
    {
        var request = new HttpRequestMessage(HttpMethod.Get, $"/trips/{tripId}/events?api-version={BookingHost.Version}");
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token ?? Traveller);
        if (lastEventId is { } last)
        {
            request.Headers.TryAddWithoutValidation("Last-Event-ID", last.ToString(CultureInfo.InvariantCulture));
        }

        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(60));
        using HttpResponseMessage response = await Client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, timeout.Token);
        response.StatusCode.ShouldBe(HttpStatusCode.OK);
        response.Content.Headers.ContentType!.MediaType.ShouldBe("text/event-stream");

        List<(int, JsonElement)> events = [];
        using var reader = new StreamReader(await response.Content.ReadAsStreamAsync(timeout.Token));
        int id = -1;
        string? line;
        while ((line = await reader.ReadLineAsync(timeout.Token)) is not null)
        {
            if (line.StartsWith("id: ", StringComparison.Ordinal))
            {
                id = int.Parse(line[4..], CultureInfo.InvariantCulture);
            }
            else if (line.StartsWith("data: ", StringComparison.Ordinal))
            {
                using JsonDocument document = JsonDocument.Parse(line[6..]);
                events.Add((id, document.RootElement.Clone()));
            }
        }

        return events;
    }

    /// <summary>
    /// Puts a message on a topic again, byte for byte: the same envelope, the same event id. That is what a broker
    /// redelivering a message looks like to a consumer, and what the inbox exists to make harmless.
    /// </summary>
    public async Task ReplayAsync(string topic, ProbedMessage message)
    {
        using IProducer<string, byte[]> producer = new ProducerBuilder<string, byte[]>(new ProducerConfig { BootstrapServers = Bootstrap }).Build();
        await producer.ProduceAsync(topic, new Message<string, byte[]> { Key = message.Key, Value = message.Value });
    }

    public string Bootstrap { get; set; } = "";

    /// <summary>How many events Booking's consumers have taken in, by event id. A duplicate with a new id counts too.</summary>
    public Task<int> InboxCountAsync() =>
        Host.InDbAsync(db => db.Set<Viatica.Kernel.Messaging.InboxMessage>().CountAsync());

    /// <summary>
    /// Waits until Booking has taken in everything the owners have said so far. It says one more thing on each owner's
    /// topic, under the trip's key, that means nothing to Booking; the consumer of a topic reads a key's messages in
    /// order, so once all four are in the inbox everything before them on those topics has been handled. It then waits
    /// for the inbox to stop growing, for the messages on the other topics that were in flight meanwhile.
    /// </summary>
    public async Task SettledAsync(Guid tripId)
    {
        int before = await InboxCountAsync();
        await Owners.SayAsync(new StayHoldExpired(Guid.NewGuid(), tripId, Guid.NewGuid()));
        await Owners.SayAsync(new CarHoldExpired(Guid.NewGuid(), tripId, Guid.NewGuid()));
        await Owners.SayAsync(new FlightOrderVoided(Guid.NewGuid(), tripId, Guid.NewGuid()));
        await Owners.SayAsync(new PaymentVoided(Guid.NewGuid(), tripId));
        await Eventually.MatchesAsync(InboxCountAsync, count => count >= before + 4);

        int last = -1;
        DateTimeOffset since = DateTimeOffset.UtcNow;
        while (DateTimeOffset.UtcNow - since < TimeSpan.FromMilliseconds(600))
        {
            await Task.Delay(100);
            int now = await InboxCountAsync();
            if (now != last)
            {
                last = now;
                since = DateTimeOffset.UtcNow;
            }
        }
    }

    public async ValueTask DisposeAsync()
    {
        client?.Dispose();
        Probe.Dispose();
        await Owners.DisposeAsync();
        await Host.DisposeAsync();
    }

    // Quote tokens as the owners issue them: readable payload, a signature Booking does not check.

    public string StayQuote(decimal total)
    {
        DateOnly checkIn = DateOnly.FromDateTime(Clock.GetUtcNow().UtcDateTime).AddDays(30);
        return Token(new Dictionary<string, object?>
        {
            ["v"] = 1,
            ["kind"] = "stay",
            ["propertyId"] = Guid.NewGuid(),
            ["roomTypeId"] = Guid.NewGuid(),
            ["checkIn"] = checkIn.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture),
            ["checkOut"] = checkIn.AddDays(3).ToString("yyyy-MM-dd", CultureInfo.InvariantCulture),
            ["quantity"] = 1,
            ["total"] = new { amount = total.ToString("0.00", CultureInfo.InvariantCulture), currency = "EUR" },
            ["expiresAt"] = Clock.GetUtcNow().AddMinutes(15),
        });
    }

    public string CarQuote(decimal total)
    {
        DateTimeOffset pickUp = Clock.GetUtcNow().AddDays(30);
        return Token(new Dictionary<string, object?>
        {
            ["v"] = 1,
            ["kind"] = "car",
            ["locationId"] = Guid.NewGuid(),
            ["categoryId"] = Guid.NewGuid(),
            ["dropOffLocationId"] = Guid.NewGuid(),
            ["pickUpAt"] = pickUp,
            ["dropOffAt"] = pickUp.AddDays(3),
            ["driverAge"] = 30,
            ["total"] = new { amount = total.ToString("0.00", CultureInfo.InvariantCulture), currency = "EUR" },
            ["expiresAt"] = Clock.GetUtcNow().AddMinutes(15),
        });
    }

    private static string Token(Dictionary<string, object?> payload) =>
        Base64Url.EncodeToString(JsonSerializer.SerializeToUtf8Bytes(payload, Json)) + "." + Base64Url.EncodeToString(RandomNumberGenerator.GetBytes(32));
}
