using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage;
using Viatica.Cars.Domain;

namespace Viatica.Cars.Application.Features.Holds;

/// <summary>
/// The statements that move cars between "free", "held" and "sold" for every day of one hold. Each is one
/// conditional UPDATE over all the days, with the condition in the WHERE clause. Nothing is read first and written
/// after, so two checkouts for the last car of a day cannot both see it free.
/// </summary>
internal static class HoldInventory
{
    /// <summary>
    /// Holds one car on every day, or none: the count of rows the statement touched must equal the number of days,
    /// otherwise some day had no car (or no row at all) and the whole statement is rolled back to the savepoint.
    /// </summary>
    public static async Task<bool> TryHoldAsync(
        ICarsDb db, IDbContextTransaction transaction, Guid locationId, Guid categoryId,
        DateOnly first, DateOnly last, int days, CancellationToken cancellationToken)
    {
        const string Savepoint = "before_hold";
        await transaction.CreateSavepointAsync(Savepoint, cancellationToken);

        int touched = await Days(db, locationId, categoryId, first, last)
            .Where(d => d.Allotment - d.Sold - d.Held >= 1)
            .ExecuteUpdateAsync(static s => s.SetProperty(d => d.Held, d => d.Held + 1), cancellationToken);

        if (touched == days)
        {
            return true;
        }

        await transaction.RollbackToSavepointAsync(Savepoint, cancellationToken);
        return false;
    }

    /// <summary>A held car becomes a sold one on every day. The hold owns those units, so none can be missing.</summary>
    public static Task CommitAsync(ICarsDb db, CarHold hold, CancellationToken cancellationToken) =>
        MoveAsync(
            db, hold,
            query => query.Where(d => d.Held >= 1)
                .ExecuteUpdateAsync(static s => s.SetProperty(d => d.Held, d => d.Held - 1).SetProperty(d => d.Sold, d => d.Sold + 1), cancellationToken));

    /// <summary>A held car goes back to free, on release and on expiry.</summary>
    public static Task ReleaseAsync(ICarsDb db, CarHold hold, CancellationToken cancellationToken) =>
        MoveAsync(
            db, hold,
            query => query.Where(d => d.Held >= 1)
                .ExecuteUpdateAsync(static s => s.SetProperty(d => d.Held, d => d.Held - 1), cancellationToken));

    /// <summary>A sold car goes back to free when its booking is cancelled.</summary>
    public static Task CancelAsync(ICarsDb db, CarHold hold, CancellationToken cancellationToken) =>
        MoveAsync(
            db, hold,
            query => query.Where(d => d.Sold >= 1)
                .ExecuteUpdateAsync(static s => s.SetProperty(d => d.Sold, d => d.Sold - 1), cancellationToken));

    private static async Task MoveAsync(
        ICarsDb db, CarHold hold, Func<IQueryable<InventoryDay>, Task<int>> update)
    {
        int touched = await update(Days(db, hold.LocationId, hold.CategoryId, hold.FirstDay, hold.LastDay));
        if (touched != hold.Days)
        {
            // The hold says it owns a unit on each of these days and the counters disagree. Carrying on would hide
            // a broken invariant; throwing rolls the transaction back and lets the consumer retry, then dead-letter.
            throw new InvalidOperationException(
                $"Hold {hold.Id} covers {hold.Days} days but only {touched} had a unit to move; the inventory counters are inconsistent.");
        }
    }

    private static IQueryable<InventoryDay> Days(ICarsDb db, Guid locationId, Guid categoryId, DateOnly first, DateOnly last) =>
        db.InventoryDays.Where(d => d.LocationId == locationId && d.CategoryId == categoryId && d.Date >= first && d.Date <= last);
}
