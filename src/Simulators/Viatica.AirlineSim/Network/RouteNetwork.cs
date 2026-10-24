using Viatica.AirlineSim.Behaviour;

namespace Viatica.AirlineSim.Network;

/// <summary>One fare class on one flight: how many seats it starts each day with, and the adult fare before taxes.</summary>
internal sealed record FareBucket(FareClass FareClass, int Seats, decimal AdultFare);

/// <summary>A daily scheduled flight. The same schedule operates every day.</summary>
internal sealed record Flight(
    string Airline,
    string Number,
    Airport Origin,
    Airport Destination,
    TimeOnly Departure,
    int DurationMinutes,
    string Aircraft,
    decimal TaxPerPassenger,
    IReadOnlyList<FareBucket> Fares)
{
    public FareBucket? Bucket(char fareClass) => Fares.FirstOrDefault(bucket => bucket.FareClass.Code == fareClass);

    public DateTime DepartureLocal(DateOnly date) => date.ToDateTime(Departure);

    public DateTimeOffset DepartureUtc(DateOnly date) =>
        new DateTimeOffset(DepartureLocal(date), TimeSpan.FromMinutes(Origin.UtcOffsetMinutes)).ToUniversalTime();

    public DateTimeOffset ArrivalUtc(DateOnly date) => DepartureUtc(date).AddMinutes(DurationMinutes);

    public DateTime ArrivalLocal(DateOnly date) =>
        ArrivalUtc(date).ToOffset(TimeSpan.FromMinutes(Destination.UtcOffsetMinutes)).DateTime;
}

/// <summary>A flight on a date: the unit seats are sold and counted in.</summary>
internal readonly record struct Leg(Flight Flight, DateOnly Date)
{
    public string Key => $"{Flight.Number}:{Date:yyyy-MM-dd}";

    /// <summary>Reads a <see cref="Key"/> back against the network, or null if it names no flight of the airline.</summary>
    public static Leg? Parse(string key, string airline, RouteNetwork network)
    {
        string[] parts = key.Split(':');
        return parts.Length == 2 &&
               network.Find(parts[0]) is { } flight && flight.Airline == airline &&
               DateOnly.TryParseExact(parts[1], "yyyy-MM-dd", out DateOnly date)
            ? new Leg(flight, date)
            : null;
    }
}

/// <summary>
/// Routes, schedules, aircraft, seat counts and fares for every airline, generated from one seed. The same seed gives
/// the same network byte for byte, which is what lets a test name a flight number and a price and expect them.
/// </summary>
/// <remarks>
/// Each airline flies its hub to every European airport and to three long haul airports, plus a handful of
/// point-to-point routes. Four core routes are flown by all four airlines, so a search on them always fans out to
/// every behaviour: IST-LHR, CDG-FCO, AMS-BCN and FRA-JFK. Direct flights only; connections are not simulated.
/// <see cref="Random"/> with a seed is used on purpose: its seeded algorithm is frozen for compatibility, so the
/// network does not change under a runtime upgrade.
/// </remarks>
internal sealed class RouteNetwork
{
    public static readonly IReadOnlyList<(string From, string To)> CoreRoutes =
        [("IST", "LHR"), ("CDG", "FCO"), ("AMS", "BCN"), ("FRA", "JFK")];

    private const double PointToPointShare = 0.08;
    private const int LongHaulPerAirline = 3;

    private static readonly string[] ShortHaulAircraft = ["Airbus A320neo", "Airbus A321neo", "Boeing 737-800", "Embraer E195-E2"];
    private static readonly string[] MediumHaulAircraft = ["Airbus A321neo", "Boeing 737 MAX 8"];
    private static readonly string[] LongHaulAircraft = ["Airbus A350-900", "Boeing 787-9", "Airbus A330-300"];

    private readonly Dictionary<(string Airline, string Origin, string Destination), List<Flight>> _byRoute = [];
    private readonly Dictionary<string, Flight> _byNumber = new(StringComparer.Ordinal);

    public RouteNetwork(int seed)
    {
        Seed = seed;

        foreach ((int index, Airline airline) in Airline.All.Index())
        {
            var random = new Random(unchecked((seed * 7919) + index));
            int nextNumber = 100;

            foreach ((Airport a, Airport b) in RoutesFor(airline, random))
            {
                foreach ((Airport origin, Airport destination) in (ReadOnlySpan<(Airport, Airport)>)[(a, b), (b, a)])
                {
                    int daily = origin.LongHaul || destination.LongHaul ? 1 : random.Next(1, 4);
                    TimeOnly[] departures =
                    [
                        .. Enumerable.Range(0, daily)
                            .Select(_ => new TimeOnly(6, 0).AddMinutes(random.Next(0, 192) * 5))
                            .Order(),
                    ];

                    foreach (TimeOnly departure in departures)
                    {
                        Add(CreateFlight(airline, $"{airline.Code}{nextNumber++}", origin, destination, departure, random));
                    }
                }
            }
        }
    }

    public int Seed { get; }

    public IEnumerable<Flight> Flights => _byNumber.Values;

