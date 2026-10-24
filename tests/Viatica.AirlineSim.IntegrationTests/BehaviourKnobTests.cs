using System.Diagnostics;
using System.Net;
using Viatica.AirlineSim.Api;
using Viatica.AirlineSim.Orders;

namespace Viatica.AirlineSim.IntegrationTests;

/// <summary>Each knob, turned through the admin API, changes what a caller of the airline API sees.</summary>
public sealed class BehaviourKnobTests(Postgres postgres)
{
    [Fact]
    public async Task The_slow_airline_is_slow_and_the_fast_one_is_not()
    {
        await using SimHost sim = await SimHost.StartAsync(postgres, latency: true);

        double slow = await MedianSearchSecondsAsync(sim, "YZ");
        double fast = await MedianSearchSecondsAsync(sim, "ZC");

        // Default p50 is 1.5 s; the median of nine draws lands well inside this band.
        slow.ShouldBeInRange(0.9, 2.6);
        fast.ShouldBeLessThan(0.4);
    }

    [Fact]
    public async Task Latency_follows_the_knob()
    {
        await using SimHost sim = await SimHost.StartAsync(postgres);
        (await sim.Client.PatchKnobsAsync("ZC", new { LatencyMedianMs = 400, LatencySigma = 0 })).EnsureSuccessStatusCode();

        Stopwatch watch = Stopwatch.StartNew();
        using HttpResponseMessage response = await sim.Client.SearchAsync("ZC", "IST", "LHR");

        response.StatusCode.ShouldBe(HttpStatusCode.OK);
        watch.Elapsed.ShouldBeGreaterThanOrEqualTo(TimeSpan.FromMilliseconds(400));
    }

    [Fact]
    public async Task An_outage_answers_503_until_it_is_switched_off()
    {
        await using SimHost sim = await SimHost.StartAsync(postgres);

        (await sim.Client.PatchKnobsAsync("ZC", new { Outage = true })).EnsureSuccessStatusCode();
        using HttpResponseMessage down = await sim.Client.SearchAsync("ZC", "IST", "LHR");
        using HttpResponseMessage otherAirline = await sim.Client.SearchAsync("JG", "IST", "LHR");
        (await sim.Client.PatchKnobsAsync("ZC", new { Outage = false })).EnsureSuccessStatusCode();
        using HttpResponseMessage up = await sim.Client.SearchAsync("ZC", "IST", "LHR");

        down.StatusCode.ShouldBe(HttpStatusCode.ServiceUnavailable);
        (await down.ErrorCodeAsync()).ShouldBe("SERVICE_UNAVAILABLE");
        otherAirline.StatusCode.ShouldBe(HttpStatusCode.OK);
        up.StatusCode.ShouldBe(HttpStatusCode.OK);
    }

    [Fact]
    public async Task The_refusal_rate_holds_over_many_orders_and_a_refusal_sticks()
    {
        const int Orders = 300;
        await using SimHost sim = await SimHost.StartAsync(postgres);
        (await sim.Client.PatchKnobsAsync("JG", new { TicketingRefusalRate = 0.3 })).EnsureSuccessStatusCode();
        OfferView offer = await sim.Client.OfferAsync("JG", "MAD", "LHR", 'K');
        await sim.SetSeatsAsync(offer.Segments[0].FlightNumber, SimHost.Outbound, 'K', Orders);

        var refused = new List<string>();
        for (int i = 0; i < Orders; i++)
        {
            OrderView order = await sim.Client.BookAsync("JG", offer);
            using HttpResponseMessage ticket = await sim.Client.OrderActionAsync("JG", order.BookingReference, "ticket");
            if (ticket.StatusCode == HttpStatusCode.UnprocessableEntity)
            {
                (await ticket.ErrorCodeAsync()).ShouldBe("TICKETING_REFUSED");
                refused.Add(order.BookingReference);
            }
            else
            {
                ticket.StatusCode.ShouldBe(HttpStatusCode.OK);
            }
        }

        // Three standard deviations of a binomial with n = 300 and p = 0.3 is about 0.08.
        (refused.Count / (double)Orders).ShouldBeInRange(0.22, 0.38);

        using HttpResponseMessage retry = await sim.Client.OrderActionAsync("JG", refused[0], "ticket");
        retry.StatusCode.ShouldBe(HttpStatusCode.UnprocessableEntity);
    }

    [Fact]
    public async Task A_timed_out_creation_that_created_the_order_is_found_by_idempotency_key()
    {
        await using SimHost sim = await SimHost.StartAsync(postgres);
        (await sim.Client.PatchKnobsAsync("ZJ", new { OrderTimeoutRate = 1.0, CreateAnywayRate = 1.0, TimeoutHangMs = 50 })).EnsureSuccessStatusCode();
        OfferView offer = await sim.Client.OfferAsync("ZJ", "IST", "LHR", 'K');

        using HttpResponseMessage create = await sim.Client.CreateOrderAsync("ZJ", offer.OfferId, "limbo-1");
        using HttpResponseMessage lookup = await sim.Client.GetAsync("/ndc/ZJ/orders?idempotencyKey=limbo-1");

        create.StatusCode.ShouldBe(HttpStatusCode.GatewayTimeout);
        (await create.ErrorCodeAsync()).ShouldBe("UPSTREAM_TIMEOUT");
        lookup.StatusCode.ShouldBe(HttpStatusCode.OK);
        (await lookup.ReadAsync(SimJson.Default.OrderView)).Status.ShouldBe(OrderStatus.Booked);
    }

