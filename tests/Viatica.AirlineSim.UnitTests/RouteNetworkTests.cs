using System.Security.Cryptography;
using System.Text;
using Viatica.AirlineSim.Behaviour;
using Viatica.AirlineSim.Network;

namespace Viatica.AirlineSim.UnitTests;

public sealed class RouteNetworkTests
{
    private const int DefaultSeed = 20261024;

    [Fact]
    public void The_same_seed_builds_the_same_network()
    {
        Fingerprint(new RouteNetwork(DefaultSeed)).ShouldBe(Fingerprint(new RouteNetwork(DefaultSeed)));
    }

    [Fact]
    public void A_different_seed_builds_a_different_network()
    {
        Fingerprint(new RouteNetwork(1)).ShouldNotBe(Fingerprint(new RouteNetwork(2)));
    }

    /// <summary>
    /// Other suites and the docs name flight numbers and prices from the default network. If this fails, the
    /// generator changed what every downstream test sees: update them together, then this hash. The hash was taken
    /// on Windows and CI checks it on Linux, so it also catches a floating point difference between platforms
    /// leaking into fares or block times.
    /// </summary>
    [Fact]
    public void The_default_network_has_not_moved()
    {
        string hash = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(Fingerprint(new RouteNetwork(DefaultSeed)))));

        hash.ShouldBe(PinnedDefaultNetworkHash);
    }

    [Fact]
    public void Every_airline_flies_every_core_route_both_ways()
    {
        var network = new RouteNetwork(DefaultSeed);

        foreach (Airline airline in Airline.All)
        {
            foreach ((string from, string to) in RouteNetwork.CoreRoutes)
            {
                network.Between(airline.Code, from, to).ShouldNotBeEmpty($"{airline.Code} {from}-{to}");
                network.Between(airline.Code, to, from).ShouldNotBeEmpty($"{airline.Code} {to}-{from}");
            }
        }
    }

    [Fact]
    public void Every_airline_flies_its_hub_to_every_european_airport()
    {
        var network = new RouteNetwork(DefaultSeed);

        foreach (Airline airline in Airline.All)
        {
            foreach (Airport airport in Airports.All.Where(a => !a.LongHaul && a.Code != airline.Hub))
            {
                network.Between(airline.Code, airline.Hub, airport.Code).ShouldNotBeEmpty($"{airline.Code} {airline.Hub}-{airport.Code}");
            }
        }
    }

    [Fact]
    public void Flight_numbers_belong_to_their_airline_and_are_unique()
    {
        Flight[] flights = [.. new RouteNetwork(DefaultSeed).Flights];

        flights.ShouldAllBe(f => f.Number.StartsWith(f.Airline, StringComparison.Ordinal));
        flights.Select(f => f.Number).Distinct().Count().ShouldBe(flights.Length);
        Airports.All.Count.ShouldBe(40);
    }

    [Fact]
    public void Arrival_local_time_accounts_for_the_time_zones()
    {
        // IST is UTC+3 and LHR UTC+0, so a flight lands three hours earlier on the clock than its block time says.
        Flight flight = new RouteNetwork(DefaultSeed).Between("ZC", "IST", "LHR")[0];
        var date = new DateOnly(2026, 11, 20);

        (flight.ArrivalLocal(date) - flight.DepartureLocal(date)).ShouldBe(TimeSpan.FromMinutes(flight.DurationMinutes - 180));
    }

    [Fact]
    public void Children_pay_three_quarters_and_infants_a_tenth_of_the_adult_fare()
    {
        Flight flight = new RouteNetwork(DefaultSeed).Between("ZC", "AMS", "BCN")[0];
        Leg[] legs = [new Leg(flight, new DateOnly(2026, 11, 23))]; // a Monday: day factor 1
        FareClass economy = FareClass.Find('K')!;
        decimal adultFare = flight.Bucket('K')!.AdultFare;

        Price adult = RouteNetwork.PriceOf(legs, economy, new PassengerMix(1, 0, 0));
        Price family = RouteNetwork.PriceOf(legs, economy, new PassengerMix(1, 1, 1));

        adult.Base.ShouldBe(adultFare);
        adult.Taxes.ShouldBe(flight.TaxPerPassenger);
        family.Base.ShouldBe(adultFare + Math.Round(adultFare * 0.75m, 2) + Math.Round(adultFare * 0.10m, 2));
        family.Taxes.ShouldBe((flight.TaxPerPassenger * 2) + Math.Round(flight.TaxPerPassenger * 0.2m, 2));
    }

    [Fact]
    public void Fares_rise_with_the_booking_class_and_on_fridays()
    {
        Flight flight = new RouteNetwork(DefaultSeed).Between("JG", "MAD", "LHR")[0];
        var adult = new PassengerMix(1, 0, 0);
        Leg[] monday = [new Leg(flight, new DateOnly(2026, 11, 23))];
        Leg[] friday = [new Leg(flight, new DateOnly(2026, 11, 27))];

        decimal[] byClass = [.. FareClass.All.Select(c => RouteNetwork.PriceOf(monday, c, adult).Base)];

        byClass.ShouldBe(byClass.Order().ToArray());
        RouteNetwork.PriceOf(friday, FareClass.Find('K')!, adult).Base
            .ShouldBeGreaterThan(RouteNetwork.PriceOf(monday, FareClass.Find('K')!, adult).Base);
    }

    private const string PinnedDefaultNetworkHash = "3A37D089B8FB07043B82BE4BE1FE0AFF98B8E5FDEAEA7E331F39A40CCFBE3980";

    private static string Fingerprint(RouteNetwork network) =>
        string.Join('\n', network.Flights.Select(static f =>
            $"{f.Number} {f.Origin.Code}-{f.Destination.Code} {f.Departure:HH:mm} {f.DurationMinutes} {f.Aircraft} {f.TaxPerPassenger} " +
            string.Join(',', f.Fares.Select(static b => $"{b.FareClass.Code}{b.Seats}@{b.AdultFare}"))));
}
