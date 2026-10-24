using Confluent.Kafka;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Viatica.Testing;

namespace Viatica.Kernel.Messaging.IntegrationTests;

public sealed class RevokedPartitionTests(PostgresDatabase database, KafkaFixture kafka) : IClassFixture<PostgresDatabase>, IAsyncLifetime
{
    public async ValueTask InitializeAsync() => await TestService.EnsureSchemaAsync(database);

    public ValueTask DisposeAsync() => ValueTask.CompletedTask;

    /// <summary>
    /// The partition is taken away while a handler runs, so when the handler returns the offset cannot be stored
    /// (<see cref="ErrorCode.Local_State"/>). The loop must shrug that off and keep consuming once the partition is
    /// handed out again.
    /// </summary>
    [Fact]
    public async Task A_consumer_whose_partition_is_revoked_during_a_handler_keeps_consuming()
    {
        string topic = await kafka.CreateTopicAsync("revoked", partitions: 1);
        string group = $"revoked-{Guid.NewGuid():N}";
        string slow = Guid.NewGuid().ToString();
        string next = Guid.NewGuid().ToString();
        var gate = new HandlerGate();
        gate.HoldKey(slow);
        using var outcomes = new OutcomeRecorder(group);

        using IHost publisher = await TestService.StartAsync(database.ConnectionString, kafka, topic);
        using IHost consumer = TestService.Build(database.ConnectionString, kafka, topic, relay: false, consumerGroup: group, gate: gate);
        var client = new TaskCompletionSource<IConsumer<string, byte[]>>(TaskCreationOptions.RunContinuationsAsynchronously);
        consumer.Services.GetServices<IHostedService>().OfType<SubscriptionConsumer<TestDb>>().Single()
            .ConsumerCreated = created => client.TrySetResult(created);
        await consumer.StartAsync();

        await TestService.PublishAsync(publisher, new ThingHappened(slow, 1));
        await gate.Entered.WaitAsync(TimeSpan.FromSeconds(60), TestContext.Current.CancellationToken);

        // The handler is blocked inside the message. Take the partition away, as a rebalance would, then let the
        // handler finish: its StoreOffset now finds a partition this consumer no longer owns.
        IConsumer<string, byte[]> kafkaClient = await client.Task;
        kafkaClient.Unassign();
        gate.Release();
        await Eventually.MatchesAsync(() => Task.FromResult(outcomes.Count("applied")), static n => n == 1, TimeSpan.FromSeconds(30));

        // The partition is handed out again. The message that lost its offset comes back and the inbox absorbs it;
        // what matters is that the loop is alive, so a message published now is applied.
        kafkaClient.Assign(new TopicPartition(topic, 0));
        await Eventually.MatchesAsync(() => Task.FromResult(outcomes.Count("duplicate")), static n => n == 1, TimeSpan.FromSeconds(60));
        await TestService.PublishAsync(publisher, new ThingHappened(next, 1));
        await Eventually.MatchesAsync(() => TestService.AppliedAsync(consumer, next), static rows => rows.Count == 1, TimeSpan.FromSeconds(60));
        (await TestService.AppliedAsync(consumer, slow)).Count.ShouldBe(1);
    }
}
