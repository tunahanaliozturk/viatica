using System.Net;
using Microsoft.EntityFrameworkCore;
using Viatica.AirlineSim.Api;
using Viatica.AirlineSim.Orders;

namespace Viatica.AirlineSim.IntegrationTests;

public sealed class BookingReferenceTests(Postgres postgres)
{
    [Fact]
    public async Task A_booking_reference_already_in_use_is_replaced_with_a_fresh_one()
    {
        var references = new ScriptedReferences("AAAAAA", "AAAAAA", "BBBBBB");
        await using SimHost sim = await SimHost.StartAsync(postgres, references: references);
        OfferView offer = await sim.Client.OfferAsync("ZC", "IST", "LHR", 'K');
        string flight = offer.Segments[0].FlightNumber;
        int capacity = new Viatica.AirlineSim.Network.RouteNetwork(SimHost.DefaultSeed).Find(flight)!.Bucket('K')!.Seats;

        OrderView first = await sim.Client.BookAsync("ZC", offer);
        using HttpResponseMessage second = await sim.Client.CreateOrderAsync("ZC", offer.OfferId, "second");

        first.BookingReference.ShouldBe("AAAAAA");
        second.StatusCode.ShouldBe(HttpStatusCode.Created);
        (await second.ReadAsync(SimJson.Default.OrderView)).BookingReference.ShouldBe("BBBBBB");
        (await sim.InDbAsync(db => db.Orders.CountAsync())).ShouldBe(2);

        // The failed attempt took its seat inside the transaction that rolled back, so nothing leaked.
        (await sim.SeatsLeftAsync(flight, SimHost.Outbound, 'K')).ShouldBe(capacity - 2);
    }

    [Fact]
    public async Task A_retry_after_a_collision_still_replays_the_order_it_made()
    {
        var references = new ScriptedReferences("AAAAAA", "AAAAAA", "BBBBBB");
        await using SimHost sim = await SimHost.StartAsync(postgres, references: references);
        OfferView offer = await sim.Client.OfferAsync("ZC", "IST", "LHR", 'K');
        await sim.Client.BookAsync("ZC", offer);

        using HttpResponseMessage created = await sim.Client.CreateOrderAsync("ZC", offer.OfferId, "retry-me");
        using HttpResponseMessage replayed = await sim.Client.CreateOrderAsync("ZC", offer.OfferId, "retry-me");

        created.StatusCode.ShouldBe(HttpStatusCode.Created);
        replayed.StatusCode.ShouldBe(HttpStatusCode.OK);
        (await replayed.ReadAsync(SimJson.Default.OrderView)).BookingReference.ShouldBe("BBBBBB");
        (await sim.InDbAsync(db => db.Orders.CountAsync())).ShouldBe(2);
    }

    [Fact]
    public async Task A_generator_that_keeps_colliding_is_given_up_on_after_a_few_draws()
    {
        var references = new ScriptedReferences("AAAAAA");
        await using SimHost sim = await SimHost.StartAsync(postgres, references: references);
        OfferView offer = await sim.Client.OfferAsync("ZC", "IST", "LHR", 'K');
        await sim.Client.BookAsync("ZC", offer);
        int drawn = references.Drawn;

        await Should.ThrowAsync<Exception>(sim.Client.CreateOrderAsync("ZC", offer.OfferId, "doomed"));

        (references.Drawn - drawn).ShouldBe(5);
        (await sim.InDbAsync(db => db.Orders.CountAsync())).ShouldBe(1);
    }

    /// <summary>Hands out the given references in order and repeats the last one when it runs out.</summary>
    private sealed class ScriptedReferences(params string[] script) : IBookingReferences
    {
        private int _drawn;

        public int Drawn => Volatile.Read(ref _drawn);

        public string Next() => script[Math.Min(Interlocked.Increment(ref _drawn) - 1, script.Length - 1)];
    }
}
