using Npgsql;
using Testcontainers.PostgreSql;
using Xunit;

namespace Viatica.Testing;

/// <summary>
/// One Postgres 18 server for a whole test assembly. Test classes get their own database on it through
/// <see cref="PostgresDatabase"/>, so they never see each other's rows while the container is paid for once.
/// </summary>
/// <remarks>Register with <c>[assembly: AssemblyFixture(typeof(PostgresFixture))]</c>.</remarks>
public sealed class PostgresFixture : IAsyncLifetime
{
    // Pinned by digest so a run today and a run next month cannot differ only in which build a moving tag named.
    // The data directory is a tmpfs: nothing here has to survive the container, and fsync on a real disk would
    // make every test pay for durability it does not test.
    private readonly PostgreSqlContainer _container =
        new PostgreSqlBuilder("postgres:18@sha256:86c951e05bf56c93d95d397747fb8820ac76cc3bedb78f43abd83eedbe3666ae")
            .WithTmpfsMount("/var/lib/postgresql")
            .Build();

    public async ValueTask InitializeAsync() => await _container.StartAsync();

    public async ValueTask DisposeAsync() => await _container.DisposeAsync();

    /// <summary>Creates an empty database and returns a connection string to it.</summary>
    public async Task<string> CreateDatabaseAsync(string prefix = "test")
    {
        string name = $"{prefix}_{Guid.NewGuid():N}".ToLowerInvariant();

        await using (var connection = new NpgsqlConnection(_container.GetConnectionString()))
        {
            await connection.OpenAsync();
            await using NpgsqlCommand command = connection.CreateCommand();
            // The name is generated above from a prefix the test chose and a GUID, never from outside input.
#pragma warning disable CA2100
            command.CommandText = $"create database \"{name}\"";
#pragma warning restore CA2100
            await command.ExecuteNonQueryAsync();
        }

        return new NpgsqlConnectionStringBuilder(_container.GetConnectionString()) { Database = name }.ConnectionString;
    }
}

/// <summary>A fresh, empty database for one test class, on the assembly's <see cref="PostgresFixture"/>.</summary>
/// <remarks>Take it with <c>IClassFixture&lt;PostgresDatabase&gt;</c>.</remarks>
public sealed class PostgresDatabase(PostgresFixture server) : IAsyncLifetime
{
    public string ConnectionString { get; private set; } = "";

    public async ValueTask InitializeAsync() => ConnectionString = await server.CreateDatabaseAsync();

    // The database goes with the container at the end of the run; dropping it per class would only add time.
    public ValueTask DisposeAsync() => ValueTask.CompletedTask;
}
