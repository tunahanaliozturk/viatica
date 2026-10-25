using Npgsql;
using Viatica.Testing;

namespace Viatica.Booking.IntegrationTests;

/// <summary>
/// One Booking, with its four stand-ins, per test class. A test takes a trip of its own, so tests in a class do not
/// disturb each other; a world per test would open a pool of connections for each of the five databases every time and
/// run the container out of them.
/// </summary>
public sealed class WorldFixture(PostgresFixture postgres, KafkaFixture kafka) : IAsyncLifetime
{
    internal BookingWorld World { get; private set; } = null!;

    public async ValueTask InitializeAsync() => World = await BookingWorld.StartAsync(postgres, kafka);

    public async ValueTask DisposeAsync()
    {
        await World.DisposeAsync();
        NpgsqlConnection.ClearAllPools();
    }
}
