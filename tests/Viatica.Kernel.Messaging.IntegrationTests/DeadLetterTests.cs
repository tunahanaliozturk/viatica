using System.Text;
using Confluent.Kafka;
using Microsoft.Extensions.Hosting;
using Viatica.Testing;

namespace Viatica.Kernel.Messaging.IntegrationTests;

public sealed class DeadLetterTests(PostgresDatabase database, KafkaFixture kafka) : IClassFixture<PostgresDatabase>, IAsyncLifetime
{
    public async ValueTask InitializeAsync() => await TestService.EnsureSchemaAsync(database);

    public ValueTask DisposeAsync() => ValueTask.CompletedTask;

    [Fact]
    public async Task A_poison_message_goes_to_the_dlq_after_the_configured_attempts_and_its_partition_moves_on()
    {
        string topic = await kafka.CreateTopicAsync("poison", partitions: 1);
        string key = Guid.NewGuid().ToString();
        var attempts = new Attempts();
        using IHost host = await TestService.StartAsync(database.ConnectionString, kafka, topic, consumerGroup: "poison", attempts: attempts);
        using EventProbe topicProbe = kafka.Probe(topic);
        using EventProbe deadLetters = kafka.Probe(topic + ".dlq");

        // Same key, so the event behind the poison sits on the same partition and has to wait for it.
        await TestService.PublishAsync(host, new PoisonPill(key), new ThingHappened(key, 2));

        ProbedMessage original = await topicProbe.WaitForAsync(m => m.Key == key && m.Type == "PoisonPill");
        ProbedMessage parked = await deadLetters.WaitForAsync(m => m.Key == key);
        parked.Id.ShouldBe(original.Id);
        parked.Headers["dlq-attempts"].ShouldBe("3");
        parked.Headers["dlq-error-type"].ShouldBe(typeof(InvalidOperationException).FullName);
        parked.Headers["dlq-topic"].ShouldBe(topic);
        parked.Headers["dlq-offset"].ShouldBe(original.Offset.ToString(System.Globalization.CultureInfo.InvariantCulture));
        parked.Headers["dlq-consumer-group"].ShouldBe("poison");
        attempts.Count[key].ShouldBe(3);

        await Eventually.MatchesAsync(() => TestService.AppliedAsync(host, key), static rows => rows.Count == 1);
    }

    [Fact]
    public async Task A_null_valued_message_goes_to_the_dlq_and_its_partition_moves_on()
    {
        string topic = await kafka.CreateTopicAsync("tombstone", partitions: 1);
        string key = Guid.NewGuid().ToString();
        using IHost host = await TestService.StartAsync(database.ConnectionString, kafka, topic, consumerGroup: "tombstone");
        using EventProbe deadLetters = kafka.Probe(topic + ".dlq");

        using (IProducer<string, byte[]> producer = new ProducerBuilder<string, byte[]>(new ProducerConfig { BootstrapServers = kafka.BootstrapServers }).Build())
        {
            await producer.ProduceAsync(topic, new Message<string, byte[]> { Key = key, Value = null! });
        }

        await TestService.PublishAsync(host, new ThingHappened(key, 1));

        ProbedMessage parked = await deadLetters.WaitForAsync(m => m.Key == key);
        parked.Value.ShouldBeNull();
        parked.Headers["dlq-attempts"].ShouldBe("0");
        parked.Headers["dlq-error-type"].ShouldStartWith("System.Text.Json.");
        await Eventually.MatchesAsync(() => TestService.AppliedAsync(host, key), static rows => rows.Count == 1);
    }

    [Fact]
    public async Task A_message_that_cannot_be_read_goes_to_the_dlq_without_retries()
    {
        string topic = await kafka.CreateTopicAsync("garbage", partitions: 1);
        string key = Guid.NewGuid().ToString();
        using IHost host = await TestService.StartAsync(database.ConnectionString, kafka, topic, consumerGroup: "garbage");
        using EventProbe deadLetters = kafka.Probe(topic + ".dlq");

        byte[] garbage = Encoding.UTF8.GetBytes("{not json");
        using (IProducer<string, byte[]> producer = new ProducerBuilder<string, byte[]>(new ProducerConfig { BootstrapServers = kafka.BootstrapServers }).Build())
        {
            await producer.ProduceAsync(topic, new Message<string, byte[]> { Key = key, Value = garbage });
        }

        await TestService.PublishAsync(host, new ThingHappened(key, 1));

        ProbedMessage parked = await deadLetters.WaitForAsync(m => m.Key == key);
        parked.Value.ShouldBe(garbage);
        parked.Headers["dlq-attempts"].ShouldBe("0");
        parked.Headers["dlq-error-type"].ShouldStartWith("System.Text.Json.");
        await Eventually.MatchesAsync(() => TestService.AppliedAsync(host, key), static rows => rows.Count == 1);
    }
}
