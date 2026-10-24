using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Viatica.Contracts;
using Viatica.Stays.Domain;

namespace Viatica.Stays.Application;

/// <summary>
/// What the use cases see of the database: the sets they read and write, a transaction, and the outbox. The
/// DbContext in Infrastructure implements it, so Application references EF Core but never a provider.
/// </summary>
public interface IStaysDb
{
    DbSet<StayProperty> Properties { get; }

    DbSet<RoomType> RoomTypes { get; }

    DbSet<RoomTypeInventory> Inventories { get; }

    DbSet<RoomNight> RoomNights { get; }

    DbSet<Hold> Holds { get; }

    /// <summary>For explicit transactions and savepoints; the hold statements are all-or-nothing inside one.</summary>
    DatabaseFacade Database { get; }

    /// <summary>
    /// Queues an integration event in the outbox. The row is written by the next <see cref="SaveChangesAsync"/>, in
    /// the same transaction as the state change that raised it.
    /// </summary>
    void Publish(IIntegrationEvent message);

    Task<int> SaveChangesAsync(CancellationToken cancellationToken = default);
}
