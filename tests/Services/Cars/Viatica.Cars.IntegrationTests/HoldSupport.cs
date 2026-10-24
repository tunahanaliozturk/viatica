using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Relevox;
using Viatica.Cars.Application.Features.Holds;
using Viatica.Cars.Domain;
using Viatica.Contracts;

namespace Viatica.Cars.IntegrationTests;

/// <summary>Drives the hold use cases the way the Kafka consumer does, and reads the numbers back.</summary>
internal static class HoldSupport
{
    public static Task<HoldOutcome> PlaceAsync(
        this CarsHost host, string token, int driverAge = 35, Guid? holdId = null, Guid? tripId = null) =>
        host.AsConsumerAsync(services => services.GetRequiredService<ISender>().Send(
            new PlaceHold(tripId ?? Guid.CreateVersion7(), Guid.CreateVersion7(), holdId ?? Guid.CreateVersion7(), token, driverAge)));

    public static Task<Transition> SendAsync(this CarsHost host, IRequest<Transition> request) =>
        host.AsConsumerAsync(services => services.GetRequiredService<ISender>().Send(request));

    /// <summary>One word for what a placement came to: "placed", the refusal's reason, or "superseded".</summary>
    public static string Describe(this HoldOutcome outcome) => outcome switch
    {
        Placed => "placed",
        Refused refused => refused.Reason.ToString(),
        Superseded => "superseded",
        null => throw new InvalidOperationException("The use case returned a default union."),
    };

    public static CarHold HoldOf(this HoldOutcome outcome) => outcome switch
    {
        Placed placed => placed.Hold,
        Superseded superseded => superseded.Hold,
        _ => throw new InvalidOperationException($"No hold: {outcome.Describe()}."),
    };

    public static Task<List<InventoryDay>> DaysAsync(this CarsHost host, Marketplace m, string from, string to) =>
        host.QueryAsync(db =>
        {
            DateOnly first = DateOnly.Parse(from, System.Globalization.CultureInfo.InvariantCulture);
            DateOnly last = DateOnly.Parse(to, System.Globalization.CultureInfo.InvariantCulture);
            return db.InventoryDays.AsNoTracking()
                .Where(d => d.LocationId == m.LocationId && d.CategoryId == m.CategoryId && d.Date >= first && d.Date <= last)
                .OrderBy(static d => d.Date)
                .ToListAsync();
        });

    public static Task<CarHold> HoldRowAsync(this CarsHost host, Guid holdId) =>
        host.QueryAsync(db => db.Holds.AsNoTracking().SingleAsync(h => h.Id == holdId));

    /// <summary>The invariant the system tests reconcile: nothing is oversold, and sold matches the committed holds.</summary>
    public static async Task AssertInventoryIsConsistentAsync(this CarsHost host, Marketplace m)
    {
        List<InventoryDay> days = await host.QueryAsync(db => db.InventoryDays.AsNoTracking()
            .Where(d => d.LocationId == m.LocationId && d.CategoryId == m.CategoryId).ToListAsync());
        List<CarHold> holds = await host.QueryAsync(db => db.Holds.AsNoTracking()
            .Where(h => h.LocationId == m.LocationId && h.CategoryId == m.CategoryId).ToListAsync());

        foreach (InventoryDay day in days)
        {
            (day.Sold + day.Held).ShouldBeLessThanOrEqualTo(day.Allotment, $"{day.Date} is oversold");

            int covering(HoldStatus status) =>
                holds.Count(h => h.Status == status && h.FirstDay <= day.Date && day.Date <= h.LastDay);

            day.Sold.ShouldBe(covering(HoldStatus.Committed), $"{day.Date}: sold must equal the committed holds covering it");
            day.Held.ShouldBe(covering(HoldStatus.Held), $"{day.Date}: held must equal the live holds covering it");
        }
    }
}
