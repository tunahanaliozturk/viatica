using Microsoft.EntityFrameworkCore;
using Relevox;
using Viatica.Booking.Application.Features.Trips;
using Viatica.Booking.Domain;

namespace Viatica.Booking.Application.Features.Checkout;

/// <summary>
/// Opens a progress stream: checks that the caller may see the trip, and says where the trip stands now. The stream
/// itself polls the database, so any instance of the service can serve it, whichever one handled the answers.
/// </summary>
public sealed record OpenProgress(Guid TripId, Caller Caller) : IRequest<TripProgress>;

/// <summary>What changed since the last revision the stream sent, or null if nothing did.</summary>
public sealed record PollProgress(Guid TripId, int AfterRevision) : IRequest<TripProgress?>;

public sealed record TripProgress(int Revision, TripView Trip, bool Final);

public sealed class OpenProgressHandler(IBookingDb db) : IRequestHandler<OpenProgress, TripProgress>
{
    public async Task<TripProgress> Handle(OpenProgress request, CancellationToken cancellationToken)
    {
        Trip trip = await db.LoadForReadAsync(request.TripId, request.Caller, cancellationToken);
        return ProgressHandler.Of(trip);
    }
}

public sealed class PollProgressHandler(IBookingDb db) : IRequestHandler<PollProgress, TripProgress?>
{
    public async Task<TripProgress?> Handle(PollProgress request, CancellationToken cancellationToken)
    {
        // One narrow read to learn whether anything changed; the whole trip is read only when it did.
        int? revision = await db.Trips.AsNoTracking().Where(t => t.Id == request.TripId).Select(static t => (int?)t.Revision).FirstOrDefaultAsync(cancellationToken);
        if (revision is null || revision <= request.AfterRevision)
        {
            return null;
        }

        Trip? trip = await db.Trips.AsNoTracking().Include(static t => t.Items).FirstOrDefaultAsync(t => t.Id == request.TripId, cancellationToken);
        return trip is null ? null : ProgressHandler.Of(trip);
    }
}

internal static class ProgressHandler
{
    public static TripProgress Of(Trip trip) =>
        new(trip.Revision, Views.Of(trip), trip.Status is TripStatus.Confirmed or TripStatus.Failed or TripStatus.Cancelled);
}
