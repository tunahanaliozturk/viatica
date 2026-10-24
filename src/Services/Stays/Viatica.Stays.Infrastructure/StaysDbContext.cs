using Microsoft.EntityFrameworkCore;
using Viatica.Contracts;
using Viatica.Kernel.Messaging;
using Viatica.Stays.Application;
using Viatica.Stays.Domain;

namespace Viatica.Stays.Infrastructure;

/// <summary>
/// The Stays database: catalogue, nights, holds, and the outbox and inbox that carry its events. Used directly by
/// the use cases through <see cref="IStaysDb"/>; there is no repository in between.
/// </summary>
public sealed class StaysDbContext(DbContextOptions<StaysDbContext> options) : DbContext(options), IStaysDb
{
    public const string Schema = "stays";

    public DbSet<StayProperty> Properties => Set<StayProperty>();

    public DbSet<RoomType> RoomTypes => Set<RoomType>();

    public DbSet<RoomTypeInventory> Inventories => Set<RoomTypeInventory>();

    public DbSet<RoomNight> RoomNights => Set<RoomNight>();

    public DbSet<Hold> Holds => Set<Hold>();

    public void Publish(IIntegrationEvent message) => this.AddIntegrationEvent(message);

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        ArgumentNullException.ThrowIfNull(modelBuilder);

        modelBuilder.HasDefaultSchema(Schema);
        modelBuilder.AddMessaging();
        StaysModel.Configure(modelBuilder);
    }
}
