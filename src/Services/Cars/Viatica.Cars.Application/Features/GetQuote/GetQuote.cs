using Microsoft.EntityFrameworkCore;
using Moongazing.OrionGuard.Core;
using Moongazing.OrionGuard.DependencyInjection;
using Relevox;
using Viatica.Cars.Application.Quotes;
using Viatica.Cars.Domain;
using Viatica.Kernel;

namespace Viatica.Cars.Application.Features.GetQuote;

/// <summary>
/// Prices a rental and returns a token that pins the price for 15 minutes. Open to anyone: a trip can be built
/// before the traveller signs in.
/// </summary>
public sealed record GetQuote(
    Guid LocationId,
    Guid CategoryId,
    Guid? DropOffLocationId,
    DateTimeOffset PickUpAt,
    DateTimeOffset DropOffAt,
    int DriverAge) : IRequest<QuoteView>;

public sealed record QuoteView(
    string QuoteToken,
    DateTimeOffset ExpiresAt,
    int RentalDays,
    IReadOnlyList<DailyRate> Days,
    Money Rental,
    Money OneWayFee,
    Money YoungDriverSurcharge,
    Money Total);

public sealed class GetQuoteValidator : AbstractValidator<GetQuote>
{
    public GetQuoteValidator()
    {
        RuleFor(static q => q.LocationId != Guid.Empty, "locationId is required.", nameof(GetQuote.LocationId)).WithErrorCode("InvalidLocationId");
        RuleFor(static q => q.CategoryId != Guid.Empty, "categoryId is required.", nameof(GetQuote.CategoryId)).WithErrorCode("InvalidCategoryId");
        RuleFor(static q => q.DriverAge is >= 18 and <= 99, "driverAge is 18 to 99.", nameof(GetQuote.DriverAge)).WithErrorCode("InvalidDriverAge");
    }
}

public sealed class GetQuoteHandler(ICarsDb db, QuoteTokenCodec tokens, TimeProvider clock) : IRequestHandler<GetQuote, QuoteView>
{
    public async Task<QuoteView> Handle(GetQuote request, CancellationToken cancellationToken)
    {
        RentalPeriod period = RentalPeriod.Create(request.PickUpAt, request.DropOffAt);
        if (request.PickUpAt < clock.GetUtcNow())
        {
            throw CarErrors.PickUpInThePast();
        }

        // Unpublished and unknown look the same to a traveller, and a partner's draft is not theirs to price.
        RentalLocation pickUp = await FindPublishedLocationAsync(request.LocationId, cancellationToken);
        Guid dropOffId = request.DropOffLocationId ?? request.LocationId;
        RentalLocation dropOff = dropOffId == pickUp.Id ? pickUp : await FindPublishedLocationAsync(dropOffId, cancellationToken);

        CarCategory category = await db.Categories.AsNoTracking()
            .FirstOrDefaultAsync(
                c => c.Id == request.CategoryId && c.PartnerId == pickUp.PartnerId && c.Status == ListingStatus.Published,
                cancellationToken)
            ?? throw CarErrors.CategoryNotFound();

        if (dropOff.PartnerId != pickUp.PartnerId)
        {
            throw CarErrors.OneWayNotAllowed();
        }

        if (!pickUp.IsOpenAt(request.PickUpAt))
        {
            throw CarErrors.LocationClosed("pickUpAt");
        }

        if (!dropOff.IsOpenAt(request.DropOffAt))
        {
            throw CarErrors.LocationClosed("dropOffAt");
        }

        if (request.DriverAge < category.MinimumDriverAge)
        {
            throw CarErrors.DriverTooYoung(category.MinimumDriverAge);
        }

        // Inventory is counted at the pick-up location on each day the car is out.
        var rows = await db.InventoryDays.AsNoTracking()
            .Where(d => d.LocationId == pickUp.Id && d.CategoryId == category.Id
                && d.Date >= period.FirstDay && d.Date <= period.LastDay)
            .OrderBy(static d => d.Date)
            .Select(static d => new { d.Date, d.Rate, Available = d.Allotment - d.Sold - d.Held })
            .ToListAsync(cancellationToken);

        if (rows.Count != period.Days || rows.Any(static r => r.Available < 1))
        {
            throw CarErrors.SoldOut();
        }

        Domain.PartnerSettings settings = await db.PartnerSettings.AsNoTracking()
            .FirstOrDefaultAsync(s => s.PartnerId == pickUp.PartnerId, cancellationToken)
            ?? Domain.PartnerSettings.Defaults(pickUp.PartnerId);

        PriceBreakdown price = Pricing.Quote(
            period,
            [.. rows.Select(static r => new DailyRate(r.Date, Money.Eur(r.Rate)))],
            oneWay: dropOff.Id != pickUp.Id,
            request.DriverAge,
            category,
            settings);

        (string token, CarQuote quote) = tokens.Issue(
            pickUp.Id, category.Id, dropOff.Id, request.PickUpAt, request.DropOffAt, request.DriverAge, price.Total);

        CarsTelemetry.QuotesIssued.Add(1);
        return new QuoteView(
            token, quote.ExpiresAt, period.Days, price.Days, price.Rental, price.OneWayFee, price.YoungDriverSurcharge, price.Total);
    }

    private async Task<RentalLocation> FindPublishedLocationAsync(Guid id, CancellationToken cancellationToken) =>
        await db.Locations.AsNoTracking()
            .FirstOrDefaultAsync(l => l.Id == id && l.Status == ListingStatus.Published, cancellationToken)
        ?? throw CarErrors.LocationNotFound();
}
