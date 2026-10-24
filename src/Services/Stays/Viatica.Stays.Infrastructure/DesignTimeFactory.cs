using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Design;

namespace Viatica.Stays.Infrastructure;

/// <summary>
/// Lets <c>dotnet ef migrations add</c> build the context without starting the host, and so without a Kafka, a
/// Keycloak or any of the keys the host insists on. Nothing connects: scaffolding a migration reads the model only.
/// </summary>
internal sealed class DesignTimeFactory : IDesignTimeDbContextFactory<StaysDbContext>
{
    public StaysDbContext CreateDbContext(string[] args) =>
        new(new DbContextOptionsBuilder<StaysDbContext>()
            .UseNpgsql(
                "Host=localhost;Database=stays;Username=stays;Password=stays",
                npgsql => npgsql.MigrationsHistoryTable("__ef_migrations_history", StaysDbContext.Schema))
            .Options);
}
