using System.Globalization;
using System.Net;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Viatica.Contracts;
using Viatica.Stays.Application.Features.Properties;
using Viatica.Stays.Application.Features.RoomTypes;
using Viatica.Testing;

namespace Viatica.Stays.IntegrationTests;

/// <summary>
/// A property with one room type and a stretch of nights on sale, built through the API like a partner would. The
/// tests that follow work on their own scenario, so none depends on another's rows.
/// </summary>
internal sealed class Scenario
{
    public required StaysHost Host { get; init; }

    public required Guid PartnerId { get; init; }

    public required string PartnerToken { get; init; }

    public required Guid PropertyId { get; init; }

    public required Guid RoomTypeId { get; init; }

    public decimal Rate { get; init; }

    public string PropertyPath => $"/properties/{PropertyId}";

    public string RoomPath => $"{PropertyPath}/room-types/{RoomTypeId}";

    /// <summary>A property with one double room, 3 nights from 1 Dec, <paramref name="allotment"/> rooms each at <paramref name="rate"/>.</summary>
    public static async Task<Scenario> CreateAsync(
        StaysHost host, int allotment = 10, decimal rate = 120m, bool publish = true, int maxOccupancy = 2)
    {
        Guid partner = Guid.CreateVersion7();
        string token = StaysHost.PartnerToken(partner);
        Guid propertyId = Guid.CreateVersion7();
        Guid roomTypeId = Guid.CreateVersion7();

        (await host.SendAsync(HttpMethod.Put, $"/properties/{propertyId}", PropertyBody(), token)).StatusCode.ShouldBe(HttpStatusCode.Created);
        (await host.SendAsync(HttpMethod.Put, $"/properties/{propertyId}/room-types/{roomTypeId}", RoomBody(maxOccupancy, rate), token))
            .StatusCode.ShouldBe(HttpStatusCode.Created);

        var scenario = new Scenario { Host = host, PartnerId = partner, PartnerToken = token, PropertyId = propertyId, RoomTypeId = roomTypeId, Rate = rate };
        await scenario.SetNightsAsync(StaysHost.CheckIn.AddDays(-1), StaysHost.CheckOut.AddDays(2), allotment, rate);

        if (publish)
        {
            await scenario.PublishAsync();
        }

        return scenario;
    }

    public static object PropertyBody(string name = "Casa do Mar") => new
    {
        name,
        description = "Rooms above the harbour.",
        address = new { line1 = "Rua das Flores 12", city = "Lisbon", region = "Lisboa", postalCode = "1200-195", countryCode = "PT" },
        location = new { latitude = 38.71, longitude = -9.14 },
        starRating = 4,
        amenities = new[] { "wifi", "pool" },
        checkInFrom = "15:00:00",
        checkOutUntil = "11:00:00",
        cancellationPolicy = new { freeUntilHoursBeforeStart = 48, penaltyAfter = "FirstUnit" },
    };

    public static object RoomBody(int maxOccupancy = 2, decimal rate = 120m, string name = "Double") => new
    {
        name,
        maxOccupancy,
        beds = "1 double",
        amenities = new[] { "balcony" },
        baseRate = Money(rate),
    };

    public static object Money(decimal amount) => new { amount = amount.ToString("F2", CultureInfo.InvariantCulture), currency = "EUR" };

    public async Task<HttpResponseMessage> SetNightsAsync(DateOnly from, DateOnly to, int allotment, decimal? rate = null, Dictionary<string, string>? headers = null) =>
        await Host.SendAsync(
            HttpMethod.Post,
            $"{RoomPath}/inventory:set",
            rate is { } r ? new { from, to, allotment, rate = Money(r) } : new { from, to, allotment },
            PartnerToken,
            headers: headers);

    public async Task<string> PublishAsync()
    {
        HttpResponseMessage current = await Host.GetAsync(PropertyPath, PartnerToken);
        using HttpResponseMessage published = await Host.SendAsync(HttpMethod.Post, $"{PropertyPath}/publish", token: PartnerToken, ifMatch: StaysHost.ETagOf(current));
        published.StatusCode.ShouldBe(HttpStatusCode.OK);
        return StaysHost.ETagOf(published);
    }

    public async Task<Application.Features.Quotes.Quote> QuoteAsync(int quantity = 1, int guests = 2, DateOnly? checkIn = null, DateOnly? checkOut = null)
    {
        using HttpResponseMessage response = await Host.SendAsync(
            HttpMethod.Post,
            $"{RoomPath}/quotes",
            new { checkIn = checkIn ?? StaysHost.CheckIn, checkOut = checkOut ?? StaysHost.CheckOut, quantity, guests });
        response.StatusCode.ShouldBe(HttpStatusCode.OK, await response.Content.ReadAsStringAsync());
        return await StaysHost.ReadAsync<Application.Features.Quotes.Quote>(response);
    }

    public static StayHoldRequested HoldRequest(string quoteToken, int quantity = 1, Guid? holdId = null) =>
        new(Guid.CreateVersion7(), Guid.CreateVersion7(), holdId ?? Guid.CreateVersion7(), quoteToken, quantity);

    /// <summary>The nights of the room type as the database holds them.</summary>
    public Task<List<Night>> NightsAsync() =>
        Host.InDbAsync(async db =>
        {
            Guid roomTypeId = RoomTypeId;
            var rows = await db.RoomNights.AsNoTracking().Where(n => n.RoomTypeId == roomTypeId).OrderBy(n => n.Night).ToListAsync();
            return rows.Select(static n => new Night(n.Night, n.Allotment, n.Sold, n.Held, n.Rate)).ToList();
        });

    /// <summary>The room type's availability counter: how many availability changes have been announced.</summary>
    public Task<long> AvailabilityVersionAsync() =>
        Host.InDbAsync(db =>
        {
            Guid roomTypeId = RoomTypeId;
            return db.Inventories.AsNoTracking().Where(i => i.RoomTypeId == roomTypeId).Select(static i => i.AvailabilityVersion).SingleAsync();
        });

    public async Task<PropertyView> GetPropertyAsync()
    {
        using HttpResponseMessage response = await Host.GetAsync(PropertyPath, PartnerToken);
        return await StaysHost.ReadAsync<PropertyView>(response);
    }

    public async Task<RoomTypeView> GetRoomAsync()
    {
        using HttpResponseMessage response = await Host.GetAsync(RoomPath, PartnerToken);
        return await StaysHost.ReadAsync<RoomTypeView>(response);
    }

    /// <summary>All the messages of one type about this property, in the order they were read from the topic.</summary>
    public Task<IReadOnlyList<ProbedMessage>> EventsAsync(EventProbe probe, string type, int count) =>
        probe.WaitForCountAsync(count, m => m.Type == type && m.DataString("propertyId") == PropertyId.ToString());

    public static long Long(ProbedMessage message, string property) =>
        message.Data.GetProperty(property).GetInt64();

    public static JsonElement Nights(ProbedMessage message) => message.Data.GetProperty("nights");
}

internal sealed record Night(DateOnly Date, int Allotment, int Sold, int Held, decimal Rate)
{
    public int Available => Allotment - Sold - Held;
}
