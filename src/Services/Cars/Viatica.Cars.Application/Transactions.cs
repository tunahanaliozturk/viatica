using Microsoft.EntityFrameworkCore.Storage;

namespace Viatica.Cars.Application;

internal static class Transactions
{
    /// <summary>
    /// Runs <paramref name="work"/> in a transaction and commits it. When the caller already opened one, as the
    /// Kafka consumer does around every handler so the inbox row commits with the effect, the work joins it and
    /// the caller commits.
    /// </summary>
    public static async Task<T> InTransactionAsync<T>(
        this ICarsDb db, Func<IDbContextTransaction, Task<T>> work, CancellationToken cancellationToken)
    {
        if (db.Database.CurrentTransaction is { } joined)
        {
            return await work(joined);
        }

        await using IDbContextTransaction own = await db.Database.BeginTransactionAsync(cancellationToken);
        T result = await work(own);
        await own.CommitAsync(cancellationToken);
        return result;
    }
}
