using Microsoft.EntityFrameworkCore;
using Viatica.AirlineSim.Behaviour;
using Viatica.AirlineSim.Network;
using Viatica.AirlineSim.Offers;
using Viatica.AirlineSim.Orders;

namespace Viatica.AirlineSim.Api;

/// <summary>Search and re-price.</summary>
internal static class Shopping
{
    /// <summary>Flights leaving sooner than this are not sold.</summary>
    private static readonly TimeSpan MinimumNotice = TimeSpan.FromHours(2);

    /// <summary>The shortest gap between landing and the flight back on a same day return.</summary>
    private static readonly TimeSpan MinimumStay = TimeSpan.FromMinutes(90);

    private const int BookingHorizonDays = 330;

    public static async Task<IResult> SearchAsync(
        string airline, SearchRequest request, RouteNetwork network, AirlineSimDb db, TimeProvider clock, CancellationToken cancellationToken)
    {
        DateTimeOffset now = clock.GetUtcNow();
        if (Validate(request, DateOnly.FromDateTime(now.UtcDateTime)) is { } invalid)
        {
            return invalid.ToResult();
        }

        DateTimeOffset earliest = now + MinimumNotice;
        Leg[] outbound = Legs(network, airline, request.Origin, request.Destination, request.DepartureDate, earliest);
        Leg[] inbound = request.ReturnDate is { } returnDate
            ? Legs(network, airline, request.Destination, request.Origin, returnDate, earliest)
            : [];

        List<Leg[]> journeys = request.ReturnDate is null
            ? [.. outbound.Select(static leg => new[] { leg })]
            : [
                .. from away in outbound
                   from back in inbound
                   where back.Flight.DepartureUtc(back.Date) >= away.Flight.ArrivalUtc(away.Date) + MinimumStay
                   select new[] { away, back },
              ];

        if (journeys.Count == 0)
        {
            return TypedResults.Json(new SearchResponse(airline, []), SimJson.Default.SearchResponse);
        }

        Availability availability = await Availability.LoadAsync(db, airline, journeys.SelectMany(static j => j), cancellationToken);
        var offers = new List<(decimal Total, DateTimeOffset Departure, OfferView View)>();

        foreach (Leg[] journey in journeys)
        {
            foreach (FareClass fareClass in FareClass.In(request.Cabin))
            {
                int seatsLeft = journey.Min(leg => availability.Remaining(leg, fareClass));
                if (seatsLeft < request.Passengers.Seats)
                {
                    continue;
                }

                Price price = RouteNetwork.PriceOf(journey, fareClass, request.Passengers);
                OfferToken token = OfferToken.Create(airline, fareClass, request.Passengers, journey, price, now);
                offers.Add((price.Total, journey[0].Flight.DepartureUtc(journey[0].Date), Views.Offer(token, journey, fareClass, seatsLeft)));
            }
        }

        OfferView[] sorted = [.. offers.OrderBy(static o => o.Total).ThenBy(static o => o.Departure).Select(static o => o.View)];
        return TypedResults.Json(new SearchResponse(airline, sorted), SimJson.Default.SearchResponse);
    }

