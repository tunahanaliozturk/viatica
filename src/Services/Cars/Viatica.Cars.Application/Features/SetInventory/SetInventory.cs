using Microsoft.EntityFrameworkCore;
using Moongazing.OrionGuard.Core;
using Moongazing.OrionGuard.DependencyInjection;
using Relevox;
using Viatica.Cars.Domain;
using Viatica.Kernel;

namespace Viatica.Cars.Application.Features.SetInventory;

/// <summary>
/// Sets the allotment and the daily rate of one category at one location for every day from <c>From</c> to
/// <c>To</c> inclusive, in one transaction: all days change or none do.
/// </summary>
public sealed record SetInventory(
    Guid PartnerId, Guid LocationId, Guid CategoryId, DateOnly From, DateOnly To, int Allotment, Money Rate)
    : IRequest<InventoryChange>
{
    public const int MaxDays = 366;

    /// <summary>How far ahead a partner may open inventory, the same horizon stays use.</summary>
    public const int MaxDaysAhead = 365;

    public int Days => To.DayNumber - From.DayNumber + 1;
}

public sealed record InventoryChange(long AvailabilityVersion, int Days);

public sealed class SetInventoryValidator : AbstractValidator<SetInventory>
{
    public SetInventoryValidator(TimeProvider clock)
    {
        ArgumentNullException.ThrowIfNull(clock);

        RuleFor(static c => c.To >= c.From && c.Days <= SetInventory.MaxDays, $"to is on or after from, and the range is at most {SetInventory.MaxDays} days.", nameof(SetInventory.To))
            .WithErrorCode("InvalidDateRange");
        // One day of slack either side of the UTC date, because the partner's local date can differ from it.
        RuleFor(c => c.From >= Today(clock).AddDays(-1), "from is in the past.", nameof(SetInventory.From))
            .WithErrorCode("DateInThePast");
        RuleFor(c => c.To <= Today(clock).AddDays(SetInventory.MaxDaysAhead), $"to is more than {SetInventory.MaxDaysAhead} days ahead.", nameof(SetInventory.To))
            .WithErrorCode("DateTooFarAhead");
        RuleFor(static c => c.Allotment is >= 0 and <= 1000, "allotment is 0 to 1000.", nameof(SetInventory.Allotment))
            .WithErrorCode("InvalidAllotment");
        RuleFor(static c => c.Rate.Currency == Money.Euro && c.Rate.Amount is >= 0 and <= 10_000, "rate is 0 to 10000 EUR.", nameof(SetInventory.Rate))
            .WithErrorCode("InvalidRate");
    }

    private static DateOnly Today(TimeProvider clock) => DateOnly.FromDateTime(clock.GetUtcNow().UtcDateTime);
}

public sealed class SetInventoryHandler(ICarsDb db) : IRequestHandler<SetInventory, InventoryChange>
{
    public async Task<InventoryChange> Handle(SetInventory request, CancellationToken cancellationToken)
    {
        // Ownership first, and both answers are 404: a location or category of another partner does not exist here.
        bool ownsLocation = await db.Locations.AsNoTracking()
            .AnyAsync(l => l.Id == request.LocationId && l.PartnerId == request.PartnerId, cancellationToken);
        if (!ownsLocation)
        {
            throw CarErrors.LocationNotFound();
        }

        bool ownsCategory = await db.Categories.AsNoTracking()
            .AnyAsync(c => c.Id == request.CategoryId && c.PartnerId == request.PartnerId, cancellationToken);
        if (!ownsCategory)
        {
            throw CarErrors.CategoryNotFound();
        }

        return await db.InTransactionAsync(
            async transaction =>
            {
                Dictionary<DateOnly, InventoryDay> existing = await db.InventoryDays
                    .Where(d => d.LocationId == request.LocationId && d.CategoryId == request.CategoryId
                        && d.Date >= request.From && d.Date <= request.To)
                    .ToDictionaryAsync(static d => d.Date, cancellationToken);

                for (DateOnly date = request.From; date <= request.To; date = date.AddDays(1))
                {
                    if (existing.TryGetValue(date, out InventoryDay? day))
                    {
                        day.Set(request.Allotment, request.Rate.Amount);
                    }
                    else
                    {
                        db.InventoryDays.Add(
                            InventoryDay.Open(request.LocationId, request.CategoryId, date, request.Allotment, request.Rate.Amount));
                    }
                }

                // A hold that slips in between the read above and this save is caught by the database: the
                // check constraint sold + held <= allotment fails the UPDATE, and the Api answers 409.
                await db.SaveChangesAsync(cancellationToken);

                long version = await Announcements.AvailabilityAsync(
                    db, request.LocationId, request.CategoryId, request.From, request.To, cancellationToken);
                await db.SaveChangesAsync(cancellationToken);
                return new InventoryChange(version, request.Days);
            },
            cancellationToken);
    }
}
