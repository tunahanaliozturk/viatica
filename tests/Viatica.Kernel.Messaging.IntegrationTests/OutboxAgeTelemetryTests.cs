using System.Diagnostics.Metrics;
using Microsoft.Extensions.Hosting;
using Viatica.Testing;

namespace Viatica.Kernel.Messaging.IntegrationTests;

public sealed class OutboxAgeTelemetryTests(PostgresDatabase database, KafkaFixture kafka) : IClassFixture<PostgresDatabase>, IAsyncLifetime
{
    public async ValueTask InitializeAsync() => await TestService.EnsureSchemaAsync(database);

    public ValueTask DisposeAsync() => ValueTask.CompletedTask;

    /// <summary>
    /// Two relays of the same DbContext type, as a host that sends to two topics from one outbox would run. Each
    /// reports its own age, and stopping one must not remove the other's gauge.
    /// </summary>
    [Fact]
    public async Task Stopping_one_relay_leaves_the_age_of_the_other_relay_on_the_gauge()
    {
        string topicA = await kafka.CreateTopicAsync("age-a", partitions: 1);
        string topicB = await kafka.CreateTopicAsync("age-b", partitions: 1);
        using IHost first = await TestService.StartAsync(database.ConnectionString, kafka, topicA);
        using IHost second = await TestService.StartAsync(database.ConnectionString, kafka, topicB);

        // Each relay reports before its first round, so both are on the gauge as soon as they have started.
        await Eventually.MatchesAsync(() => Task.FromResult(ReadTopics(topicA, topicB)), static topics => topics.Count == 2, TimeSpan.FromSeconds(5));

        await first.StopAsync();

        ReadTopics(topicA, topicB).ShouldBe([topicB]);
    }

    private static List<string> ReadTopics(params string[] among)
    {
        var topics = new List<string>();
        using var listener = new MeterListener
        {
            InstrumentPublished = static (instrument, l) =>
            {
                if (instrument.Meter.Name == MessagingTelemetry.Name && instrument.Name == "viatica.outbox.age")
                {
                    l.EnableMeasurementEvents(instrument);
                }
            },
        };
        listener.SetMeasurementEventCallback<double>((_, _, tags, _) =>
        {
            foreach (KeyValuePair<string, object?> tag in tags)
            {
                if (tag.Key == "messaging.destination.name" && tag.Value is string topic && among.Contains(topic))
                {
                    topics.Add(topic);
                }
            }
        });
        listener.Start();
        listener.RecordObservableInstruments();
        return topics;
    }
}