    [Fact]
    public async Task A_timed_out_creation_that_created_nothing_is_not_found_and_can_be_retried()
    {
        await using SimHost sim = await SimHost.StartAsync(postgres);
        (await sim.Client.PatchKnobsAsync("ZJ", new { OrderTimeoutRate = 1.0, CreateAnywayRate = 0.0, TimeoutHangMs = 50 })).EnsureSuccessStatusCode();
        OfferView offer = await sim.Client.OfferAsync("ZJ", "IST", "LHR", 'K');

        using HttpResponseMessage create = await sim.Client.CreateOrderAsync("ZJ", offer.OfferId, "limbo-2");
        using HttpResponseMessage lookup = await sim.Client.GetAsync("/ndc/ZJ/orders?idempotencyKey=limbo-2");
        (await sim.Client.PatchKnobsAsync("ZJ", new { OrderTimeoutRate = 0.0 })).EnsureSuccessStatusCode();
        using HttpResponseMessage retry = await sim.Client.CreateOrderAsync("ZJ", offer.OfferId, "limbo-2");

        create.StatusCode.ShouldBe(HttpStatusCode.GatewayTimeout);
        lookup.StatusCode.ShouldBe(HttpStatusCode.NotFound);
        retry.StatusCode.ShouldBe(HttpStatusCode.Created);
    }

    [Fact]
    public async Task Timeouts_and_created_anyway_follow_their_rates()
    {
        const int Attempts = 200;
        await using SimHost sim = await SimHost.StartAsync(postgres);
        (await sim.Client.PatchKnobsAsync("ZJ", new { OrderTimeoutRate = 0.5, CreateAnywayRate = 0.5, TimeoutHangMs = 0 })).EnsureSuccessStatusCode();
        OfferView offer = await sim.Client.OfferAsync("ZJ", "IST", "LHR", 'K');
        await sim.SetSeatsAsync(offer.Segments[0].FlightNumber, SimHost.Outbound, 'K', Attempts);

        int timeouts = 0, createdAnyway = 0;
        for (int i = 0; i < Attempts; i++)
        {
            using HttpResponseMessage create = await sim.Client.CreateOrderAsync("ZJ", offer.OfferId, $"rate-{i}");
            if (create.StatusCode == HttpStatusCode.GatewayTimeout)
            {
                timeouts++;
                using HttpResponseMessage lookup = await sim.Client.GetAsync($"/ndc/ZJ/orders?idempotencyKey=rate-{i}");
                createdAnyway += lookup.StatusCode == HttpStatusCode.OK ? 1 : 0;
            }
        }

        (timeouts / (double)Attempts).ShouldBeInRange(0.39, 0.61);
        (createdAnyway / (double)timeouts).ShouldBeInRange(0.33, 0.67);
    }

    [Fact]
    public async Task Bad_knobs_are_rejected_and_reset_restores_every_default()
    {
        await using SimHost sim = await SimHost.StartAsync(postgres, latency: true);

        using HttpResponseMessage bad = await sim.Client.PatchKnobsAsync("JG", new { TicketingRefusalRate = 1.5 });
        (await sim.Client.PatchKnobsAsync("JG", new { Outage = true })).EnsureSuccessStatusCode();
        using HttpResponseMessage reset = await sim.Client.PostAsync("/admin/knobs/reset", null);
        IReadOnlyList<AirlineView> airlines = await (await sim.Client.GetAsync("/admin/airlines")).ReadAsync(SimJson.Default.IReadOnlyListAirlineView);

        bad.StatusCode.ShouldBe(HttpStatusCode.BadRequest);
        reset.StatusCode.ShouldBe(HttpStatusCode.NoContent);
        airlines.Select(a => a.Code).ShouldBe(["ZC", "YZ", "JG", "ZJ"]);
        airlines.ShouldAllBe(a => a.Knobs == a.Defaults);
    }

    private static async Task<double> MedianSearchSecondsAsync(SimHost sim, string airline)
    {
        double[] seconds = await Task.WhenAll(Enumerable.Range(0, 9).Select(async _ =>
        {
            Stopwatch watch = Stopwatch.StartNew();
            using HttpResponseMessage response = await sim.Client.SearchAsync(airline, "IST", "LHR");
            response.EnsureSuccessStatusCode();
            return watch.Elapsed.TotalSeconds;
        }));

        return seconds.Order().ElementAt(seconds.Length / 2);
    }
}
