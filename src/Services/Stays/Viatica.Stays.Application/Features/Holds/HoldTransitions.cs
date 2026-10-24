using Microsoft.EntityFrameworkCore;
using Viatica.Stays.Domain;

namespace Viatica.Stays.Application.Features.Holds;

/// <summary>
/// Moves a hold from one state to the next with a compare-and-set <c>UPDATE</c>. The saga's commit and the expiry
/// sweeper race on the same row, and a read-then-write would let both win; here the database picks one, and the
/// loser sees that it changed nothing.
/// </summary>
/// <remarks>
/// Each method returns the hold when this call moved it and null when it did not (a redelivery, a hold that is
/// already past that state, a hold that never existed). Callers change the nights only after a non-null answer, in
/// the same transaction, so the hold's state and the room counts cannot disagree.
/// </remarks>
public sealed class HoldTransitions(IStaysDb db, TimeProvider clock)
{
    /// <summary>
    /// Held to committed, for as long as the hold is held. The sweeper is the only authority on expiry: a commit
    /// that reaches a hold past its <c>ExpiresAt</c> before the sweeper does still wins, because the rooms are still
    /// reserved and refusing would only make Booking unwind a trip whose flights are already ticketed. Cars follows
    /// the same rule, so the saga sees one behaviour from both.
    /// </summary>
    public Task<Hold?> CommitAsync(Guid holdId, CancellationToken cancellationToken) =>
        MoveAsync(holdId, h => h.Id == holdId && h.Status == HoldStatus.Held, HoldStatus.Committed, clock.GetUtcNow(), cancellationToken);

    public Task<Hold?> ReleaseAsync(Guid holdId, CancellationToken cancellationToken) =>
        MoveAsync(holdId, h => h.Id == holdId && h.Status == HoldStatus.Held, HoldStatus.Released, clock.GetUtcNow(), cancellationToken);

    /// <summary>Held to expired, once the expiry has passed.</summary>
    public Task<Hold?> ExpireAsync(Guid holdId, CancellationToken cancellationToken)
    {
        DateTimeOffset now = clock.GetUtcNow();
        return MoveAsync(holdId, h => h.Id == holdId && h.Status == HoldStatus.Held && h.ExpiresAt <= now, HoldStatus.Expired, now, cancellationToken);
    }

    public Task<Hold?> CancelAsync(Guid holdId, CancellationToken cancellationToken) =>
        MoveAsync(holdId, h => h.Id == holdId && h.Status == HoldStatus.Committed, HoldStatus.Cancelled, clock.GetUtcNow(), cancellationToken);

    /// <summary>Where a hold stands, for the log line that explains why a request changed nothing.</summary>
    public async Task<HoldStatus?> StatusOfAsync(Guid holdId, CancellationToken cancellationToken) =>
        await db.Holds.AsNoTracking()
            .Where(h => h.Id == holdId)
            .Select(static h => (HoldStatus?)h.Status)
            .SingleOrDefaultAsync(cancellationToken);

    private async Task<Hold?> MoveAsync(
        Guid holdId, System.Linq.Expressions.Expression<Func<Hold, bool>> from, HoldStatus to, DateTimeOffset now, CancellationToken cancellationToken)
    {
        int moved = await db.Holds
            .Where(from)
            .ExecuteUpdateAsync(s => s.SetProperty(h => h.Status, to).SetProperty(h => h.UpdatedAt, now), cancellationToken);

        return moved == 0 ? null : await db.Holds.AsNoTracking().SingleAsync(h => h.Id == holdId, cancellationToken);
    }
}
