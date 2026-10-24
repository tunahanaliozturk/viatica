using Microsoft.EntityFrameworkCore;
using Relevox;
using Viatica.Cars.Application.Features.SetInventory;
using Viatica.Cars.Domain;
using Viatica.Kernel;

namespace Viatica.Cars.Application.Features.GetInventory;

/// <summary>The partner's calendar for one category at one location: what exists, what is sold and held, the rate.</summary>
public sealed record GetInventory(Guid PartnerId, Guid LocationId, Guid CategoryId, DateOnly From, DateOnly To)
    : IRequest<IReadOnlyList<InventoryDayView>>
{
    public int Days => To.DayNumber - From.DayNumber + 1;
}

public sealed record InventoryDayView(DateOnly Date, int Allotment, int Sold, int Held, int Available, Money Rate);

public sealed class GetInventoryHandler(ICarsDb db) : IRequestHandler<GetInventory, IReadOnlyList<InventoryDayView>>
{
    public async Task<IReadOnlyList<InventoryDayView>> Handle(GetInventory request, CancellationToken cancellationToken)
    {
        if (request.To < request.From || request.Days > SetInventory.SetInventory.MaxDays)
        {
            throw ApiErrorException.Invalid(
                "InvalidDateRange", $"to is on or after from, and the range is at most {SetInventory.SetInventory.MaxDays} days.", "to");
        }

        bool owns = await db.Locations.AsNoTracking()
            .AnyAsync(l => l.Id == request.LocationId && l.PartnerId == request.PartnerId, cancellationToken);
        if (!owns)
        {
            throw CarErrors.LocationNotFound();
        }

        var rows = await db.InventoryDays.AsNoTracking()
            .Where(d => d.LocationId == request.LocationId && d.CategoryId == request.CategoryId
                && d.Date >= request.From && d.Date <= request.To)
            .OrderBy(static d => d.Date)
            .Select(static d => new { d.Date, d.Allotment, d.Sold, d.Held, d.Rate })
            .ToListAsync(cancellationToken);

        return [.. rows.Select(static d => new InventoryDayView(d.Date, d.Allotment, d.Sold, d.Held, d.Allotment - d.Sold - d.Held, Money.Eur(d.Rate)))];
    }
}