    /// <summary>
    /// Re-prices an offer against today's seats. Same class still open: same price, unless the price change knob
    /// fires. Class sold out: the next class up in the cabin, at its price. Nothing left in the cabin: gone.
    /// </summary>
    public static async Task<IResult> PriceAsync(
        string airline, string offerId, RouteNetwork network, Behaviours behaviours, AirlineSimDb db, TimeProvider clock, CancellationToken cancellationToken)
    {
        if (OfferToken.Decode(offerId) is not { } token || token.Airline != airline ||
            FareClass.Find(token.FareClass) is not { } offered || token.ResolveLegs(network) is not { } legs)
        {
            return SupplierError.OfferNotFound.ToResult();
        }

        DateTimeOffset now = clock.GetUtcNow();
        if (now > token.Expiry)
        {
            return SupplierError.OfferExpired.ToResult();
        }

        PassengerMix mix = token.Mix;
        Availability availability = await Availability.LoadAsync(db, airline, legs, cancellationToken);
        FareClass? fareClass = FareClass.In(offered.Cabin)
            .Where(c => c.Multiplier >= offered.Multiplier)
            .FirstOrDefault(c => legs.All(leg => availability.Remaining(leg, c) >= mix.Seats));

        if (fareClass is null)
        {
            return SupplierError.NoAvailability.ToResult();
        }

        Price price = RouteNetwork.PriceOf(legs, fareClass, mix);

        AirlineState state = behaviours.Find(airline)!;
        if (state.Chance(state.Knobs.PriceChangeRate))
        {
            // Five to fifteen percent dearer: enough that nobody could mistake it for rounding.
            decimal increase = 1.05m + (decimal)(state.Next() * 0.10);
            price = price with { Base = Math.Round(price.Base * increase, 2) };
            Telemetry.Fault(airline, "price_change");
        }

        OfferToken repriced = OfferToken.Create(airline, fareClass, mix, legs, price, now);
        int seatsLeft = legs.Min(leg => availability.Remaining(leg, fareClass));
        RepriceStatus status = price.Total == token.Price.Total ? RepriceStatus.Unchanged : RepriceStatus.Changed;

        return TypedResults.Json(
            new RepriceResponse(status, token.Price.Total, Views.Offer(repriced, legs, fareClass, seatsLeft)),
            SimJson.Default.RepriceResponse);
    }

    private static Leg[] Legs(RouteNetwork network, string airline, string origin, string destination, DateOnly date, DateTimeOffset earliest) =>
        [
            .. network.Between(airline, origin, destination)
                .Select(flight => new Leg(flight, date))
                .Where(leg => leg.Flight.DepartureUtc(leg.Date) >= earliest),
        ];

    private static SupplierError? Validate(SearchRequest? request, DateOnly today)
    {
        if (request?.Passengers is null)
        {
            return SupplierError.Invalid("Origin, Destination, DepartureDate, Passengers and Cabin are required.");
        }

        if (Airports.Find(request.Origin ?? "") is null || Airports.Find(request.Destination ?? "") is null)
        {
            return SupplierError.Invalid("Origin and Destination must be airport codes this airline group serves.");
        }

        if (request.Origin == request.Destination)
        {
            return SupplierError.Invalid("Origin and Destination must differ.");
        }

        if (request.DepartureDate < today || request.DepartureDate > today.AddDays(BookingHorizonDays))
        {
            return SupplierError.Invalid($"DepartureDate must be between today and {BookingHorizonDays} days ahead.");
        }

        if (request.ReturnDate is { } returnDate && (returnDate < request.DepartureDate || returnDate > today.AddDays(BookingHorizonDays)))
        {
            return SupplierError.Invalid("ReturnDate must not be before DepartureDate or beyond the booking horizon.");
        }

        if (!Enum.IsDefined(request.Cabin))
        {
            return SupplierError.Invalid("Cabin must be Economy or Business.");
        }

        return request.Passengers.Problem() is { } problem ? SupplierError.Invalid(problem) : null;
    }
}

/// <summary>Seats left per leg and fare class, read in one query for a whole search.</summary>
internal sealed class Availability(Dictionary<(string FlightNumber, DateOnly Date, char FareClass), int> remaining)
{
    public int Remaining(Leg leg, FareClass fareClass) =>
        remaining.TryGetValue((leg.Flight.Number, leg.Date, fareClass.Code), out int left)
            ? left
            : leg.Flight.Bucket(fareClass.Code)!.Seats;

    public static async Task<Availability> LoadAsync(AirlineSimDb db, string airline, IEnumerable<Leg> legs, CancellationToken cancellationToken)
    {
        Leg[] all = [.. legs];
        string[] numbers = [.. all.Select(static leg => leg.Flight.Number).Distinct()];
        DateOnly[] dates = [.. all.Select(static leg => leg.Date).Distinct()];

        // Numbers and dates are filtered separately; the cross product can return a few rows nobody asked about,
        // which costs less than one query per leg.
        Dictionary<(string, DateOnly, char), int> rows = await db.SeatBuckets
            .AsNoTracking()
            .Where(b => b.Airline == airline && numbers.Contains(b.FlightNumber) && dates.Contains(b.FlightDate))
            .ToDictionaryAsync(static b => (b.FlightNumber, b.FlightDate, b.FareClass), static b => b.Remaining, cancellationToken);

        return new Availability(rows);
    }
}
