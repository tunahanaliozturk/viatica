using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage;
using Moongazing.OrionGuard.DependencyInjection;
using Relevox;
using Viatica.Kernel;
using Viatica.Stays.Application.Features.Properties;
using Viatica.Stays.Application.Inventory;
using Viatica.Stays.Domain;

namespace Viatica.Stays.Application.Features.Nights;

/// <summary>
/// Sets the allotment, and the rate, of one room type for every night from <see cref="From"/> to
/// <see cref="To"/> inclusive: the partner's calendar edit, as one request and one transaction. A night that has no
/// row yet is opened; one that has keeps its sold and held counts.
/// </summary>
public sealed record SetNights(Guid PropertyId, Guid RoomTypeId, Caller Caller, DateOnly From, DateOnly To, int Allotment, Money? Rate)
    : IRequest<NightsSet>;

/// <param name="Nights">How many nights the request covered.</param>
/// <param name="AvailabilityVersion">The version published with the change.</param>
public sealed record NightsSet(int Nights, long AvailabilityVersion);

public sealed class SetNightsValidator : AbstractValidator<SetNights>
{
    public SetNightsValidator(TimeProvider clock)
    {
        RuleFor(static r => r.From <= r.To, "to must not be before from.", nameof(SetNights.To))
            .WithErrorCode(StayErrorCode.InvalidDateRange);

        // Today in UTC, less a day: a partner east of Greenwich is already on the next date.
        RuleFor(r => r.From >= StayDates.EarliestCheckIn(clock.GetUtcNow()), "from is in the past.", nameof(SetNights.From))
            .WithErrorCode(StayErrorCode.OutsideRateWindow);
        RuleFor(
            r => r.To <= StayDates.LatestRateDate(clock.GetUtcNow()),
            $"to is more than {StayDates.RateWindowDays} days ahead.",
            nameof(SetNights.To))
            .WithErrorCode(StayErrorCode.OutsideRateWindow);

        RuleFor(static r => r.Allotment is >= 0 and <= 10_000, "allotment must be between 0 and 10000.", nameof(SetNights.Allotment));
        RuleFor(
            static r => r.Rate is null || (r.Rate.Currency == Money.Euro && r.Rate.Amount is > 0 and <= 100_000),
            "rate must be a positive amount in EUR.",
            nameof(SetNights.Rate));
    }
}

public sealed class SetNightsHandler(IStaysDb db, NightLedger ledger) : IRequestHandler<SetNights, NightsSet>
{
    public async Task<NightsSet> Handle(SetNights request, CancellationToken cancellationToken)
    {
        StayProperty property = await db.Properties.AsNoTracking()
            .FirstOrDefaultAsync(p => p.Id == request.PropertyId, cancellationToken)
            ?? throw StayErrors.PropertyNotFound();
        if (!request.Caller.Owns(property))
        {
            throw StayErrors.PropertyNotFound();
        }

        RoomType roomType = await db.RoomTypes.AsNoTracking()
            .FirstOrDefaultAsync(r => r.Id == request.RoomTypeId && r.PropertyId == property.Id, cancellationToken)
            ?? throw StayErrors.RoomTypeNotFound();

        decimal rate = request.Rate?.Amount ?? roomType.BaseRate;
        int allotment = request.Allotment;
        DateOnly from = request.From;
        DateOnly to = request.To;
        int nights = to.DayNumber - from.DayNumber + 1;

        // A caller that disposes the transaction without committing rolls everything back, which is how a refused
        // range leaves no night changed.
        await using IDbContextTransaction transaction = await db.Database.BeginTransactionAsync(cancellationToken);

        List<DateOnly> existing = await db.RoomNights.AsNoTracking()
            .Where(n => n.RoomTypeId == roomType.Id && n.Night >= from && n.Night <= to)
            .Select(static n => n.Night)
            .ToListAsync(cancellationToken);

        HashSet<DateOnly> known = [.. existing];
        for (DateOnly night = from; night <= to; night = night.AddDays(1))
        {
            if (!known.Contains(night))
            {
                db.RoomNights.Add(RoomNight.Open(roomType.Id, night, allotment, rate));
            }
        }

        await db.SaveChangesAsync(cancellationToken);

        // The condition keeps a night from being cut below what is already sold or held. Counting the rows that
        // passed it is how a refusal is noticed: one short night fails the whole range, not just that night.
        Guid roomTypeId = roomType.Id;
        int updated = await db.RoomNights
            .Where(n => n.RoomTypeId == roomTypeId && n.Night >= from && n.Night <= to && n.Sold + n.Held <= allotment)
            .ExecuteUpdateAsync(s => s.SetProperty(n => n.Allotment, allotment).SetProperty(n => n.Rate, rate), cancellationToken);
        if (updated != nights)
        {
            throw StayErrors.AllotmentBelowBooked();
        }

        long version = await ledger.PublishAvailabilityAsync(property.Id, roomTypeId, from, to, cancellationToken);
        await db.SaveChangesAsync(cancellationToken);
        await transaction.CommitAsync(cancellationToken);
        return new NightsSet(nights, version);
    }
}
