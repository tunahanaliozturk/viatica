using Viatica.Testing;

namespace Viatica.Payments.IntegrationTests;

/// <summary>Everything a test class needs, built once: the simulator, the Payments host, a Booking stand-in, and a probe on the topic Payments writes.</summary>
public abstract class PaymentsFixture(PostgresFixture postgres, KafkaFixture kafka) : IAsyncLifetime
{
    internal SimHost Sim { get; private set; } = null!;

    internal PaymentsHost Host { get; private set; } = null!;

    internal EventProbe Probe { get; private set; } = null!;

    internal BookingStub Booking { get; private set; } = null!;

    internal RawBookingSender RawBooking { get; private set; } = null!;

    internal HttpClient Client { get; private set; } = null!;

    internal virtual HostSettings Settings => new();

    public async ValueTask InitializeAsync()
    {
        Sim = new SimHost();
        await Sim.QuietAsync();
        Host = await PaymentsHost.StartAsync(postgres, kafka, Sim, Settings);
        Probe = kafka.Probe(Host.PaymentsTopic);
        Booking = await BookingStub.StartAsync(postgres, kafka, Host.BookingTopic);
        RawBooking = new RawBookingSender(kafka.BootstrapServers, Host.BookingTopic);
        Client = Host.CreateClient();
    }

    public async ValueTask DisposeAsync()
    {
        Client?.Dispose();
        RawBooking?.Dispose();
        if (Booking is not null)
        {
            await Booking.DisposeAsync();
        }

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

/// <summary>The service as configured by default: a one second provider timeout, a breaker that never opens.</summary>
public sealed class DefaultPayments(PostgresFixture postgres, KafkaFixture kafka) : PaymentsFixture(postgres, kafka);

/// <summary>A breaker that opens after three calls and stays open for six seconds, and a consumer patient enough to outlast it.</summary>
public sealed class BreakerPayments(PostgresFixture postgres, KafkaFixture kafka) : PaymentsFixture(postgres, kafka)
{
    internal override HostSettings Settings => new()
    {
        BreakerMinimumThroughput = 3,
        BreakDuration = "00:00:06",
        ConsumerAttempts = 14,
    };
}
