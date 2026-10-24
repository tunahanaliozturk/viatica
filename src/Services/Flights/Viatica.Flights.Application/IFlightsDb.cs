using Microsoft.EntityFrameworkCore;
using Viatica.Contracts;
using Viatica.Flights.Domain;

namespace Viatica.Flights.Application;

/// <summary>The part of the service's database the use cases touch, and the way to queue an event with a state change.</summary>
public interface IFlightsDb
{
    DbSet<FlightOrder> Orders { get; }

    /// <summary>Queues an event in the outbox. It is written by the next <see cref="SaveChangesAsync"/>, in the same transaction.</summary>
    void Publish(IIntegrationEvent message);

    Task<int> SaveChangesAsync(CancellationToken cancellationToken = default);
}
