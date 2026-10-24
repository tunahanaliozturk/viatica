using StackExchange.Redis;
using Testcontainers.Redis;
using Xunit;

namespace Viatica.Testing;

/// <summary>
/// One Valkey server for a whole test assembly, the image and version compose runs, so a claim about the cache is
/// tested against the server it will meet. Keys are unique per test (search ids are GUIDs), so tests share the
/// server without a flush between them.
/// </summary>
/// <remarks>Register with <c>[assembly: AssemblyFixture(typeof(ValkeyFixture))]</c>.</remarks>
public sealed class ValkeyFixture : IAsyncLifetime
{
    // The tag compose uses. Testcontainers' Redis module speaks the same protocol and health check Valkey does.
    private readonly RedisContainer _container = new RedisBuilder("valkey/valkey:8.1.10-alpine").Build();

    public string ConnectionString => _container.GetConnectionString();

    public async ValueTask InitializeAsync() => await _container.StartAsync();

    public async ValueTask DisposeAsync() => await _container.DisposeAsync();

    /// <summary>A new connection, for a test that wants to look at the keys the service left behind.</summary>
    public Task<ConnectionMultiplexer> ConnectAsync() => ConnectionMultiplexer.ConnectAsync(ConnectionString);
}
