using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Design;

namespace Viatica.Payments.Infrastructure.Persistence;

/// <summary>
/// Lets <c>dotnet ef migrations add</c> build the context without starting the host. The connection string is never
/// used to connect when scaffolding; it only selects the provider.
/// </summary>
internal sealed class DesignTimeFactory : IDesignTimeDbContextFactory<PaymentsDbContext>
{
    public PaymentsDbContext CreateDbContext(string[] args) =>
        new(new DbContextOptionsBuilder<PaymentsDbContext>()
            .UseNpgsql(Environment.GetEnvironmentVariable("PAYMENTS_DB") ?? "Host=localhost;Port=5450;Database=payments;Username=payments;Password=payments")
            .Options);
}
