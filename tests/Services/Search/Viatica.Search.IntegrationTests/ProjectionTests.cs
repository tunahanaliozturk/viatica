using System.Text;
using System.Text.Json;
using Viatica.Contracts;
using Viatica.Testing;

namespace Viatica.Search.IntegrationTests;

/// <summary>
/// The path an event takes in production: onto Kafka, through the running consumers, into the index. Events of one
/// property share a Kafka key, so they reach one partition in the order they were sent; a last "marker" event for the
/// same property therefore says every earlier one has been handled.
/// </summary>
public sealed class ProjectionTests(SearchApp app) : IClassFixture<SearchApp>, IAsyncLifetime
{
    private static readonly TimeSpan Patience = TimeSpan.FromSeconds(60);

    public async ValueTask InitializeAsync() => await app.ResetAsync();

    public ValueTask DisposeAsync() => ValueTask.CompletedTask;

    [Fact]
    public async Task Stale_and_duplicate_events_are_dropped_and_counted()
    {
        using var metrics = new MetricRecorder();
        var room = new RoomSpec("Standard", 2, 3, 100m);
        var id = Guid.NewGuid();

        await app.PublishAsync(
            Seed.Listing(id, 5, "Current", "Lisbon", 4, 38.7, -9.1, ["wifi"], room),
            Seed.Listing(id, 3, "Older", "Lisbon", 1, 38.7, -9.1, [], room),
            Seed.Listing(id, 5, "Duplicate", "Lisbon", 2, 38.7, -9.1, [], room),
            new StayAvailabilityChanged(id, room.Id, 2, Seed.Range(0, 5, 3, 100m)),
            new StayAvailabilityChanged(id, room.Id, 1, Seed.Range(0, 5, 0, 1m)),
            new StayAvailabilityChanged(id, room.Id, 2, Seed.Range(0, 5, 3, 100m)),
            new StayAvailabilityChanged(id, room.Id, 3, Seed.Range(40, 1, 3, 100m)));

        JsonElement document = await WaitForAsync(id, static d => HasVersion(d, 3));

        document.GetProperty("name").GetString().ShouldBe("Current");
        document.GetProperty("listingVersion").GetInt64().ShouldBe(5);
        document.GetProperty("starRating").GetInt32().ShouldBe(4);
        JsonElement state = document.GetProperty("rooms")[0].GetProperty("state");
        state.GetProperty("available")[0].GetInt32().ShouldBe(3);
        state.GetProperty("rateCents")[0].GetInt64().ShouldBe(10000);
        metrics.Count("applied").ShouldBe(3);
        metrics.Count("dropped").ShouldBe(4);
    }

    [Fact]
    public async Task Availability_that_arrives_before_the_listing_waits_for_it_and_a_late_older_delta_loses()
    {
        var room = new RoomSpec("Standard", 2, 3, 100m);
        var id = Guid.NewGuid();
        string stay = $"checkIn={SearchApp.Base.AddDays(1):yyyy-MM-dd}&checkOut={SearchApp.Base.AddDays(4):yyyy-MM-dd}";

        // Newer delta first, the older one behind it, and no listing yet: the property cannot be found.
        await app.PublishAsync(
            new StayAvailabilityChanged(id, room.Id, 4, Seed.Range(0, 10, 2, 120m)),
            new StayAvailabilityChanged(id, room.Id, 2, Seed.Range(0, 10, 0, 50m)));
        await WaitForAsync(id, static d => HasVersion(d, 4));
        await app.RefreshAsync();
        (_, JsonElement before) = await app.GetAsync($"/stays/search?destination=lisbon&{stay}");
        before.GetProperty("value").GetArrayLength().ShouldBe(0);

        // The listing completes it; the property is found at the newer delta's price.
        await app.PublishAsync(Seed.Listing(id, 1, "Late Listing", "Lisbon", 3, 38.7, -9.1, [], room));
        JsonElement found = await Eventually.MatchesAsync(
            async () =>
            {
                await app.RefreshAsync();
                return (await app.GetAsync($"/stays/search?destination=lisbon&{stay}")).Body;
            },
            static body => body.GetProperty("value").GetArrayLength() == 1,
            Patience);

        found.GetProperty("value")[0].GetProperty("total").GetProperty("amount").GetString().ShouldBe("360.00");
    }

