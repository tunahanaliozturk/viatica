using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.ChangeTracking;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Viatica.Booking.Domain;
using Viatica.Contracts;

namespace Viatica.Booking.Application;

/// <summary>
/// What the use cases may do with the database. The sets are the service's tables; the other members are the pieces
/// that cannot be written without the provider's SQL or the outbox, which Infrastructure owns.
/// </summary>
public interface IBookingDb
{
    DbSet<Trip> Trips { get; }

    DbSet<TripItem> Items { get; }

    DatabaseFacade Database { get; }

    ChangeTracker ChangeTracker { get; }

    /// <summary>Queues an integration event in the outbox; it commits with the next <see cref="SaveChangesAsync"/>.</summary>
    void Publish(IIntegrationEvent message);

    /// <summary>
    /// Takes a row lock on the trip until the transaction ends, so the events of one trip, which arrive on four
    /// topics and are handled by four consumers, are applied one after the other and never against a stale copy.
    /// Call it inside a transaction, before loading the trip.
    /// </summary>
    Task LockTripAsync(Guid tripId, CancellationToken cancellationToken);

    Task<int> SaveChangesAsync(CancellationToken cancellationToken = default);
}
