using Confluent.Kafka;
using Confluent.Kafka.Admin;
using Testcontainers.Kafka;
using Xunit;

namespace Viatica.Testing;

/// <summary>
/// One Kafka broker (KRaft, single node) for a whole test assembly. Tests isolate themselves by topic name, not by
/// broker: <see cref="CreateTopicAsync"/> hands out a fresh topic every call.
/// </summary>
/// <remarks>Register with <c>[assembly: AssemblyFixture(typeof(KafkaFixture))]</c>.</remarks>
public sealed class KafkaFixture : IAsyncLifetime
{
    // 3.9.2, not 4.x: Testcontainers writes a listener list with a trailing comma, which Kafka 4 refuses to parse.
    private readonly KafkaContainer _container =
        new KafkaBuilder("apache/kafka:3.9.2@sha256:05b4616e0702ef2729327705d54ad6b50ea70b271c4b730fabd2320789fb7b02")
            .Build();

    public string BootstrapServers => _container.GetBootstrapAddress();

    public async ValueTask InitializeAsync() => await _container.StartAsync();

    public async ValueTask DisposeAsync() => await _container.DisposeAsync();

    /// <summary>
    /// Creates a topic with a unique name and returns the name. Created explicitly rather than left to broker
    /// auto-creation, so the partition count is the one the test asked for.
    /// </summary>
    public async Task<string> CreateTopicAsync(string prefix = "test", int partitions = 3)
    {
        string name = $"{prefix}-{Guid.NewGuid():N}";

        using IAdminClient admin = new AdminClientBuilder(new AdminClientConfig { BootstrapServers = BootstrapServers }).Build();
        await admin.CreateTopicsAsync(
        [
            new TopicSpecification { Name = name, NumPartitions = partitions, ReplicationFactor = 1 },
        ]);

        return name;
    }

    /// <summary>Starts an <see cref="EventProbe"/> that reads <paramref name="topic"/> from its first message.</summary>
    public EventProbe Probe(string topic) => new(BootstrapServers, topic);
}
