using System.Net;
using System.Text.Json;

namespace Viatica.Flights.IntegrationTests;

/// <summary>FLT-3: the price is asked again before an offer joins a trip, and a change is shown, never applied.</summary>
[Collection(DefaultFlightsGroup.Name)]
public sealed class RepriceTests(DefaultFlights flights) : IAsyncLifetime
{
    public async ValueTask InitializeAsync() => await flights.Sim.QuietAsync();

    public ValueTask DisposeAsync() => ValueTask.CompletedTask;

    private async Task<JsonElement> CheapestAsync(string airline)
    {
        (_, JsonElement search) = await flights.Client.SearchAsync(Travel.IstanbulToLondon());
        return (await flights.Client.AllOffersAsync(search)).From(airline).First();
    }

    [Fact]
    public async Task An_unchanged_price_comes_back_as_a_fresh_offer_with_the_same_total()
    {
        JsonElement offer = await CheapestAsync("ZC");
        int before = flights.Host.Wire.Count("ZC", HttpMethod.Post, "/price");

        (HttpStatusCode status, JsonElement result) = await flights.Client.RepriceAsync(offer.GetProperty("id").GetString()!);

        status.ShouldBe(HttpStatusCode.OK);
        result.GetProperty("status").GetString().ShouldBe("Unchanged");
        result.TryGetProperty("previousTotal", out _).ShouldBeFalse();
        JsonElement fresh = result.GetProperty("offer");
        fresh.GetProperty("total").Amount().ShouldBe(offer.GetProperty("total").Amount());
        fresh.GetProperty("id").GetString().ShouldNotBe(offer.GetProperty("id").GetString());
        fresh.GetProperty("expiresAt").GetDateTimeOffset().ShouldBeGreaterThanOrEqualTo(offer.GetProperty("expiresAt").GetDateTimeOffset());
        flights.Host.Wire.Count("ZC", HttpMethod.Post, "/price").ShouldBe(before + 1);
    }

    [Fact]
    public async Task A_changed_price_is_returned_with_the_old_total_and_is_not_applied_silently()
    {
        JsonElement offer = await CheapestAsync("ZC");
        await flights.Sim.SetKnobsAsync("ZC", new { PriceChangeRate = 1.0 });

        (HttpStatusCode status, JsonElement result) = await flights.Client.RepriceAsync(offer.GetProperty("id").GetString()!);

        status.ShouldBe(HttpStatusCode.OK);
        result.GetProperty("status").GetString().ShouldBe("PriceChanged");
        decimal previous = result.GetProperty("previousTotal").Amount();
        previous.ShouldBe(offer.GetProperty("total").Amount());

        decimal now = result.GetProperty("offer").GetProperty("total").Amount();
        now.ShouldBeGreaterThan(previous);
        now.ShouldBeLessThan(previous * 1.2m);
    }

    [Fact]
    public async Task The_new_offer_can_be_priced_again_so_a_trip_holds_an_offer_that_exists()
    {
        JsonElement offer = await CheapestAsync("YZ");
        (_, JsonElement first) = await flights.Client.RepriceAsync(offer.GetProperty("id").GetString()!);

        (HttpStatusCode status, JsonElement second) = await flights.Client.RepriceAsync(first.GetProperty("offer").GetProperty("id").GetString()!);

        status.ShouldBe(HttpStatusCode.OK);
        second.GetProperty("status").GetString().ShouldBe("Unchanged");
    }

    [Fact]
    public async Task An_offer_whose_cabin_sold_out_is_gone()
    {
        JsonElement offer = await CheapestAsync("ZC");

        // Every economy class of the flight, so the airline has nothing to move the traveller up to.
        await SetSeatsAsync(offer, "KMY", remaining: 0);
        try
        {
            (HttpStatusCode status, JsonElement error) = await flights.Client.RepriceAsync(offer.GetProperty("id").GetString()!);

            status.ShouldBe(HttpStatusCode.Conflict);
            error.GetProperty("error").GetProperty("code").GetString().ShouldBe("OfferExpired");
        }
        finally
        {
            await SetSeatsAsync(offer, "KMY", remaining: 25);
        }
    }

    [Fact]
    public async Task A_sold_out_class_moves_the_traveller_up_and_says_the_price_changed()
    {
        JsonElement offer = await CheapestAsync("ZC");
        offer.GetProperty("fareClass").GetString().ShouldBe("K", "the cheapest fare of a flight is its lowest bucket");

        await SetSeatsAsync(offer, "K", remaining: 0);
        try
        {
            (HttpStatusCode status, JsonElement result) = await flights.Client.RepriceAsync(offer.GetProperty("id").GetString()!);

            status.ShouldBe(HttpStatusCode.OK);
            result.GetProperty("status").GetString().ShouldBe("PriceChanged");
            result.GetProperty("offer").GetProperty("fareClass").GetString().ShouldBe("M");
            result.GetProperty("offer").GetProperty("total").Amount().ShouldBeGreaterThan(result.GetProperty("previousTotal").Amount());
        }
        finally
        {
            await SetSeatsAsync(offer, "K", remaining: 25);
        }
    }

    // The sim keeps its seats between tests, and a flight left sold out would starve every later test of an offer.
    private async Task SetSeatsAsync(JsonElement offer, string fareClasses, int remaining)
    {
        foreach (JsonElement segment in offer.GetProperty("segments").EnumerateArray())
        {
            foreach (char fareClass in fareClasses)
            {
                await flights.Sim.SetSeatsAsync(
                    segment.GetProperty("flightNumber").GetString()!,
                    DateOnly.FromDateTime(segment.GetProperty("departureLocal").GetDateTime()),
                    fareClass,
                    remaining);
            }
        }
    }

    [Theory]
    [InlineData("0192f0c47a3b7cde8f004f7c2b1d9e01")]
    [InlineData("not-an-offer-id")]
    [InlineData("0192f0c47a3b7cde8f004f7c2b1d9e010192f0c47a3b7cde8f004f7c2b1d9e010192f0c47a3b7cde8f004f7c2b1d9e01")]
    public async Task An_offer_nobody_issued_or_one_that_expired_is_offer_expired(string offerId)
    {
        int before = flights.Host.Wire.Total;

        (HttpStatusCode status, JsonElement error) = await flights.Client.RepriceAsync(offerId);

        status.ShouldBe(HttpStatusCode.Conflict);
        error.GetProperty("error").GetProperty("code").GetString().ShouldBe("OfferExpired");
        flights.Host.Wire.Total.ShouldBe(before);
    }

    [Fact]
    public async Task An_airline_that_cannot_be_asked_is_a_503_and_the_offer_survives_it()
    {
        JsonElement offer = await CheapestAsync("ZC");
        await flights.Sim.SetKnobsAsync("ZC", new { Outage = true });

        (HttpStatusCode status, JsonElement error) = await flights.Client.RepriceAsync(offer.GetProperty("id").GetString()!);

        status.ShouldBe(HttpStatusCode.ServiceUnavailable);
        error.GetProperty("error").GetProperty("code").GetString().ShouldBe("SupplierUnavailable");

        await flights.Sim.SetKnobsAsync("ZC", new { Outage = false });
        (await flights.Client.RepriceAsync(offer.GetProperty("id").GetString()!)).Status.ShouldBe(HttpStatusCode.OK);
    }
}
