using Viatica.Contracts;
using Viatica.Testing;

namespace Viatica.Flights.IntegrationTests;

/// <summary>
/// The case the requirements single out: the airline times out on order creation and may have created the order
/// anyway. Whatever it did, the answer must match the airline's books. These run on a host whose airline timeout is
/// one second, against ZJ (Limbo Airlines), whose timeout knobs are exactly this.
/// </summary>
public sealed class TimedOutCreationTests(ImpatientFlights flights) : IClassFixture<ImpatientFlights>, IAsyncLifetime
{
    private readonly Scenario scenario = new(flights);

    public async ValueTask InitializeAsync() => await flights.Sim.QuietAsync();

    public ValueTask DisposeAsync() => ValueTask.CompletedTask;

    [Fact]
    public async Task A_creation_that_timed_out_and_was_made_anyway_ends_with_exactly_one_order_and_FlightOrderCreated()
    {
        PricedOffer offer = await scenario.PriceAsync("ZJ");
        Guid trip = Guid.CreateVersion7();
        Guid item = Guid.CreateVersion7();
        await flights.Sim.SetKnobsAsync("ZJ", new { OrderTimeoutRate = 1.0, CreateAnywayRate = 1.0, TimeoutHangMs = 5_000 });
        int ordersBefore = await flights.Sim.CountOrdersAsync("ZJ");
        int postsBefore = scenario.OrderPosts("ZJ");
        int lookupsBefore = scenario.OrderLookups("ZJ");

        await flights.Booking.SendAsync(scenario.RequestFor(offer, trip, item));
        ProbedMessage created = await scenario.NextAsync(nameof(FlightOrderCreated), trip);

        // One order at the airline, found by our key; one row here; one create call and one lookup on the wire.
        (await flights.Sim.CountOrdersAsync("ZJ")).ShouldBe(ordersBefore + 1);
        (await flights.Sim.FindOrderAsync("ZJ", Scenario.Key(item)))!.Value.GetProperty("BookingReference").GetString()
            .ShouldBe(created.DataString("bookingReference"));
        (await scenario.RowsForAsync(item)).ShouldBe(1);
        scenario.OrderPosts("ZJ").ShouldBe(postsBefore + 1);
        scenario.OrderLookups("ZJ").ShouldBe(lookupsBefore + 1);
        flights.Probe.Received.ShouldNotContain(m => m.Type == nameof(FlightOrderRejected) && m.DataString("tripId") == trip.ToString());
    }

    [Fact]
    public async Task A_creation_that_timed_out_and_was_not_made_is_rejected_with_nothing_left_at_the_airline()
    {
        PricedOffer offer = await scenario.PriceAsync("ZJ");
        Guid trip = Guid.CreateVersion7();
        Guid item = Guid.CreateVersion7();
        await flights.Sim.SetKnobsAsync("ZJ", new { OrderTimeoutRate = 1.0, CreateAnywayRate = 0.0, TimeoutHangMs = 5_000 });
        int ordersBefore = await flights.Sim.CountOrdersAsync("ZJ");
        (string flight, DateOnly date) = offer.Flights.First();
        int? seats = await flights.Sim.SeatsLeftAsync(flight, date, offer.FareClass[0]);
        int postsBefore = scenario.OrderPosts("ZJ");
        int lookupsBefore = scenario.OrderLookups("ZJ");

        await flights.Booking.SendAsync(scenario.RequestFor(offer, trip, item));
        ProbedMessage rejected = await scenario.NextAsync(nameof(FlightOrderRejected), trip);

        rejected.DataString("reason").ShouldBe("SupplierUnavailable");
        (await flights.Sim.CountOrdersAsync("ZJ")).ShouldBe(ordersBefore);
        (await flights.Sim.FindOrderAsync("ZJ", Scenario.Key(item))).ShouldBeNull();
        (await flights.Sim.SeatsLeftAsync(flight, date, offer.FareClass[0])).ShouldBe(seats);
        (await scenario.RowsForAsync(item)).ShouldBe(0);
        scenario.OrderPosts("ZJ").ShouldBe(postsBefore + 1, "a creation is never retried blindly");
        scenario.OrderLookups("ZJ").ShouldBe(lookupsBefore + 1);
    }

    [Fact]
    public async Task When_the_lookup_cannot_be_answered_either_nothing_is_decided_and_the_request_is_tried_again()
    {
        PricedOffer offer = await scenario.PriceAsync("ZJ");
        Guid trip = Guid.CreateVersion7();
        Guid item = Guid.CreateVersion7();
        int ordersBefore = await flights.Sim.CountOrdersAsync("ZJ");
        int lookupsBefore = scenario.OrderLookups("ZJ");

        // Every call to the airline now takes longer than the timeout, the lookup included. Whether the order exists
        // is unknowable from here, which is the point.
        await flights.Sim.SetKnobsAsync("ZJ", new { LatencyMedianMs = 3_000, OrderTimeoutRate = 1.0, CreateAnywayRate = 1.0, TimeoutHangMs = 5_000 });
        await flights.Booking.SendAsync(scenario.RequestFor(offer, trip, item));
        await Eventually.MatchesAsync(() => Task.FromResult(scenario.OrderLookups("ZJ")), lookups => lookups > lookupsBefore, TimeSpan.FromSeconds(30));

        // The lookup failed. A guess here would reject an order that exists, or accept one that does not.
        flights.Probe.Received.ShouldNotContain(m => m.DataString("tripId") == trip.ToString());

        // The airline recovers; the redelivery repeats the creation with the same key and gets the order that is there.
        await flights.Sim.SetKnobsAsync("ZJ", new { LatencyMedianMs = 20, OrderTimeoutRate = 0.0, CreateAnywayRate = 0.0 });
        ProbedMessage created = await scenario.NextAsync(nameof(FlightOrderCreated), trip);

        (await flights.Sim.CountOrdersAsync("ZJ")).ShouldBe(ordersBefore + 1);
        (await flights.Sim.FindOrderAsync("ZJ", Scenario.Key(item)))!.Value.GetProperty("BookingReference").GetString()
            .ShouldBe(created.DataString("bookingReference"));
        (await scenario.RowsForAsync(item)).ShouldBe(1);
    }
}
