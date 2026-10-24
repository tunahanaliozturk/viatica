using System.Net;
using Viatica.AirlineSim.Api;
using Viatica.AirlineSim.Behaviour;
using Viatica.AirlineSim.Offers;

namespace Viatica.AirlineSim.IntegrationTests;

public sealed class SearchAndPriceTests(Postgres postgres)
{
    [Fact]
    public async Task The_same_seed_gives_identical_search_results()
    {
        await using SimHost first = await SimHost.StartAsync(postgres);
        await using SimHost second = await SimHost.StartAsync(postgres);

        foreach (Airline airline in Airline.All)
        {
            using HttpResponseMessage a = await first.Client.SearchAsync(airline.Code, "IST", "LHR", SimHost.Inbound, adults: 2, children: 1);
            using HttpResponseMessage b = await second.Client.SearchAsync(airline.Code, "IST", "LHR", SimHost.Inbound, adults: 2, children: 1);

            string body = await a.Content.ReadAsStringAsync();
            body.ShouldBe(await b.Content.ReadAsStringAsync());
            body.ShouldContain("OfferId");
        }
    }

    [Fact]
    public async Task A_different_seed_gives_different_fares()
    {
        await using SimHost first = await SimHost.StartAsync(postgres, seed: 1);
        await using SimHost second = await SimHost.StartAsync(postgres, seed: 2);

        using HttpResponseMessage a = await first.Client.SearchAsync("ZC", "IST", "LHR");
        using HttpResponseMessage b = await second.Client.SearchAsync("ZC", "IST", "LHR");

        (await a.Content.ReadAsStringAsync()).ShouldNotBe(await b.Content.ReadAsStringAsync());
    }

    [Fact]
    public async Task A_return_search_offers_both_legs_with_a_fifteen_minute_expiry()
    {
        await using SimHost sim = await SimHost.StartAsync(postgres);

        using HttpResponseMessage response = await sim.Client.SearchAsync("JG", "CDG", "FCO", SimHost.Inbound);
        SearchResponse search = await response.ReadAsync(SimJson.Default.SearchResponse);

        search.Offers.ShouldNotBeEmpty();
        search.Offers.ShouldAllBe(offer =>
            offer.Segments.Count == 2 && offer.Segments[0].Origin == "CDG" && offer.Segments[1].Origin == "FCO" &&
            offer.ExpiresAt == SimHost.Start + OfferToken.Lifetime && offer.Price.Currency == "EUR");
        search.Offers.Select(offer => offer.Price.Total).ShouldBe(search.Offers.Select(offer => offer.Price.Total).Order());
    }

    [Fact]
    public async Task Invalid_searches_get_the_airline_error_shape()
    {
        await using SimHost sim = await SimHost.StartAsync(postgres);

        using HttpResponseMessage unknownAirport = await sim.Client.SearchAsync("ZC", "IST", "XXX");
        using HttpResponseMessage infantsWithoutLaps = await sim.Client.SearchAsync("ZC", "IST", "LHR", adults: 1, infants: 2);
        using HttpResponseMessage unknownAirline = await sim.Client.SearchAsync("QQ", "IST", "LHR");

        unknownAirport.StatusCode.ShouldBe(HttpStatusCode.BadRequest);
        (await unknownAirport.ErrorCodeAsync()).ShouldBe("INVALID_REQUEST");
        infantsWithoutLaps.StatusCode.ShouldBe(HttpStatusCode.BadRequest);
        unknownAirline.StatusCode.ShouldBe(HttpStatusCode.NotFound);
        (await unknownAirline.ErrorCodeAsync()).ShouldBe("UNKNOWN_AIRLINE");
    }

    [Fact]
    public async Task A_sold_out_bucket_disappears_from_search()
    {
        await using SimHost sim = await SimHost.StartAsync(postgres);
        OfferView cheapest = await sim.Client.OfferAsync("ZC", "AMS", "BCN", 'K');

        await sim.SetSeatsAsync(cheapest.Segments[0].FlightNumber, SimHost.Outbound, 'K', 0);

        using HttpResponseMessage response = await sim.Client.SearchAsync("ZC", "AMS", "BCN");
        SearchResponse search = await response.ReadAsync(SimJson.Default.SearchResponse);
        search.Offers.ShouldNotContain(offer => offer.FareClass == "K" && offer.Segments[0].FlightNumber == cheapest.Segments[0].FlightNumber);
    }

