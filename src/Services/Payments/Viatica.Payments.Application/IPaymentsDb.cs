using Microsoft.EntityFrameworkCore;
using Viatica.Contracts;
using Viatica.Payments.Domain;

namespace Viatica.Payments.Application;

/// <summary>The part of the service's database the use cases touch, and the way to queue an event with a state change.</summary>
public interface IPaymentsDb
{
    DbSet<Payment> Payments { get; }

    DbSet<PaymentMethod> Methods { get; }

    DbSet<OperationRecord> Operations { get; }

    /// <summary>Queues an event in the outbox. It is written by the next <see cref="SaveChangesAsync"/>, in the same transaction.</summary>
    void Publish(IIntegrationEvent message);

    Task<int> SaveChangesAsync(CancellationToken cancellationToken = default);
}
