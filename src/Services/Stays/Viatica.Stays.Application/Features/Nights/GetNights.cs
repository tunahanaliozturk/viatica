using Microsoft.EntityFrameworkCore;
using Moongazing.OrionGuard.DependencyInjection;
using Relevox;
using Viatica.Kernel;
using Viatica.Stays.Domain;

namespace Viatica.Stays.Application.Features.Nights;

/// <summary>
/// The partner's calendar for one room type: allotment, sold, held and rate per night. Bounded by the range
/// (at most a year and a bit), so it is one response and not a paged collection.
/// </summary>
public sealed record GetNights(Guid PropertyId, Guid RoomTypeId, Caller Caller, DateOnly From, DateOnly To)
    : IRequest<IReadOnlyList<NightView>>;

public sealed record NightView(DateOnly Date, int Allotment, int Sold, int Held, int Available, Money Rate);

public sealed class GetNightsValidator : AbstractValidator<GetNights>
{
    public GetNightsValidator()
    {
        RuleFor(static r => r.From <= r.To, "to must not be before from.", nameof(GetNights.To))
            .WithErrorCode(StayErrorCode.InvalidDateRange);
        RuleFor(
            static r => r.To.DayNumber - r.From.DayNumber < StayDates.RateWindowDays + 2,
            $"the range is at most {StayDates.RateWindowDays + 2} nights.",
            nameof(GetNights.To))
            .WithErrorCode(StayErrorCode.InvalidDateRange);
    }
}

public sealed class GetNightsHandler(IStaysDb db) : IRequestHandler<GetNights, IReadOnlyList<NightView>>
{
    public async Task<IReadOnlyList<NightView>> Handle(GetNights request, CancellationToken cancellationToken)
    {
        StayProperty property = await db.Properties.AsNoTracking()
            .FirstOrDefaultAsync(p => p.Id == request.PropertyId, cancellationToken)
            ?? throw StayErrors.PropertyNotFound();
        if (!request.Caller.Owns(property))
        {
            throw StayErrors.PropertyNotFound();
        }

        if (!await db.RoomTypes.AnyAsync(r => r.Id == request.RoomTypeId && r.PropertyId == property.Id, cancellationToken))
        {
            throw StayErrors.RoomTypeNotFound();
        }

        Guid roomTypeId = request.RoomTypeId;
        DateOnly from = request.From;
        DateOnly to = request.To;

        var nights = await db.RoomNights.AsNoTracking()
            .Where(n => n.RoomTypeId == roomTypeId && n.Night >= from && n.Night <= to)
            .OrderBy(static n => n.Night)
            .Select(static n => new { n.Night, n.Allotment, n.Sold, n.Held, n.Rate })
            .ToListAsync(cancellationToken);

        return [.. nights.Select(static n => new NightView(n.Night, n.Allotment, n.Sold, n.Held, n.Allotment - n.Sold - n.Held, Money.Eur(n.Rate)))];
    }
}