    [Fact]
    public async Task Re_pricing_an_open_offer_keeps_the_price()
    {
        await using SimHost sim = await SimHost.StartAsync(postgres);
        OfferView offer = await sim.Client.OfferAsync("ZC", "IST", "LHR", 'K');

        RepriceResponse repriced = await PriceAsync(sim, "ZC", offer);

        repriced.Status.ShouldBe(RepriceStatus.Unchanged);
        repriced.Offer.Price.ShouldBe(offer.Price);
        repriced.Offer.OfferId.ShouldNotBeNullOrEmpty();
    }

    [Fact]
    public async Task The_price_change_knob_makes_a_re_price_dearer()
    {
        await using SimHost sim = await SimHost.StartAsync(postgres);
        OfferView offer = await sim.Client.OfferAsync("ZC", "IST", "LHR", 'K');
        (await sim.Client.PatchKnobsAsync("ZC", new { PriceChangeRate = 1.0 })).EnsureSuccessStatusCode();

        RepriceResponse repriced = await PriceAsync(sim, "ZC", offer);

        repriced.Status.ShouldBe(RepriceStatus.Changed);
        repriced.PreviousTotal.ShouldBe(offer.Price.Total);
        repriced.Offer.Price.Total.ShouldBeInRange(offer.Price.Total * 1.04m, offer.Price.Total * 1.16m);
    }

    [Fact]
    public async Task A_sold_out_class_re_prices_into_the_next_class_up()
    {
        await using SimHost sim = await SimHost.StartAsync(postgres);
        OfferView offer = await sim.Client.OfferAsync("ZC", "IST", "LHR", 'K');
        await sim.SetSeatsAsync(offer.Segments[0].FlightNumber, SimHost.Outbound, 'K', 0);

        RepriceResponse repriced = await PriceAsync(sim, "ZC", offer);

        repriced.Status.ShouldBe(RepriceStatus.Changed);
        repriced.Offer.FareClass.ShouldBe("M");
        repriced.Offer.Price.Total.ShouldBeGreaterThan(offer.Price.Total);
    }

    [Fact]
    public async Task An_offer_is_gone_when_the_cabin_is_sold_out_or_the_offer_expired()
    {
        await using SimHost sim = await SimHost.StartAsync(postgres);
        OfferView soldOut = await sim.Client.OfferAsync("ZC", "IST", "LHR", 'Y');
        OfferView expired = await sim.Client.OfferAsync("ZC", "AMS", "BCN", 'K');
        await sim.SetSeatsAsync(soldOut.Segments[0].FlightNumber, SimHost.Outbound, 'Y', 0);

        using HttpResponseMessage noSeats = await sim.Client.PostAsync($"/ndc/ZC/offers/{soldOut.OfferId}/price", null);
        sim.Clock.Advance(OfferToken.Lifetime + TimeSpan.FromSeconds(1));
        using HttpResponseMessage tooLate = await sim.Client.PostAsync($"/ndc/ZC/offers/{expired.OfferId}/price", null);
        using HttpResponseMessage foreign = await sim.Client.PostAsync($"/ndc/JG/offers/{expired.OfferId}/price", null);

        noSeats.StatusCode.ShouldBe(HttpStatusCode.Conflict);
        (await noSeats.ErrorCodeAsync()).ShouldBe("NO_AVAILABILITY");
        tooLate.StatusCode.ShouldBe(HttpStatusCode.Gone);
        (await tooLate.ErrorCodeAsync()).ShouldBe("OFFER_EXPIRED");
        foreign.StatusCode.ShouldBe(HttpStatusCode.NotFound);
    }

    [Fact]
    public async Task Health_endpoints_answer()
    {
        await using SimHost sim = await SimHost.StartAsync(postgres);

        (await sim.Client.GetAsync("/health/live")).StatusCode.ShouldBe(HttpStatusCode.OK);
        (await sim.Client.GetAsync("/health/ready")).StatusCode.ShouldBe(HttpStatusCode.OK);
    }

    private static async Task<RepriceResponse> PriceAsync(SimHost sim, string airline, OfferView offer)
    {
        using HttpResponseMessage response = await sim.Client.PostAsync($"/ndc/{airline}/offers/{offer.OfferId}/price", null);
        response.StatusCode.ShouldBe(HttpStatusCode.OK);
        return await response.ReadAsync(SimJson.Default.RepriceResponse);
    }
}
