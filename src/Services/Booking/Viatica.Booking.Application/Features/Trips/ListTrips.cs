using Microsoft.EntityFrameworkCore;
using Relevox;
using Viatica.Booking.Domain;
using Viatica.Kernel;
using Viatica.Kernel.Querying;

namespace Viatica.Booking.Application.Features.Trips;

/// <summary>
/// The columns a trip list can filter and sort on. A member-init class, not a record, because EF Core translates a
/// filter on an object initialiser and cannot on a positional constructor.
/// </summary>
public sealed class TripRow
{
    public Guid Id { get; init; }

    public TripStatus Status { get; init; }

    public DateTimeOffset CreatedAt { get; init; }

    public DateTimeOffset UpdatedAt { get; init; }

    public int ItemCount { get; init; }

    public decimal TotalAmount { get; init; }

    public string Currency { get; init; } = Money.Euro;
}

public sealed record TripSummary(Guid Id, TripStatus Status, Money Total, int ItemCount, DateTimeOffset CreatedAt, DateTimeOffset UpdatedAt);

/// <summary>The caller's own trips, newest first (BKG-5). Someone else's trips are not in the query, not filtered out afterwards.</summary>
public sealed record ListTrips(CollectionQuery<TripRow> Query, Caller Caller) : IRequest<KeysetPage<TripSummary>>;

public static class TripQueries
{
    public static QueryFields<TripRow> Fields { get; } = new QueryFields<TripRow>()
        .Key("id", static t => t.Id)
        .Field("status", static t => t.Status)
        .Field("createdAt", static t => t.CreatedAt, sortable: true)
        .Field("updatedAt", static t => t.UpdatedAt, sortable: true)
        .DefaultOrderBy("createdAt desc");
}

public sealed class ListTripsHandler(IBookingDb db) : IRequestHandler<ListTrips, KeysetPage<TripSummary>>
{
    public async Task<KeysetPage<TripSummary>> Handle(ListTrips request, CancellationToken cancellationToken)
    {
        Guid traveller = request.Caller.TravellerId ?? throw BookingErrors.TripNotFound();

        IQueryable<TripRow> rows = db.Trips.AsNoTracking()
            .Where(t => t.TravellerId == traveller)
            .Select(static t => new TripRow
            {
                Id = t.Id,
                Status = t.Status,
                CreatedAt = t.CreatedAt,
                UpdatedAt = t.UpdatedAt,
                ItemCount = t.Items.Count,
                TotalAmount = t.Items.Where(static i => i.Status != ItemStatus.Cancelled && i.Status != ItemStatus.Rejected).Sum(static i => i.TotalAmount ?? i.ExpectedAmount),
                Currency = t.Currency,
            });

        List<TripRow> page = await request.Query.ApplyTo(rows).ToListAsync(cancellationToken);

        return request.Query.ToPage(
            page,
            static r => new TripSummary(r.Id, r.Status, new Money(r.TotalAmount, r.Currency), r.ItemCount, r.CreatedAt, r.UpdatedAt));
    }
}