    [Fact]
    public async Task Other_event_types_on_the_topics_are_ignored_not_failed()
    {
        using var metrics = new MetricRecorder();
        var room = new RoomSpec("Standard", 2, 3, 100m);
        var id = Guid.NewGuid();

        await app.PublishAsync(
            new StayHoldPlaced(Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), id, room.Id, SearchApp.Base, SearchApp.Base.AddDays(2), 1, new Price(200m, "EUR"), DateTimeOffset.UtcNow),
            new StayBookingCancelled(Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid()),
            new CarHoldReleased(Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid()),
            Seed.Listing(id, 1, "After The Noise", "Lisbon", 3, 38.7, -9.1, [], room),
            new StayAvailabilityChanged(id, room.Id, 1, Seed.Range(0, 3, 1, 100m)));

        await WaitForAsync(id, static d => HasVersion(d, 1));

        metrics.Count("ignored").ShouldBeGreaterThanOrEqualTo(2);
        metrics.Count("dead_lettered").ShouldBe(0);
        metrics.Count("applied").ShouldBe(2);
    }

    [Fact]
    public async Task A_message_that_is_not_an_envelope_goes_to_the_dead_letter_topic_and_the_consumer_carries_on()
    {
        using EventProbe deadLetters = app.Kafka.Probe(app.StaysTopic + ".dlq");
        var room = new RoomSpec("Standard", 2, 3, 100m);
        var id = Guid.NewGuid();

        await app.PublishRawAsync(app.StaysTopic, id.ToString("N"), Encoding.UTF8.GetBytes("{not json"));
        await app.PublishAsync(
            Seed.Listing(id, 1, "Survivor", "Lisbon", 3, 38.7, -9.1, [], room),
            new StayAvailabilityChanged(id, room.Id, 1, Seed.Range(0, 3, 1, 100m)));

        ProbedMessage parked = await deadLetters.WaitForAsync(m => m.Key == id.ToString("N"), Patience);
        parked.IsEnvelope.ShouldBeFalse();
        parked.Headers["dlq-attempts"].ShouldBe("0");
        parked.Headers["dlq-topic"].ShouldBe(app.StaysTopic);
        parked.Headers.ShouldContainKey("dlq-error-type");
        await WaitForAsync(id, static d => HasVersion(d, 1));
    }

    [Fact]
    public async Task Projection_lag_is_the_time_from_the_events_occurred_at_to_its_index_write()
    {
        using var metrics = new MetricRecorder();
        var room = new RoomSpec("Standard", 2, 3, 100m);
        var id = Guid.NewGuid();
        DateTimeOffset occurred = DateTimeOffset.UtcNow.AddSeconds(-5);

        await app.PublishAsync(
            occurred,
            Seed.Listing(id, 1, "Lag", "Lisbon", 3, 38.7, -9.1, [], room),
            new StayAvailabilityChanged(id, room.Id, 1, Seed.Range(0, 3, 1, 100m)));
        await WaitForAsync(id, static d => HasVersion(d, 1));

        metrics.Lags.Count.ShouldBeGreaterThanOrEqualTo(2);
        metrics.Lags.ShouldAllBe(static lag => lag >= 5 && lag < 30);
    }

    [Fact]
    public async Task Car_events_take_the_same_path_into_the_cars_index()
    {
        var category = new CarSpec("ECMR", "Economy", 5, Transmission.Manual, 3, 35m);
        var id = Guid.NewGuid();

        await app.PublishAsync(Seed.Location(id, "Lisbon Airport", "Lisbon", 38.77, -9.13, Seed.CarPartner, "LIS", category));

        string trip = $"pickUpLocationId={id}&pickUpAt={SearchApp.Base.AddDays(2):yyyy-MM-dd}T10:00:00Z&dropOffAt={SearchApp.Base.AddDays(4):yyyy-MM-dd}T10:00:00Z";
        JsonElement body = await Eventually.MatchesAsync(
            async () =>
            {
                await app.RefreshAsync();
                return (await app.GetAsync($"/cars/search?{trip}")).Body;
            },
            static b => b.TryGetProperty("value", out JsonElement value) && value.GetArrayLength() == 1,
            Patience);

        body.GetProperty("value")[0].GetProperty("offers")[0].GetProperty("total").GetProperty("amount").GetString().ShouldBe("70.00");
    }

    private static bool HasVersion(JsonElement document, long version) =>
        document.GetProperty("rooms").EnumerateArray()
            .Any(room => room.GetProperty("state").GetProperty("version").EnumerateArray().Any(v => v.GetInt64() == version));

    private async Task<JsonElement> WaitForAsync(Guid propertyId, Func<JsonElement, bool> condition) =>
        (await Eventually.MatchesAsync(
            async () => await app.StayDocumentAsync(propertyId),
            document => document is { } d && condition(d),
            Patience))!.Value;
}
