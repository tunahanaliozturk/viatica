using Viatica.Testing;

namespace Viatica.Stays.IntegrationTests;

/// <summary>
/// One Stays host, one Booking stub and the Kafka to reach them, for the tests of one class. The sweeper ticks every
/// 200 ms, which the tests that move the clock rely on.
/// </summary>
public class StaysFixture : IAsyncLifetime
{
    private readonly PostgresFixture _postgres;
    private readonly KafkaFixture _kafka;
    private readonly ValkeyFixture _valkey;

    public StaysFixture(PostgresFixture postgres, KafkaFixture kafka, ValkeyFixture valkey)
    {
        _postgres = postgres;
        _kafka = kafka;
        _valkey = valkey;
    }

    internal StaysHost Host { get; private set; } = null!;

    internal BookingStub Booking { get; private set; } = null!;

    /// <summary>How long the sweeper sleeps between ticks.</summary>
    protected virtual TimeSpan SweepInterval => TimeSpan.FromMilliseconds(200);

    /// <summary>Connections one test host may open; the race tests raise it.</summary>
    protected virtual int MaxPool => 20;

    public async ValueTask InitializeAsync()
    {
        Host = await StaysHost.StartAsync(_postgres, _kafka, _valkey, SweepInterval, MaxPool);
        Booking = await BookingStub.StartAsync(_postgres, _kafka, Host.BookingTopic);
    }

    public async ValueTask DisposeAsync()
    {
        await Booking.DisposeAsync();
        Host.Dispose();
        GC.SuppressFinalize(this);
    }

    /// <summary>Reads <c>stays.v1</c> from its first message, so a test never races its own setup.</summary>
    internal EventProbe Probe() => _kafka.Probe(Host.StaysTopic);
}

/// <summary>A host whose sweeper never ticks in a test's lifetime, so the test decides when holds expire.</summary>
public sealed class ManualSweepFixture(PostgresFixture postgres, KafkaFixture kafka, ValkeyFixture valkey)
    : StaysFixture(postgres, kafka, valkey)
{
    protected override TimeSpan SweepInterval => TimeSpan.FromHours(1);
}

/// <summary>A host with room for the connections 200 parallel holds ask for.</summary>
public sealed class RaceFixture(PostgresFixture postgres, KafkaFixture kafka, ValkeyFixture valkey)
    : StaysFixture(postgres, kafka, valkey)
{
    protected override int MaxPool => 60;

    protected override TimeSpan SweepInterval => TimeSpan.FromHours(1);
}
