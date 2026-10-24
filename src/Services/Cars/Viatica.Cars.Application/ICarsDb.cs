using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Viatica.Cars.Domain;
using Viatica.Contracts;

namespace Viatica.Cars.Application;

/// <summary>
/// What the use cases may do with the database. The sets are the service's tables; the two extra members are the
/// pieces that cannot be written without the provider's SQL or the outbox, which Infrastructure owns.
/// </summary>
public interface ICarsDb
{
    DbSet<RentalLocation> Locations { get; }

    DbSet<CarCategory> Categories { get; }

    DbSet<PartnerSettings> PartnerSettings { get; }

    DbSet<InventoryDay> InventoryDays { get; }

    DbSet<CarHold> Holds { get; }

    DatabaseFacade Database { get; }

    /// <summary>Queues an integration event in the outbox; it commits with the next <see cref="SaveChangesAsync"/>.</summary>
    void Publish(IIntegrationEvent integrationEvent);

    /// <summary>
    /// Takes the next availability version of one category at one location. The row it updates is locked until the
    /// transaction ends, so two writers of the same category and location are ordered by their commits, and the
    /// version order is the order their events leave the outbox. Call it inside a transaction.
    /// </summary>
    Task<long> NextAvailabilityVersionAsync(Guid locationId, Guid categoryId, CancellationToken cancellationToken);

    Task<int> SaveChangesAsync(CancellationToken cancellationToken = default);
}
