using Testcontainers.Redis;
using Viatica.Testing;

[assembly: AssemblyFixture(typeof(PostgresFixture))]
[assembly: AssemblyFixture(typeof(KafkaFixture))]
[assembly: AssemblyFixture(typeof(Viatica.Stays.IntegrationTests.ValkeyFixture))]

// One test class at a time. Every class starts a whole service with its own pool of connections to one Postgres
// container, whose default limit is 100; run together the classes exhaust it ("too many clients"), and the failure is
// the container's, not the service's.
[assembly: CollectionBehavior(CollectionBehavior.CollectionPerAssembly)]

namespace Viatica.Stays.IntegrationTests;

/// <summary>
/// The Valkey compose runs, same image and version, for the repeatable-request store. It lives here rather than in
/// Viatica.Testing because only this suite and the kernel's own need it, and the kernel's keeps its copy.
/// </summary>
public sealed class ValkeyFixture : IAsyncLifetime
{
    private readonly RedisContainer _container = new RedisBuilder("valkey/valkey:8.1.10-alpine").Build();

    public string ConnectionString => _container.GetConnectionString();

    public async ValueTask InitializeAsync() => await _container.StartAsync();

    public async ValueTask DisposeAsync() => await _container.DisposeAsync();
}
