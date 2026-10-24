using System.Globalization;
using System.Net;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Viatica.Contracts;
using Viatica.Flights.Domain;
using Viatica.Kernel.Pii;
using Viatica.Testing;

namespace Viatica.Flights.IntegrationTests;

/// <summary>An offer that has been through a re-price: the one a trip would hold.</summary>
internal sealed record PricedOffer(string Airline, string OfferId, Price Total, JsonElement Offer, int Adults, int Children, int Infants)
{
    public string FareClass => Offer.GetProperty("fareClass").GetString()!;

    /// <summary>Each flight of the itinerary with its date, which is what the sim's seat buckets are keyed by.</summary>
    public IEnumerable<(string Flight, DateOnly Date)> Flights =>
        Offer.GetProperty("segments").EnumerateArray().Select(static s =>
            (s.GetProperty("flightNumber").GetString()!, DateOnly.FromDateTime(s.GetProperty("departureLocal").GetDateTime())));
}

/// <summary>An order created through the service, with the ids a later request needs.</summary>
internal sealed record PlacedOrder(Guid TripId, Guid ItemId, Guid OrderId, PricedOffer Offer, ProbedMessage Created)
{
    public string BookingReference => Created.DataString("bookingReference")!;
}

/// <summary>
/// The steps every order test takes, written once: find an offer, re-price it, ask Booking-style for an order, and
/// wait for the answer on the topic Booking would read.
/// </summary>
internal sealed class Scenario(FlightsFixture flights)
{
    private static readonly TimeSpan Wait = TimeSpan.FromSeconds(45);

    /// <summary>
    /// Whoever is travelling in the tests that check logs. The names are unusual on purpose, so a match anywhere in a
    /// log or an event can only be this passenger leaking.
    /// </summary>
    public static readonly PassengerDetails Zeynebnur =
        new(PassengerType.Adult, "Zeynebnur", "Karabulutoglu", new DateOnly(1987, 5, 17), "TR8675309X");

    public static PassengerDetails[] OneAdult { get; } = [Zeynebnur];

    public FlightsHost Host => flights.Host;

    public SimHost Sim => flights.Sim;

    public async Task<PricedOffer> PriceAsync(string airline, int adults = 1, int children = 0, int infants = 0, int seatsToLeave = 20)
    {
        (HttpStatusCode status, JsonElement search) = await flights.Client.SearchAsync(Travel.IstanbulToLondon(adults, children, infants));
        status.ShouldBe(HttpStatusCode.Created);

        List<JsonElement> offers = await flights.Client.AllOffersAsync(search);
        JsonElement cheapest = offers.From(airline).First();

        (status, JsonElement priced) = await flights.Client.RepriceAsync(cheapest.GetProperty("id").GetString()!);
        status.ShouldBe(HttpStatusCode.OK);
        JsonElement offer = priced.GetProperty("offer");

        var result = new PricedOffer(
            airline,
            offer.GetProperty("id").GetString()!,
            new Price(offer.GetProperty("total").Amount(), "EUR"),
            offer,
            adults, children, infants);

        // Earlier tests in the collection took seats. Start every order from a flight with room, so a test about a
        // sold out flight is the only one that ever sees one.
        foreach ((string flight, DateOnly date) in result.Flights)
        {
            await Sim.SetSeatsAsync(flight, date, result.FareClass[0], seatsToLeave);
        }

        return result;
    }

    public FlightOrderRequested RequestFor(
        PricedOffer offer, Guid tripId, Guid itemId, IReadOnlyList<PassengerDetails>? passengers = null, Price? expected = null) =>
        new(tripId, itemId, offer.OfferId, expected ?? offer.Total,
            Host.BrokerSealer.SealPassengers(passengers ?? DefaultPassengers(offer), tripId, itemId));

    public static PassengerDetails[] DefaultPassengers(PricedOffer offer) =>
    [
        .. Enumerable.Range(0, offer.Adults).Select(i => new PassengerDetails(PassengerType.Adult, i == 0 ? "Zeynebnur" : $"Adaline{(char)('a' + i)}", "Karabulutoglu", new DateOnly(1987, 5, 17), i == 0 ? "TR8675309X" : null)),
        .. Enumerable.Range(0, offer.Children).Select(i => new PassengerDetails(PassengerType.Child, $"Ceyda{(char)('a' + i)}", "Karabulutoglu", DateOnly.FromDateTime(DateTime.UtcNow).AddYears(-7))),
        .. Enumerable.Range(0, offer.Infants).Select(i => new PassengerDetails(PassengerType.Infant, $"Ilkay{(char)('a' + i)}", "Karabulutoglu", DateOnly.FromDateTime(DateTime.UtcNow).AddMonths(-5))),
    ];

    public Task<ProbedMessage> NextAsync(string type, Guid tripId) =>
        flights.Probe.WaitForAsync(m => m.Type == type && m.DataString("tripId") == tripId.ToString(), Wait);

    public async Task<PlacedOrder> OrderAsync(string airline = "ZC", Guid? tripId = null, int adults = 1)
    {
        PricedOffer offer = await PriceAsync(airline, adults);
        Guid trip = tripId ?? Guid.CreateVersion7();
        Guid item = Guid.CreateVersion7();

        await flights.Booking.SendAsync(RequestFor(offer, trip, item));
        ProbedMessage created = await NextAsync(nameof(FlightOrderCreated), trip);

        return new PlacedOrder(trip, item, Guid.Parse(created.DataString("orderId")!), offer, created);
    }

    public async Task<PlacedOrder> TicketedOrderAsync(string airline = "ZC")
    {
        PlacedOrder order = await OrderAsync(airline);
        await flights.Booking.SendAsync(new FlightTicketingRequested(order.TripId, order.ItemId, order.OrderId));
        await NextAsync(nameof(FlightTicketed), order.TripId);
        return order;
    }

    public Task<int> RowsForAsync(Guid itemId) =>
        flights.Host.InDbAsync(db => db.Orders.CountAsync(o => o.ItemId == itemId));

    public Task<FlightOrder> RowAsync(Guid orderId) =>
        flights.Host.InDbAsync(db => db.Orders.AsNoTracking().SingleAsync(o => o.Id == orderId));

    public static string Key(Guid itemId) => itemId.ToString("D", CultureInfo.InvariantCulture);

    public int OrderPosts(string airline) => Host.Wire.Count(airline, HttpMethod.Post, "/orders");

    public int OrderLookups(string airline) => Host.Wire.Count(airline, HttpMethod.Get, "/orders");

    public int Calls(string airline, string action) => Host.Wire.Count(airline, HttpMethod.Post, "/" + action);
}
