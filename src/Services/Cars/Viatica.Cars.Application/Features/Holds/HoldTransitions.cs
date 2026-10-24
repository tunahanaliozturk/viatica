using Microsoft.EntityFrameworkCore;
using Relevox;
using Viatica.Cars.Domain;
using Viatica.Contracts;

namespace Viatica.Cars.Application.Features.Holds;

/// <summary>What a request to move a hold did.</summary>
public enum Transition
{
    /// <summary>The hold moved now.</summary>
    Applied,

    /// <summary>The hold was already where the request wants it. The caller confirms again; Booking may be resending.</summary>
    Repeated,

    /// <summary>The hold is somewhere the request cannot apply to, or is not this trip's. Nothing changed.</summary>
    Ignored,
}

public sealed record CommitHold(Guid TripId, Guid ItemId, Guid HoldId) : IRequest<Transition>;

public sealed record ReleaseHold(Guid TripId, Guid ItemId, Guid HoldId) : IRequest<Transition>;

public sealed record CancelBooking(Guid TripId, Guid ItemId, Guid HoldId) : IRequest<Transition>;

/// <summary>
/// Confirms a held car: it stops being promised and starts being sold, on every day. A hold that has already
/// expired is not committed late; Booking learned of the expiry from <c>CarHoldExpired</c>.
/// </summary>
public sealed class CommitHoldHandler(ICarsDb db, TimeProvider clock) : IRequestHandler<CommitHold, Transition>
{
    public async Task<Transition> Handle(CommitHold request, CancellationToken cancellationToken)
    {
        CarHold? hold = await HoldLookup.FindAsync(db, request.HoldId, request.TripId, request.ItemId, cancellationToken);
        if (hold is null)
        {
            return Transition.Ignored;
        }

        if (hold.Status == HoldStatus.Committed)
        {
            return Transition.Repeated;
        }

        if (hold.Status != HoldStatus.Held)
        {
            return Transition.Ignored;
        }

        return await db.InTransactionAsync(
            async transaction =>
            {
                // The hold row goes first. Saving it claims the transition (or throws, if a writer got there first
                // and the consumer retries and finds the hold moved), so the inventory below only ever changes for
                // a transition this transaction owns.
                _ = hold.Commit(clock.GetUtcNow());
                await db.SaveChangesAsync(cancellationToken);
                await HoldInventory.CommitAsync(db, hold, cancellationToken);
                return Transition.Applied;
            },
            cancellationToken);
    }
}

/// <summary>
/// Gives a held car back. A hold that does not exist, or has expired, is also "released": the caller wants the car
/// not to be held, and it is not.
/// </summary>
public sealed class ReleaseHoldHandler(ICarsDb db, TimeProvider clock) : IRequestHandler<ReleaseHold, Transition>
{
    public async Task<Transition> Handle(ReleaseHold request, CancellationToken cancellationToken)
    {
        CarHold? hold = await db.Holds.FirstOrDefaultAsync(h => h.Id == request.HoldId, cancellationToken);
        if (hold is null)
        {
            return Transition.Repeated;
        }

        if (hold.TripId != request.TripId || hold.ItemId != request.ItemId)
        {
            return Transition.Ignored;
        }

        if (hold.Status is HoldStatus.Released or HoldStatus.Expired)
        {
            return Transition.Repeated;
        }

        if (hold.Status != HoldStatus.Held)
        {
            return Transition.Ignored;
        }

        return await db.InTransactionAsync(
            async transaction =>
            {
                _ = hold.Release(clock.GetUtcNow());
                await db.SaveChangesAsync(cancellationToken);
                await HoldInventory.ReleaseAsync(db, hold, cancellationToken);
                await Announcements.AvailabilityAsync(db, hold.LocationId, hold.CategoryId, hold.FirstDay, hold.LastDay, cancellationToken);
                await db.SaveChangesAsync(cancellationToken);
                return Transition.Applied;
            },
            cancellationToken);
    }
}

