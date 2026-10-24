using System.Diagnostics;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Hosting;
using Viatica.Testing;

namespace Viatica.Kernel.Messaging.IntegrationTests;

/// <summary>Outbox to Kafka to consumer to inbox, against real Postgres 18 and Kafka.</summary>
public sealed class OutboxToInboxTests(PostgresDatabase database, KafkaFixture kafka) : IClassFixture<PostgresDatabase>, IAsyncLifetime
{
    public async ValueTask InitializeAsync() => await TestService.EnsureSchemaAsync(database);

    public ValueTask DisposeAsync() => ValueTask.CompletedTask;

    [Fact]
    public async Task An_event_saved_with_the_state_change_reaches_kafka_in_the_envelope_and_is_applied_once()
    {
        string topic = await kafka.CreateTopicAsync("e2e");
        string key = Guid.NewGuid().ToString();
        using IHost host = await TestService.StartAsync(database.ConnectionString, kafka, topic, consumerGroup: "e2e");
        using EventProbe probe = kafka.Probe(topic);

        await TestService.PublishAsync(host, new ThingHappened(key, 1));

        ProbedMessage message = await probe.WaitForAsync(m => m.Key == key);
        message.Type.ShouldBe("ThingHappened");
        message.Version.ShouldBe(1);
        message.Id.Version.ShouldBe(7);
        message.OccurredAt.Offset.ShouldBe(TimeSpan.Zero);
        message.DataString("key").ShouldBe(key);
        message.DataString("sequence").ShouldBe("1");
        message.Data.TryGetProperty("partitionKey", out _).ShouldBeFalse("the key travels as the Kafka key, not in the data");

        await Eventually.MatchesAsync(() => TestService.AppliedAsync(host, key), static rows => rows.Count == 1);
        (await TestService.QueryAsync(host, db => db.Set<InboxMessage>().CountAsync(m => m.EventId == message.Id))).ShouldBe(1);
        await Eventually.MatchesAsync(() => TestService.QueryAsync(host, static db => db.Set<OutboxMessage>().CountAsync()), static n => n == 0);
    }

    [Fact]
    public async Task Trace_context_of_the_code_that_raised_the_event_reaches_the_handler()
    {
        using var source = new ActivitySource("Viatica.Messaging.IntegrationTests");
        using var listener = new ActivityListener
        {
            ShouldListenTo = static s => s.Name is MessagingTelemetry.Name or "Viatica.Messaging.IntegrationTests",
            Sample = static (ref ActivityCreationOptions<ActivityContext> _) => ActivitySamplingResult.AllDataAndRecorded,
        };
        ActivitySource.AddActivityListener(listener);

        string topic = await kafka.CreateTopicAsync("trace");
        string key = Guid.NewGuid().ToString();
        using IHost host = await TestService.StartAsync(database.ConnectionString, kafka, topic, consumerGroup: "trace");
        using EventProbe probe = kafka.Probe(topic);

        ActivityTraceId traceId;
        using (Activity request = source.StartActivity("POST /checkout")!)
        {
            traceId = request.TraceId;
            await TestService.PublishAsync(host, new ThingHappened(key, 1));
        }

        ProbedMessage message = await probe.WaitForAsync(m => m.Key == key);
        message.TraceParent.ShouldNotBeNull().ShouldContain(traceId.ToString());

        List<AppliedEvent> applied = await Eventually.MatchesAsync(() => TestService.AppliedAsync(host, key), static rows => rows.Count == 1);
        applied[0].TraceId.ShouldBe(traceId.ToString());
    }

    /// <summary>
    /// Two relay instances share one outbox, as two replicas of a service would, while the events are written.
    /// If both could take rows of one key, a later event could reach Kafka first.
    /// </summary>
    [Fact]
    public async Task Per_key_order_holds_through_two_relays_and_a_consumer()
    {
        const int keys = 40;
        const int perKey = 25;
        string topic = await kafka.CreateTopicAsync("order", partitions: 6);
        string run = Guid.NewGuid().ToString("N")[..8];
        using IHost first = await TestService.StartAsync(database.ConnectionString, kafka, topic, consumerGroup: "order");
        using IHost second = await TestService.StartAsync(database.ConnectionString, kafka, topic);
        using EventProbe probe = kafka.Probe(topic);

        // One SaveChanges per sequence number across all keys, so the relays see the outbox fill up while they
        // drain it, and every key has rows in many rounds.
        for (int sequence = 1; sequence <= perKey; sequence++)
        {
            await TestService.PublishAsync(
                sequence % 2 == 0 ? first : second,
                [.. Enumerable.Range(0, keys).Select(k => new ThingHappened($"{run}-{k}", sequence))]);
        }

        IReadOnlyList<ProbedMessage> messages = await probe.WaitForCountAsync(keys * perKey, m => m.Key.StartsWith(run, StringComparison.Ordinal));
        foreach (IGrouping<string, ProbedMessage> key in messages.GroupBy(static m => m.Key))
        {
            key.Select(static m => int.Parse(m.DataString("sequence")!, System.Globalization.CultureInfo.InvariantCulture))
                .ShouldBe(Enumerable.Range(1, perKey), $"on the topic, key {key.Key}");
        }

        for (int k = 0; k < keys; k++)
        {
            List<AppliedEvent> applied = await Eventually.MatchesAsync(
                () => TestService.AppliedAsync(first, $"{run}-{k}"),
                static rows => rows.Count >= perKey);
            applied.Select(static a => a.Sequence).ShouldBe(Enumerable.Range(1, perKey), $"as applied, key {run}-{k}");
        }
    }
}
