using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Design;

namespace Viatica.Flights.Infrastructure.Persistence;

/// <summary>
/// Lets <c>dotnet ef migrations add</c> build the context without starting the host. The connection string is never
/// used to connect when scaffolding; it only selects the provider.
/// </summary>
internal sealed class DesignTimeFactory : IDesignTimeDbContextFactory<FlightsDbContext>
{
    public FlightsDbContext CreateDbContext(string[] args) =>
        new(new DbContextOptionsBuilder<FlightsDbContext>()
            .UseNpgsql(Environment.GetEnvironmentVariable("FLIGHTS_DB") ?? "Host=localhost;Port=5450;Database=flights;Username=flights;Password=flights")
            .Options);
}
