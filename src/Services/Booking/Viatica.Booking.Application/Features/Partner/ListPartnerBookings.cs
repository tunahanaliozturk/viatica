using Microsoft.EntityFrameworkCore;
using Relevox;
using Viatica.Booking.Domain;
using Viatica.Kernel;
using Viatica.Kernel.Querying;

namespace Viatica.Booking.Application.Features.Partner;

public sealed class BookingRow
{
    public Guid Id { get; init; }

    public Guid TripId { get; init; }

    public ItemKind Kind { get; init; }

    public ItemStatus Status { get; init; }

    public string Description { get; init; } = "";

    public decimal TotalAmount { get; init; }

    public string Currency { get; init; } = Money.Euro;

    public DateTimeOffset BookedAt { get; init; }

    public DateTimeOffset? StartsAt { get; init; }

    public decimal? RefundAmount { get; init; }
}

/// <summary>
/// A booking against a partner's inventory: one confirmed or cancelled item. It names the trip and the item, what was
/// booked, when it starts and what it cost. It does not name the traveller: the partner needs to know what to hand
/// over, and the traveller's details are not Booking's to give once the saga has ended.
/// </summary>
public sealed record PartnerBooking(
    Guid ItemId,
    Guid TripId,
    ItemKind Kind,
    ItemStatus Status,
    string Description,
    Money Total,
    DateTimeOffset BookedAt,
    DateTimeOffset? StartsAt,
    Money? Refunded);

/// <summary>The bookings against the calling partner's own inventory (BKG-5). Another partner's bookings are not in the query.</summary>
public sealed record ListPartnerBookings(CollectionQuery<BookingRow> Query, Guid PartnerId) : IRequest<KeysetPage<PartnerBooking>>;

public static class PartnerBookingQueries
{
    public static QueryFields<BookingRow> Fields { get; } = new QueryFields<BookingRow>()
        .Key("id", static b => b.Id)
        .Field("tripId", static b => b.TripId)
        .Field("kind", static b => b.Kind)
        .Field("status", static b => b.Status)
        .Field("bookedAt", static b => b.BookedAt, sortable: true)
        .Field("startsAt", static b => b.StartsAt)
        .Field("total", static b => b.TotalAmount, sortable: true)
        .DefaultOrderBy("bookedAt desc");
}

public sealed class ListPartnerBookingsHandler(IBookingDb db) : IRequestHandler<ListPartnerBookings, KeysetPage<PartnerBooking>>
{
    public async Task<KeysetPage<PartnerBooking>> Handle(ListPartnerBookings request, CancellationToken cancellationToken)
    {
        // Only items of a trip that ended well. A stay that is held or being committed is not a booking yet, and one on
        // a trip that is being unwound never will be.
        IQueryable<BookingRow> rows =
            from item in db.Items.AsNoTracking()
            join trip in db.Trips.AsNoTracking() on item.TripId equals trip.Id
            where item.PartnerId == request.PartnerId
                && (trip.Status == TripStatus.Confirmed || trip.Status == TripStatus.Cancelled)
                && (item.Status == ItemStatus.Committed || item.Status == ItemStatus.Cancelled)
            select new BookingRow
            {
                Id = item.Id,
                TripId = item.TripId,
                Kind = item.Kind,
                Status = item.Status,
                Description = item.Description,
                TotalAmount = item.TotalAmount ?? item.ExpectedAmount,
                Currency = item.Currency,
                BookedAt = item.AddedAt,
                StartsAt = item.StartsAt,
                RefundAmount = item.RefundAmount,
            };

        List<BookingRow> page = await request.Query.ApplyTo(rows).ToListAsync(cancellationToken);

        return request.Query.ToPage(
            page,
            static r => new PartnerBooking(
                r.Id, r.TripId, r.Kind, r.Status, r.Description, new Money(r.TotalAmount, r.Currency), r.BookedAt, r.StartsAt,
                r.RefundAmount is { } refund ? new Money(refund, r.Currency) : null));
    }
}
