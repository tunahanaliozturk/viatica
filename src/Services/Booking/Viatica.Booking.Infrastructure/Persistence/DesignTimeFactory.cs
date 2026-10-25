using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Design;

namespace Viatica.Booking.Infrastructure.Persistence;

/// <summary>
/// Lets <c>dotnet ef migrations add</c> build the context without starting the host. The connection string is never
/// used to connect when scaffolding; it only selects the provider.
/// </summary>
internal sealed class DesignTimeFactory : IDesignTimeDbContextFactory<BookingDbContext>
{
    public BookingDbContext CreateDbContext(string[] args) =>
        new(new DbContextOptionsBuilder<BookingDbContext>()
            .UseNpgsql(Environment.GetEnvironmentVariable("BOOKING_DB") ?? "Host=localhost;Port=5450;Database=booking;Username=booking;Password=booking")
            .Options);
}
