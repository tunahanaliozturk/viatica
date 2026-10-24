using Viatica.AirlineSim.Api;
using Viatica.AirlineSim.Network;
using Viatica.AirlineSim.Offers;

namespace Viatica.AirlineSim.UnitTests;

public sealed class OfferAndPassengerTests
{
    private static readonly RouteNetwork Network = new(20261024);
    private static readonly DateOnly TravelDate = new(2026, 12, 15);

    [Fact]
    public void An_offer_id_round_trips_everything_the_offer_promised()
    {
        Leg[] legs =
        [
            new(Network.Between("ZC", "IST", "LHR")[0], TravelDate),
            new(Network.Between("ZC", "LHR", "IST")[0], TravelDate.AddDays(4)),
        ];
        var mix = new PassengerMix(2, 1, 1);
        var now = new DateTimeOffset(2026, 11, 2, 8, 0, 0, TimeSpan.Zero);
        OfferToken token = OfferToken.Create("ZC", FareClass.Find('M')!, mix, legs, new Price(812.40m, 191.20m), now);

        OfferToken decoded = OfferToken.Decode(token.Encode()).ShouldNotBeNull();

        decoded.Airline.ShouldBe("ZC");
        decoded.FareClass.ShouldBe('M');
        decoded.Mix.ShouldBe(mix);
        decoded.Price.ShouldBe(new Price(812.40m, 191.20m));
        decoded.Expiry.ShouldBe(now + OfferToken.Lifetime);
        decoded.ResolveLegs(Network).ShouldBe(legs);
    }

    [Theory]
    [InlineData("")]
    [InlineData("not-an-offer")]
    [InlineData("bnVsbA")] // "null"
    [InlineData("e30")] // "{}"
    public void Anything_that_is_not_an_offer_id_decodes_to_null(string offerId)
    {
        OfferToken.Decode(offerId).ShouldBeNull();
    }

    [Fact]
    public void Legs_of_another_airline_do_not_resolve()
    {
        Leg leg = new(Network.Between("ZC", "IST", "LHR")[0], TravelDate);
        OfferToken token = OfferToken.Create("JG", FareClass.Find('K')!, new PassengerMix(1, 0, 0), [leg], new Price(1, 1), DateTimeOffset.UnixEpoch);

        token.ResolveLegs(Network).ShouldBeNull();
    }

    [Theory]
    [InlineData(0, 0, 0)]
    [InlineData(1, 0, 2)]
    [InlineData(5, 5, 0)]
    [InlineData(1, -1, 0)]
    public void Impossible_passenger_mixes_are_rejected(int adults, int children, int infants)
    {
        new PassengerMix(adults, children, infants).Problem().ShouldNotBeNull();
    }

    [Fact]
    public void Passengers_that_match_the_offer_are_accepted()
    {
        PassengerInput[] passengers =
        [
            new("ADT", "Ayse", "Yilmaz", new DateOnly(1988, 4, 2)),
            new("CHD", "Deniz", "Yilmaz", new DateOnly(2018, 1, 9)),
            new("INF", "Ali", "Yilmaz", new DateOnly(2026, 2, 1)),
        ];

        Ordering.CheckPassengers(passengers, new PassengerMix(1, 1, 1), TravelDate).ShouldBeNull();
    }

    [Fact]
    public void A_passenger_mix_that_differs_from_the_offer_is_rejected()
    {
        PassengerInput[] passengers = [new("ADT", "Ayse", "Yilmaz", new DateOnly(1988, 4, 2))];

        Ordering.CheckPassengers(passengers, new PassengerMix(2, 0, 0), TravelDate).ShouldNotBeNull();
    }

    [Theory]
    [InlineData("ADT", "2016-01-01")] // ten years old is not an adult
    [InlineData("CHD", "2025-06-01")] // eighteen months old is an infant
    [InlineData("INF", "2023-01-01")] // three years old is a child
    [InlineData("XYZ", "1990-01-01")]
    public void A_type_that_does_not_fit_the_age_on_the_travel_date_is_rejected(string type, string birthDate)
    {
        PassengerInput[] passengers = [new(type, "Ayse", "Yilmaz", DateOnly.Parse(birthDate, System.Globalization.CultureInfo.InvariantCulture))];

        Ordering.CheckPassengers(passengers, new PassengerMix(1, 0, 0), TravelDate).ShouldNotBeNull();
    }

    [Theory]
    [InlineData("")]
    [InlineData("R2-D2")]
    [InlineData("<script>")]
    public void Names_that_could_not_be_on_a_passport_are_rejected_without_being_echoed(string name)
    {
        PassengerInput[] passengers = [new("ADT", name, "Yilmaz", new DateOnly(1988, 4, 2))];

        SupplierError error = Ordering.CheckPassengers(passengers, new PassengerMix(1, 0, 0), TravelDate).ShouldNotBeNull();

        if (name.Length > 0)
        {
            error.Text.ShouldNotContain(name);
        }
    }
}
