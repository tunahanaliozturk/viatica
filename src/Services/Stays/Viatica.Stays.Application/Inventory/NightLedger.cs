using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage;
using Viatica.Contracts;
using Viatica.Kernel;
using Viatica.Stays.Domain;

namespace Viatica.Stays.Application.Inventory;

/// <summary>
/// Every change to <c>sold</c> and <c>held</c> goes through here. Each is one <c>UPDATE</c> over the nights of a
/// stay whose <c>WHERE</c> clause carries the rule, never a read followed by a write: two travellers racing for the
/// last room are ordered by the row lock, and the second one's condition is evaluated against the first one's
/// result.
/// </summary>
/// <remarks>
/// The statements run inside the caller's transaction (the consumer's, for a hold request). The caller commits
/// them together with the hold row, the inbox row and the outbox rows, or none of it.
/// </remarks>
public sealed class NightLedger(IStaysDb db)
{
    private const string Savepoint = "reserve_nights";

    /// <summary>
    /// Reserves <paramref name="quantity"/> rooms on every night of the stay, or on none. One statement updates
    /// every night that has enough rooms; if it touched fewer rows than there are nights, at least one night was
    /// short, and the nights it did reserve are rolled back to the savepoint taken just before.
    /// </summary>
    public async Task<bool> TryReserveAsync(Guid roomTypeId, StayDates dates, int quantity, CancellationToken cancellationToken)
    {
        IDbContextTransaction transaction = db.Database.CurrentTransaction
            ?? throw new InvalidOperationException("Rooms are reserved inside a transaction, so a short night can undo the others.");
        DateOnly from = dates.CheckIn;
        DateOnly to = dates.CheckOut;

        await transaction.CreateSavepointAsync(Savepoint, cancellationToken);

        int reserved = await db.RoomNights
            .Where(n => n.RoomTypeId == roomTypeId && n.Night >= from && n.Night < to && n.Allotment - n.Sold - n.Held >= quantity)
            .ExecuteUpdateAsync(s => s.SetProperty(n => n.Held, n => n.Held + quantity), cancellationToken);

        if (reserved == dates.Nights)
        {
            await transaction.ReleaseSavepointAsync(Savepoint, cancellationToken);
            return true;
        }

        await transaction.RollbackToSavepointAsync(Savepoint, cancellationToken);
        return false;
    }

    /// <summary>A hold becomes a sale. No condition beyond the hold being ours: the rooms were reserved already.</summary>
    public Task MoveHeldToSoldAsync(Hold hold, CancellationToken cancellationToken) =>
        Apply(hold, "commit", Nights(hold)
            .ExecuteUpdateAsync(s => s.SetProperty(n => n.Held, n => n.Held - hold.Quantity).SetProperty(n => n.Sold, n => n.Sold + hold.Quantity), cancellationToken));

    /// <summary>A hold is given back, by the saga or by the sweeper.</summary>
    public Task ReleaseHeldAsync(Hold hold, CancellationToken cancellationToken) =>
        Apply(hold, "release", Nights(hold)
            .ExecuteUpdateAsync(s => s.SetProperty(n => n.Held, n => n.Held - hold.Quantity), cancellationToken));

    /// <summary>A committed stay was cancelled and its rooms go back on sale.</summary>
    public Task ReleaseSoldAsync(Hold hold, CancellationToken cancellationToken) =>
        Apply(hold, "cancel", Nights(hold)
            .ExecuteUpdateAsync(s => s.SetProperty(n => n.Sold, n => n.Sold - hold.Quantity), cancellationToken));

    /// <summary>
    /// Takes the next <c>AvailabilityVersion</c> of the room type and queues <c>StayAvailabilityChanged</c> with the
    /// current numbers for <paramref name="nights"/>. Called last, after the nights are changed: the row lock on the
    /// counter is held until commit, so versions are handed out in commit order, and a writer holds it for as short
    /// a time as it can.
    /// </summary>
    public async Task<long> PublishAvailabilityAsync(Guid propertyId, Guid roomTypeId, DateOnly from, DateOnly toInclusive, CancellationToken cancellationToken)
    {
        int bumped = await db.Inventories
            .Where(i => i.RoomTypeId == roomTypeId)
            .ExecuteUpdateAsync(s => s.SetProperty(i => i.AvailabilityVersion, i => i.AvailabilityVersion + 1), cancellationToken);
        if (bumped != 1)
        {
            throw new InvalidOperationException($"Room type {roomTypeId} has no inventory row.");
        }

        // The counter row is locked by this transaction now, so this read sees the version it just wrote.
        long version = await db.Inventories.AsNoTracking()
            .Where(i => i.RoomTypeId == roomTypeId)
            .Select(static i => i.AvailabilityVersion)
            .SingleAsync(cancellationToken);

        var rows = await db.RoomNights.AsNoTracking()
            .Where(n => n.RoomTypeId == roomTypeId && n.Night >= from && n.Night <= toInclusive)
            .OrderBy(static n => n.Night)
            .Select(static n => new { n.Night, Available = n.Allotment - n.Sold - n.Held, n.Rate })
            .ToListAsync(cancellationToken);

        db.Publish(new StayAvailabilityChanged(
            propertyId,
            roomTypeId,
            version,
            [.. rows.Select(static r => new DateAvailability(r.Night, r.Available, new Price(r.Rate, Money.Euro)))]));
        return version;
    }

    public Task<long> PublishAvailabilityAsync(Hold hold, CancellationToken cancellationToken) =>
        PublishAvailabilityAsync(hold.PropertyId, hold.RoomTypeId, hold.CheckIn, hold.CheckOut.AddDays(-1), cancellationToken);

    private IQueryable<RoomNight> Nights(Hold hold)
    {
        Guid roomTypeId = hold.RoomTypeId;
        DateOnly from = hold.CheckIn;
        DateOnly to = hold.CheckOut;
        return db.RoomNights.Where(n => n.RoomTypeId == roomTypeId && n.Night >= from && n.Night < to);
    }

    // A night row is never deleted, so a count that differs from the stay's length means the data is broken. Failing
    // rolls the whole message back, which is better than a ledger that is quietly off by a night.
    private static async Task Apply(Hold hold, string what, Task<int> update)
    {
        int touched = await update;
        if (touched != hold.Dates.Nights)
        {
            throw new InvalidOperationException(
                $"The {what} of hold {hold.Id} changed {touched} nights, expected {hold.Dates.Nights}.");
        }
    }
}
