using Viatica.Testing;

namespace Viatica.Flights.IntegrationTests;

// One Flights host per distinct configuration. Test classes that can share a host do, through a collection, because
// a host means a database, two Kafka topics and a consumer group join; the classes in a collection run one after the
// other and each starts by putting the simulator back to quiet.

/// <summary>The service as configured by default: 2.5 s search deadline, eight second airline timeout, ten call breaker.</summary>
public sealed class DefaultFlights(PostgresFixture postgres, KafkaFixture kafka, ValkeyFixture valkey)
    : FlightsFixture(postgres, kafka, valkey);

[CollectionDefinition(Name)]
public sealed class DefaultFlightsGroup : ICollectionFixture<DefaultFlights>
{
    public const string Name = "default flights";
}

/// <summary>A breaker that opens after three calls and stays open for the whole test.</summary>
public sealed class BreakerFlights(PostgresFixture postgres, KafkaFixture kafka, ValkeyFixture valkey)
    : FlightsFixture(postgres, kafka, valkey)
{
    internal override HostSettings Settings => new() { BreakerMinimumThroughput = 3, BreakDuration = "00:05:00" };
}

/// <summary>A one second airline timeout, so a hanging order creation is cut off quickly.</summary>
public sealed class ImpatientFlights(PostgresFixture postgres, KafkaFixture kafka, ValkeyFixture valkey)
    : FlightsFixture(postgres, kafka, valkey)
{
    internal override HostSettings Settings => new() { RequestTimeout = "00:00:01" };
}