    public IReadOnlyList<Flight> Between(string airline, string origin, string destination) =>
        _byRoute.TryGetValue((airline, origin, destination), out List<Flight>? flights) ? flights : [];

    public Flight? Find(string flightNumber) => _byNumber.GetValueOrDefault(flightNumber);

    /// <summary>
    /// What <paramref name="passengers"/> pay for <paramref name="legs"/> in <paramref name="fareClass"/>. Children pay
    /// 75% of the adult fare and infants 10%; infants pay a fifth of the taxes.
    /// </summary>
    public static Price PriceOf(IEnumerable<Leg> legs, FareClass fareClass, PassengerMix passengers)
    {
        decimal fares = 0m;
        decimal taxes = 0m;

        foreach (Leg leg in legs)
        {
            FareBucket bucket = leg.Flight.Bucket(fareClass.Code)
                ?? throw new InvalidOperationException($"{leg.Flight.Number} sells no {fareClass.Code} class.");

            decimal adult = Money(bucket.AdultFare * DayFactor(leg.Date.DayOfWeek));
            fares += (adult * passengers.Adults) + (Money(adult * 0.75m) * passengers.Children) +
                     (Money(adult * 0.10m) * passengers.Infants);
            taxes += (leg.Flight.TaxPerPassenger * passengers.Seats) +
                     (Money(leg.Flight.TaxPerPassenger * 0.2m) * passengers.Infants);
        }

        return new Price(fares, taxes);
    }

    // Fares follow the weekly demand curve and nothing else. A fare that moved with the days left to departure would
    // make a test's expected price depend on the day the suite runs.
    private static decimal DayFactor(DayOfWeek day) => day switch
    {
        DayOfWeek.Friday => 1.15m,
        DayOfWeek.Sunday => 1.12m,
        DayOfWeek.Tuesday or DayOfWeek.Wednesday => 0.92m,
        _ => 1.00m,
    };

    private static decimal Money(decimal amount) => Math.Round(amount, 2, MidpointRounding.ToEven);

    private static List<(Airport, Airport)> RoutesFor(Airline airline, Random random)
    {
        Airport hub = Airports.Find(airline.Hub)!;
        Airport[] european = [.. Airports.All.Where(static airport => !airport.LongHaul)];
        var routes = new List<(Airport, Airport)>();
        var seen = new HashSet<(string, string)>();

        void Add(Airport a, Airport b)
        {
            (string, string) key = string.CompareOrdinal(a.Code, b.Code) < 0 ? (a.Code, b.Code) : (b.Code, a.Code);
            if (a != b && seen.Add(key))
            {
                routes.Add((a, b));
            }
        }

        foreach (Airport airport in european)
        {
            Add(hub, airport);
        }

        foreach (Airport airport in Airports.All.Where(static a => a.LongHaul).OrderBy(_ => random.Next()).Take(LongHaulPerAirline))
        {
            Add(hub, airport);
        }

        foreach ((string from, string to) in CoreRoutes)
        {
            Add(Airports.Find(from)!, Airports.Find(to)!);
        }

        for (int i = 0; i < european.Length; i++)
        {
            for (int j = i + 1; j < european.Length; j++)
            {
                if (random.NextDouble() < PointToPointShare)
                {
                    Add(european[i], european[j]);
                }
            }
        }

        return routes;
    }

    private static Flight CreateFlight(
        Airline airline, string number, Airport origin, Airport destination, TimeOnly departure, Random random)
    {
        double km = Airports.DistanceKm(origin, destination);

        // Roughly 780 km/h in the air plus taxi, climb and descent, rounded up to five minutes like a timetable.
        int duration = (int)Math.Ceiling(((km / 780 * 60) + 35) / 5) * 5;

        string[] fleet = km switch
        {
            < 2_000 => ShortHaulAircraft,
            < 4_500 => MediumHaulAircraft,
            _ => LongHaulAircraft,
        };
        string aircraft = fleet[random.Next(fleet.Length)];

        // The cheapest bucket grows with distance; each flight gets its own competitive position on top.
        decimal lowestFare = Math.Round((decimal)(29 + (0.07 * km)) * (decimal)(0.85 + (random.NextDouble() * 0.40)), 0);
        FareBucket[] fares =
        [
            .. FareClass.All.Select(fareClass => new FareBucket(
                fareClass,
                random.Next(fareClass.MinSeats, fareClass.MaxSeats + 1),
                Math.Round(lowestFare * fareClass.Multiplier, 0))),
        ];

        decimal tax = Math.Round((decimal)(18 + (0.01 * km)), 2);
        return new Flight(airline.Code, number, origin, destination, departure, duration, aircraft, tax, fares);
    }

    private void Add(Flight flight)
    {
        _byNumber.Add(flight.Number, flight);

        (string, string, string) route = (flight.Airline, flight.Origin.Code, flight.Destination.Code);
        if (!_byRoute.TryGetValue(route, out List<Flight>? flights))
        {
            _byRoute[route] = flights = [];
        }

        flights.Add(flight);
    }
}
