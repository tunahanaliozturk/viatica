using System.Runtime.CompilerServices;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Storage;
using Microsoft.Extensions.DependencyInjection;

namespace Viatica.Kernel.Messaging;

/// <summary>
/// Makes a key's outbox rows commit in the order of their sequence numbers. An identity value is handed out when the
/// row is inserted, not when its transaction commits, so with two overlapping transactions the later sequence can
/// commit first and the relay, which sees only committed rows, would send it before the earlier one. Taking a
/// transaction scoped advisory lock per key before the insert makes writers of one key take turns: the second
/// insert, and so its sequence number, happens after the first commit.
/// </summary>
/// <remarks>
/// The relay was the other candidate: refusing to send past a gap. It cannot tell a gap that a rollback left from one
/// an open transaction will fill, so it would have to guess with a timeout. A lock decides it exactly, at the price
/// of one statement per save and of writers of one key waiting for each other's transaction, which for a key that is
/// an aggregate id is the order the aggregate wants anyway (ADR 0007).
/// </remarks>
internal sealed class OutboxKeyLocks : SaveChangesInterceptor
{
    private OutboxKeyLocks()
    {
    }

    public static OutboxKeyLocks Instance { get; } = new();

    // EF Core takes interceptors from the context's options, not from the application service provider, so this is
    // wired per context type with ConfigureDbContext. Registered once per type even when a host adds both a relay
    // and a subscription, because the same interceptor twice would try to own the transaction twice.
    internal static void AddTo<TDbContext>(IServiceCollection services)
        where TDbContext : DbContext
    {
        if (services.Any(static d => d.ServiceType == typeof(Registered<TDbContext>)))
        {
            return;
        }

        services.AddSingleton<Registered<TDbContext>>();
        services.ConfigureDbContext<TDbContext>(static options => options.AddInterceptors(Instance));
    }

    /// <summary>
    /// Throws when <paramref name="db"/> was not set up with the interceptor, because an event queued then would
    /// reach Kafka in the wrong order under load and nothing would say so.
    /// </summary>
    internal static void EnsureOn(DbContext db)
    {
        bool present = db.GetService<IDbContextOptions>().Extensions
            .OfType<CoreOptionsExtension>()
            .Any(static e => e.Interceptors?.Contains(Instance) == true);
        if (!present)
        {
            throw new InvalidOperationException(
                $"{db.GetType().Name} queues an integration event but is not set up for the outbox. Register " +
                $"{nameof(MessagingServiceCollectionExtensions.AddOutboxRelay)}<{db.GetType().Name}> or " +
                $"{nameof(MessagingServiceCollectionExtensions.AddSubscription)}<{db.GetType().Name}> in the host, so saves take the per key lock that keeps events in order.");
        }
    }

    private sealed class Registered<TDbContext>;

    // The transaction this interceptor began for a save that had none, so it can end it. A pooled context is reused,
    // so the entry is removed when the save ends.
    private readonly ConditionalWeakTable<DbContext, IDbContextTransaction> _owned = [];

    public override async ValueTask<InterceptionResult<int>> SavingChangesAsync(
        DbContextEventData eventData,
        InterceptionResult<int> result,
        CancellationToken cancellationToken = default)
    {
        if (eventData.Context is { } db && PendingKeys(db) is { Length: > 0 } keys)
        {
            if (db.Database.CurrentTransaction is null)
            {
                _owned.Add(db, await db.Database.BeginTransactionAsync(cancellationToken));
            }

            await db.Database.ExecuteSqlRawAsync(LockSql, [keys], cancellationToken);
        }

        return result;
    }

    public override InterceptionResult<int> SavingChanges(DbContextEventData eventData, InterceptionResult<int> result)
    {
        if (eventData.Context is { } db && PendingKeys(db) is { Length: > 0 } keys)
        {
            if (db.Database.CurrentTransaction is null)
            {
                _owned.Add(db, db.Database.BeginTransaction());
            }

            db.Database.ExecuteSqlRaw(LockSql, [keys]);
        }

        return result;
    }

    public override async ValueTask<int> SavedChangesAsync(SaveChangesCompletedEventData eventData, int result, CancellationToken cancellationToken = default)
    {
        if (Take(eventData.Context) is { } transaction)
        {
            await using (transaction)
            {
                await transaction.CommitAsync(cancellationToken);
            }
        }

        return result;
    }

    public override int SavedChanges(SaveChangesCompletedEventData eventData, int result)
    {
        if (Take(eventData.Context) is { } transaction)
        {
            using (transaction)
            {
                transaction.Commit();
            }
        }

        return result;
    }

    public override async Task SaveChangesFailedAsync(DbContextErrorEventData eventData, CancellationToken cancellationToken = default)
    {
        if (Take(eventData.Context) is { } transaction)
        {
            await transaction.DisposeAsync();
        }
    }

    public override void SaveChangesFailed(DbContextErrorEventData eventData) => Take(eventData.Context)?.Dispose();

    public override async Task SaveChangesCanceledAsync(DbContextEventData eventData, CancellationToken cancellationToken = default)
    {
        if (Take(eventData.Context) is { } transaction)
        {
            await transaction.DisposeAsync();
        }
    }

    public override void SaveChangesCanceled(DbContextEventData eventData) => Take(eventData.Context)?.Dispose();

    // One statement, one round trip, however many keys. Sorted ordinally by the caller: the lock order is the same in
    // every writer, so two transactions holding overlapping sets of keys cannot wait on each other.
    private const string LockSql =
        "SELECT pg_advisory_xact_lock(hashtextextended(k, 0)) FROM unnest({0}::text[]) AS k";

    private static string[] PendingKeys(DbContext db) =>
        [.. db.ChangeTracker.Entries<OutboxMessage>()
            .Where(static e => e.State == EntityState.Added)
            .Select(static e => e.Entity.PartitionKey)
            .Distinct()
            .Order(StringComparer.Ordinal)];

    private IDbContextTransaction? Take(DbContext? db) =>
        db is not null && _owned.TryGetValue(db, out IDbContextTransaction? transaction) && _owned.Remove(db) ? transaction : null;
}