/// <summary>Cancels a confirmed booking and gives its car back. Refund rules belong to Booking.</summary>
public sealed class CancelBookingHandler(ICarsDb db, TimeProvider clock) : IRequestHandler<CancelBooking, Transition>
{
    public async Task<Transition> Handle(CancelBooking request, CancellationToken cancellationToken)
    {
        CarHold? hold = await HoldLookup.FindAsync(db, request.HoldId, request.TripId, request.ItemId, cancellationToken);
        if (hold is null)
        {
            return Transition.Ignored;
        }

        if (hold.Status == HoldStatus.Cancelled)
        {
            return Transition.Repeated;
        }

        if (hold.Status != HoldStatus.Committed)
        {
            return Transition.Ignored;
        }

        return await db.InTransactionAsync(
            async transaction =>
            {
                _ = hold.Cancel(clock.GetUtcNow());
                await db.SaveChangesAsync(cancellationToken);
                await HoldInventory.CancelAsync(db, hold, cancellationToken);
                await Announcements.AvailabilityAsync(db, hold.LocationId, hold.CategoryId, hold.FirstDay, hold.LastDay, cancellationToken);
                await db.SaveChangesAsync(cancellationToken);
                return Transition.Applied;
            },
            cancellationToken);
    }
}

internal static class HoldLookup
{
    /// <summary>The hold, if it exists and belongs to this trip item. Another trip's id finds nothing.</summary>
    public static async Task<CarHold?> FindAsync(
        ICarsDb db, Guid holdId, Guid tripId, Guid itemId, CancellationToken cancellationToken)
    {
        CarHold? hold = await db.Holds.FirstOrDefaultAsync(h => h.Id == holdId, cancellationToken);
        return hold is not null && hold.TripId == tripId && hold.ItemId == itemId ? hold : null;
    }
}

/// <summary>The ids of holds whose time is up, oldest first. The sweeper expires each in a transaction of its own.</summary>
public sealed record FindExpiredHolds(int Limit) : IRequest<IReadOnlyList<Guid>>;

public sealed class FindExpiredHoldsHandler(ICarsDb db, TimeProvider clock) : IRequestHandler<FindExpiredHolds, IReadOnlyList<Guid>>
{
    public async Task<IReadOnlyList<Guid>> Handle(FindExpiredHolds request, CancellationToken cancellationToken)
    {
        DateTimeOffset now = clock.GetUtcNow();
        return await db.Holds.AsNoTracking()
            .Where(h => h.Status == HoldStatus.Held && h.ExpiresAt <= now)
            .OrderBy(static h => h.ExpiresAt)
            .Select(static h => h.Id)
            .Take(request.Limit)
            .ToListAsync(cancellationToken);
    }
}

public sealed record ExpireHold(Guid HoldId) : IRequest<Transition>;

/// <summary>
/// Gives back the car of a hold nobody confirmed in time and tells Booking. Safe against a commit arriving at the
/// same moment: the hold row's version lets exactly one of the two save.
/// </summary>
public sealed class ExpireHoldHandler(ICarsDb db, TimeProvider clock) : IRequestHandler<ExpireHold, Transition>
{
    public async Task<Transition> Handle(ExpireHold request, CancellationToken cancellationToken)
    {
        DateTimeOffset now = clock.GetUtcNow();
        CarHold? hold = await db.Holds.FirstOrDefaultAsync(h => h.Id == request.HoldId, cancellationToken);
        if (hold is null || hold.Status != HoldStatus.Held || hold.ExpiresAt > now)
        {
            return Transition.Ignored;
        }

        return await db.InTransactionAsync(
            async transaction =>
            {
                _ = hold.Expire(now);
                await db.SaveChangesAsync(cancellationToken);
                await HoldInventory.ReleaseAsync(db, hold, cancellationToken);
                await Announcements.AvailabilityAsync(db, hold.LocationId, hold.CategoryId, hold.FirstDay, hold.LastDay, cancellationToken);
                db.Publish(new CarHoldExpired(hold.Id, hold.TripId, hold.ItemId));
                await db.SaveChangesAsync(cancellationToken);
                return Transition.Applied;
            },
            cancellationToken);
    }
}
