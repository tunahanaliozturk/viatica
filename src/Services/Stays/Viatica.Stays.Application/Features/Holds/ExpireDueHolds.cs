using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage;
using Viatica.Contracts;
using Viatica.Stays.Application.Inventory;
using Viatica.Stays.Domain;

namespace Viatica.Stays.Application.Features.Holds;

/// <summary>
/// What the sweeper does: find the holds whose time is up and give each one's rooms back, announcing
/// <see cref="StayHoldExpired"/>. A hold is one transaction, so a failure on one leaves the others to be tried again.
/// </summary>
/// <remarks>
/// Every instance of the service runs a sweeper. They do not need to coordinate: the compare-and-set in
/// <see cref="HoldTransitions.ExpireAsync"/> lets exactly one of them expire a given hold, and the others read zero
/// rows changed and move on. The cost is a few wasted reads per tick, which a leader lock would save at the price of
/// another failure mode.
/// </remarks>
public sealed class ExpireDueHolds(IStaysDb db, HoldTransitions holds, NightLedger ledger, TimeProvider clock)
{
    /// <summary>The oldest due holds first, so one that keeps failing cannot hide behind newer ones.</summary>
    public async Task<IReadOnlyList<Guid>> FindDueAsync(int batchSize, CancellationToken cancellationToken)
    {
        DateTimeOffset now = clock.GetUtcNow();
        return await db.Holds.AsNoTracking()
            .Where(h => h.Status == HoldStatus.Held && h.ExpiresAt <= now)
            .OrderBy(static h => h.ExpiresAt)
            .Select(static h => h.Id)
            .Take(batchSize)
            .ToListAsync(cancellationToken);
    }

    /// <summary>Expires one hold. Returns false when another sweeper, or a commit, got there first.</summary>
    public async Task<bool> ExpireAsync(Guid holdId, CancellationToken cancellationToken)
    {
        await using IDbContextTransaction transaction = await db.Database.BeginTransactionAsync(cancellationToken);

        if (await holds.ExpireAsync(holdId, cancellationToken) is not { } hold)
        {
            return false;
        }

        await ledger.ReleaseHeldAsync(hold, cancellationToken);
        await ledger.PublishAvailabilityAsync(hold, cancellationToken);
        db.Publish(new StayHoldExpired(hold.Id, hold.TripId, hold.ItemId));
        await db.SaveChangesAsync(cancellationToken);
        await transaction.CommitAsync(cancellationToken);

        StaysTelemetry.HoldsExpired.Add(1);
        return true;
    }
}
