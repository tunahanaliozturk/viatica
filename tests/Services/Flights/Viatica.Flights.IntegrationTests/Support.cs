using System.Net;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using Confluent.Kafka;
using Viatica.Contracts;
using Viatica.Kernel.Pii;
using Viatica.Testing;

namespace Viatica.Flights.IntegrationTests;

/// <summary>Everything a test class needs, built once: the simulator, the Flights host, and a probe on the topic it writes.</summary>
public abstract class FlightsFixture(PostgresFixture postgres, KafkaFixture kafka, ValkeyFixture valkey) : IAsyncLifetime
{
    internal SimHost Sim { get; private set; } = null!;

    internal FlightsHost Host { get; private set; } = null!;

    internal EventProbe Probe { get; private set; } = null!;

    internal BookingSender Booking { get; private set; } = null!;

    internal HttpClient Client { get; private set; } = null!;

    internal virtual HostSettings Settings => new();

    public async ValueTask InitializeAsync()
    {
        Sim = await SimHost.StartAsync(postgres);
        Host = await FlightsHost.StartAsync(postgres, kafka, valkey, Sim, Settings);
        Probe = kafka.Probe(Host.FlightsTopic);
        Booking = new BookingSender(kafka.BootstrapServers, Host.BookingTopic);
        Client = Host.CreateClient();
    }

    public async ValueTask DisposeAsync()
    {
        Client?.Dispose();
        Booking?.Dispose();
        Probe?.Dispose();
        if (Host is not null)
        {
            await Host.DisposeAsync();
        }

        if (Sim is not null)
        {
            await Sim.DisposeAsync();
        }

        GC.SuppressFinalize(this);
    }
}

/// <summary>Puts booking events on the topic Flights consumes, in the envelope Kernel.Messaging writes.</summary>
internal sealed class BookingSender : IDisposable
{
    private readonly IProducer<string, byte[]> producer;
    private readonly string topic;

    public BookingSender(string bootstrapServers, string topic)
    {
        this.topic = topic;
        producer = new ProducerBuilder<string, byte[]>(new ProducerConfig { BootstrapServers = bootstrapServers, Acks = Acks.All, EnableIdempotence = true }).Build();
    }

    /// <summary>Sends an event. Pass the same <paramref name="eventId"/> twice to deliver one event twice.</summary>
    public async Task SendAsync(IIntegrationEvent message, Guid? eventId = null)
    {
        string type = message.GetType().Name;
        string data = JsonSerializer.Serialize(message, message.GetType(), ContractsJsonContext.Default);
        string envelope = $$"""{"id":"{{eventId ?? Guid.CreateVersion7()}}","type":"{{type}}","version":1,"occurredAt":"{{DateTimeOffset.UtcNow:O}}","traceparent":null,"data":{{data}}}""";

        await producer.ProduceAsync(topic, new Message<string, byte[]> { Key = message.PartitionKey, Value = Encoding.UTF8.GetBytes(envelope) });
    }

    public void Dispose()
    {
        producer.Flush(TimeSpan.FromSeconds(5));
        producer.Dispose();
    }
}

internal static class Travel
{
    /// <summary>Three weeks out. Far enough for the airline's two hour notice, near enough for every schedule to exist.</summary>
    public static DateOnly Departure => DateOnly.FromDateTime(DateTime.UtcNow).AddDays(21);

    public static DateOnly Return => Departure.AddDays(4);

    public static string V(string url) => url + (url.Contains('?', StringComparison.Ordinal) ? "&" : "?") + "api-version=" + FlightsHost.ApiVersion;

    /// <summary>A search on a core route, which all four airlines fly.</summary>
    public static object IstanbulToLondon(int adults = 1, int children = 0, int infants = 0, DateOnly? returnDate = null) => new
    {
        origin = "IST",
        destination = "LHR",
        departureDate = Departure.ToString("yyyy-MM-dd"),
        returnDate = returnDate?.ToString("yyyy-MM-dd"),
        adults,
        children,
        infants,
        cabin = "Economy",
    };
}

internal static class ApiCalls
{
    public static async Task<(HttpStatusCode Status, JsonElement Body)> SearchAsync(this HttpClient client, object body, TimeSpan? within = null)
    {
        using var cts = new CancellationTokenSource(within ?? TimeSpan.FromSeconds(30));
        using HttpResponseMessage response = await client.PostAsJsonAsync(Travel.V("/flights/searches"), body, cts.Token);
        return (response.StatusCode, await ReadAsync(response));
    }

    public static async Task<(HttpStatusCode Status, JsonElement Body)> GetJsonAsync(this HttpClient client, string url)
    {
        using HttpResponseMessage response = await client.GetAsync(url);
        return (response.StatusCode, await ReadAsync(response));
    }

    public static async Task<(HttpStatusCode Status, JsonElement Body)> RepriceAsync(this HttpClient client, string offerId)
    {
        using HttpResponseMessage response = await client.PostAsync(Travel.V($"/flights/offers/{offerId}/price"), content: null);
        return (response.StatusCode, await ReadAsync(response));
    }

    /// <summary>Follows nextLink to the end and returns every page's offers.</summary>
    public static async Task<List<JsonElement>> AllOffersAsync(this HttpClient client, JsonElement firstPage)
    {
        List<JsonElement> offers = [.. firstPage.GetProperty("value").EnumerateArray()];
        JsonElement page = firstPage;

        while (page.TryGetProperty("nextLink", out JsonElement next))
        {
            (HttpStatusCode status, page) = await client.GetJsonAsync(next.GetString()!);
            status.ShouldBe(HttpStatusCode.OK);
            offers.AddRange(page.GetProperty("value").EnumerateArray());
        }

        return offers;
    }

    public static IEnumerable<JsonElement> From(this IEnumerable<JsonElement> offers, string airline) =>
        offers.Where(offer => offer.GetProperty("airline").GetString() == airline);

    public static decimal Amount(this JsonElement money) =>
        decimal.Parse(money.GetProperty("amount").GetString()!, System.Globalization.CultureInfo.InvariantCulture);

    public static async Task<JsonElement> ReadAsync(HttpResponseMessage response)
    {
        string text = await response.Content.ReadAsStringAsync();
        return text.Length == 0 ? default : JsonElement.Parse(text);
    }
}
