using System.Diagnostics;
using Microsoft.EntityFrameworkCore;
using Relevox;
using Viatica.Cars.Application.Quotes;
using Viatica.Cars.Domain;
using Viatica.Contracts;
using Viatica.Kernel;

namespace Viatica.Cars.Application.Features.Holds;

/// <summary>
/// Reserves one car for a checkout in progress. <c>HoldId</c> is Booking's, so asking twice finds the first hold.
/// </summary>
public sealed record PlaceHold(Guid TripId, Guid ItemId, Guid HoldId, string QuoteToken, int DriverAge) : IRequest<HoldOutcome>;

/// <summary>A car is held (or already was, for a repeated request).</summary>
public sealed record Placed(CarHold Hold);

/// <summary>Nothing was held, and why. Booking turns this into a rejected item.</summary>
public sealed record Refused(HoldRejectionReason Reason);

/// <summary>The hold exists but has moved on (released, expired, committed, cancelled). There is nothing to answer.</summary>
public sealed record Superseded(CarHold Hold);

/// <summary>
/// What placing a hold came to. None of the three is an error, so none is an exception: a sold-out day is the
/// normal outcome of the contention the service is built for (ADR 0001).
/// </summary>
public union HoldOutcome(Placed, Refused, Superseded);

public sealed class PlaceHoldHandler(ICarsDb db, QuoteTokenCodec tokens, TimeProvider clock) : IRequestHandler<PlaceHold, HoldOutcome>
{
    public async Task<HoldOutcome> Handle(PlaceHold request, CancellationToken cancellationToken)
    {
        using Activity? activity = CarsTelemetry.Source.StartActivity("cars.place_hold");
        activity?.SetTag("cars.hold.id", request.HoldId);

        CarHold? existing = await db.Holds.FirstOrDefaultAsync(h => h.Id == request.HoldId, cancellationToken);
        if (existing is not null)
        {
            // A repeat of a request we already served, under a new event id. Held and committed holds are answered
            // again; one that has moved on is left alone, because the answer would be stale.
            return existing.Status is HoldStatus.Held or HoldStatus.Committed
                ? new Placed(existing)
                : new Superseded(existing);
        }

        QuoteTokenStatus status = tokens.Read(request.QuoteToken, out CarQuote? quote);
        if (status == QuoteTokenStatus.Expired)
        {
            return new Refused(HoldRejectionReason.QuoteExpired);
        }

        // The token pins the price for one driver. Another age is another price, so it is another quote.
        if (status != QuoteTokenStatus.Valid || quote is null || quote.DriverAge != request.DriverAge)
        {
            return new Refused(HoldRejectionReason.QuoteInvalid);
        }

        if (!await IsBookableAsync(quote, cancellationToken))
        {
            return new Refused(HoldRejectionReason.NotBookable);
        }

        RentalPeriod period;
        try
        {
            period = RentalPeriod.Create(quote.PickUpAt, quote.DropOffAt);
        }
        catch (ApiErrorException)
        {
            // We signed this token, so its dates were valid. A rule that has tightened since is a quote we no
            // longer stand behind.
            return new Refused(HoldRejectionReason.QuoteInvalid);
        }

        DateTimeOffset now = clock.GetUtcNow();
        return await db.InTransactionAsync(
            async transaction =>
            {
                if (!await HoldInventory.TryHoldAsync(
                    db, transaction, quote.LocationId, quote.CategoryId, period.FirstDay, period.LastDay, period.Days, cancellationToken))
                {
                    return new HoldOutcome(new Refused(HoldRejectionReason.SoldOut));
                }

                var hold = CarHold.Place(
                    request.HoldId, request.TripId, request.ItemId, quote.LocationId, quote.CategoryId, quote.DropOffLocationId,
                    period, quote.Total.Amount, now);
                db.Holds.Add(hold);

                await Announcements.AvailabilityAsync(db, hold.LocationId, hold.CategoryId, hold.FirstDay, hold.LastDay, cancellationToken);
                await db.SaveChangesAsync(cancellationToken);
                return new HoldOutcome(new Placed(hold));
            },
            cancellationToken);
    }

    // The token proves we quoted this. It does not prove the listing is still for sale, or that the driver is
    // still old enough if the partner raised the minimum age since.
    private async Task<bool> IsBookableAsync(CarQuote quote, CancellationToken cancellationToken)
    {
        var pickUp = await db.Locations.AsNoTracking()
            .Where(l => l.Id == quote.LocationId && l.Status == ListingStatus.Published)
            .Select(static l => new { l.PartnerId })
            .FirstOrDefaultAsync(cancellationToken);
        if (pickUp is null)
        {
            return false;
        }

        bool dropOffOk = quote.DropOffLocationId == quote.LocationId
            || await db.Locations.AsNoTracking().AnyAsync(
                l => l.Id == quote.DropOffLocationId && l.Status == ListingStatus.Published && l.PartnerId == pickUp.PartnerId,
                cancellationToken);
        if (!dropOffOk)
        {
            return false;
        }

        return await db.Categories.AsNoTracking().AnyAsync(
            c => c.Id == quote.CategoryId && c.PartnerId == pickUp.PartnerId && c.Status == ListingStatus.Published
                && c.MinimumDriverAge <= quote.DriverAge,
            cancellationToken);
    }
}
