using System.Collections.Concurrent;
using System.Diagnostics.Metrics;
using Confluent.Kafka;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Viatica.Testing;

namespace Viatica.Kernel.Messaging.IntegrationTests;

public sealed class ConsumerCrashTests(PostgresDatabase database, KafkaFixture kafka) : IClassFixture<PostgresDatabase>, IAsyncLifetime
{
    public async ValueTask InitializeAsync() => await TestService.EnsureSchemaAsync(database);

    public ValueTask DisposeAsync() => ValueTask.CompletedTask;

    /// <summary>
    /// The first consumer applies the event and commits it with its inbox row, then dies before its offset is
    /// committed: its Kafka client is dropped without Close, as a killed process drops it. The consumer that takes
    /// the partition over is handed the same message again.
    /// </summary>
    [Fact]
    public async Task A_consumer_killed_between_handling_and_offset_commit_does_not_apply_the_event_twice()
    {
        string topic = await kafka.CreateTopicAsync("crash", partitions: 1);
        string group = $"crash-{Guid.NewGuid():N}";
        string key = Guid.NewGuid().ToString();
        using var outcomes = new OutcomeRecorder(group);

        using IHost publisher = await TestService.StartAsync(database.ConnectionString, kafka, topic);
        await TestService.PublishAsync(publisher, new ThingHappened(key, 1));

        // The doomed consumer: same group and the real message path, but nothing ever stores or commits its offset.
        using (IHost doomed = TestService.Build(database.ConnectionString, kafka, topic, relay: false, consumerGroup: group))
        {
            SubscriptionConsumer<TestDb> subscription = doomed.Services.GetServices<IHostedService>().OfType<SubscriptionConsumer<TestDb>>().Single();
            IConsumer<string, byte[]> client = new ConsumerBuilder<string, byte[]>(new ConsumerConfig
            {
                BootstrapServers = kafka.BootstrapServers,
                GroupId = group,
                AutoOffsetReset = AutoOffsetReset.Earliest,
                EnableAutoCommit = false,
                // The broker's minimum, so the group notices the dead member in seconds rather than 45.
                SessionTimeoutMs = 6000,
            }).Build();
            client.Subscribe(topic);

            ConsumeResult<string, byte[]> result = client.Consume(TimeSpan.FromSeconds(30)).ShouldNotBeNull();
            await subscription.ProcessAsync(result, TestContext.Current.CancellationToken);
            client.Dispose();
        }

        (await TestService.AppliedAsync(publisher, key)).Count.ShouldBe(1);

        using IHost successor = await TestService.StartAsync(database.ConnectionString, kafka, topic, relay: false, consumerGroup: group);
        await Eventually.MatchesAsync(() => Task.FromResult(outcomes.Count("duplicate")), static n => n == 1, TimeSpan.FromSeconds(60));

        // And it moved past the redelivered message instead of stalling on it.
        string next = Guid.NewGuid().ToString();
        await TestService.PublishAsync(publisher, new ThingHappened(next, 1));
        await Eventually.MatchesAsync(() => TestService.AppliedAsync(successor, next), static rows => rows.Count == 1);

        (await TestService.AppliedAsync(successor, key)).Count.ShouldBe(1);
        (await TestService.QueryAsync(successor, db => db.Set<InboxMessage>().CountAsync(m => m.ConsumerGroup == group))).ShouldBe(2);
    }
}

/// <summary>Counts the consumer's outcomes for one consumer group from the messaging meter.</summary>
internal sealed class OutcomeRecorder : IDisposable
{
    private readonly MeterListener _listener = new();
    private readonly ConcurrentDictionary<string, int> _counts = new();

    public OutcomeRecorder(string consumerGroup)
    {
        _listener.InstrumentPublished = static (instrument, listener) =>
        {
            if (instrument is { Meter.Name: MessagingTelemetry.Name, Name: "viatica.messaging.consumed" })
            {
                listener.EnableMeasurementEvents(instrument);
            }
        };
        _listener.SetMeasurementEventCallback<long>((_, value, tags, _) =>
        {
            string? group = null;
            string? outcome = null;
            foreach (KeyValuePair<string, object?> tag in tags)
            {
                group = tag.Key == "messaging.consumer.group.name" ? (string?)tag.Value : group;
                outcome = tag.Key == "outcome" ? (string?)tag.Value : outcome;
            }

            if (group == consumerGroup && outcome is not null)
            {
                _counts.AddOrUpdate(outcome, (int)value, (_, n) => n + (int)value);
            }
        });
        _listener.Start();
    }

    public int Count(string outcome) => _counts.GetValueOrDefault(outcome);

    public void Dispose() => _listener.Dispose();
}
