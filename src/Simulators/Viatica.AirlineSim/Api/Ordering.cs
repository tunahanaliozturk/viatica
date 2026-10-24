using Microsoft.EntityFrameworkCore;
using Viatica.AirlineSim.Behaviour;
using Viatica.AirlineSim.Network;
using Viatica.AirlineSim.Offers;
using Viatica.AirlineSim.Orders;

namespace Viatica.AirlineSim.Api;

/// <summary>Order creation, lookup, ticketing, void and cancel.</summary>
internal static class Ordering
{
    private const string LogCategory = "Viatica.AirlineSim.Orders";
    private const int MaxIdempotencyKeyLength = 100;
    private const int MaxNameLength = 60;

    public static async Task<IResult> CreateAsync(
        string airline,
        OrderCreateRequest request,
        RouteNetwork network,
        Behaviours behaviours,
        AirlineSimDb db,
        TimeProvider clock,
        ILoggerFactory loggers,
        CancellationToken cancellationToken)
    {
        ILogger logger = loggers.CreateLogger(LogCategory);

        if (string.IsNullOrWhiteSpace(request?.IdempotencyKey) || request.IdempotencyKey.Length > MaxIdempotencyKeyLength ||
            string.IsNullOrEmpty(request.OfferId))
        {
            return SupplierError.Invalid($"OfferId and an IdempotencyKey of at most {MaxIdempotencyKeyLength} characters are required.").ToResult();
        }

        // A repeated key is answered before anything else is checked, so a retry still gets its order after the
        // offer it was made from has expired.
        if (await FindAsync(db, airline, request.IdempotencyKey, cancellationToken) is { } existing)
        {
            return Replay(existing, request, network, logger);
        }

        if (OfferToken.Decode(request.OfferId) is not { } token || token.Airline != airline ||
            FareClass.Find(token.FareClass) is not { } fareClass || token.ResolveLegs(network) is not { } legs)
        {
            return SupplierError.OfferNotFound.ToResult();
        }

        DateTimeOffset now = clock.GetUtcNow();
        if (now > token.Expiry)
        {
            return SupplierError.OfferExpired.ToResult();
        }

        if (CheckPassengers(request.Passengers, token.Mix, legs.Min(static leg => leg.Date)) is { } invalid)
        {
            return invalid.ToResult();
        }

        AirlineState state = behaviours.Find(airline)!;
        Knobs knobs = state.Knobs;
        bool timeout = state.Chance(knobs.OrderTimeoutRate);
        bool createAnyway = timeout && state.Chance(knobs.CreateAnywayRate);

        if (timeout && !createAnyway)
        {
            return await TimeOutAsync(airline, knobs, createdAnyway: false, logger, cancellationToken);
        }

        Order order = Order.Book(airline, request.IdempotencyKey, request.OfferId, fareClass.Code, legs, token.Mix, token.Price, now);
        try
        {
            if (!await TryBookAsync(db, order, legs, cancellationToken))
            {
                return timeout
                    ? await TimeOutAsync(airline, knobs, createdAnyway: false, logger, cancellationToken)
                    : SupplierError.NoAvailability.ToResult();
            }
        }
        catch (DbUpdateException exception) when (AirlineSimDb.IsIdempotencyKeyClash(exception))
        {
            // Two creations with one key raced and the other committed first. Answer with its order.
            db.ChangeTracker.Clear();
            return Replay((await FindAsync(db, airline, request.IdempotencyKey, cancellationToken))!, request, network, logger);
        }

        Log.OrderBooked(logger, airline, order.BookingReference, order.FareClass, order.Seats);
        Telemetry.Transition(airline, "booked");

        if (createAnyway)
        {
            return await TimeOutAsync(airline, knobs, createdAnyway: true, logger, cancellationToken);
        }

        return TypedResults.Json(
            Views.Order(order, network),
            SimJson.Default.OrderView,
            statusCode: StatusCodes.Status201Created);
    }

    public static async Task<IResult> GetAsync(
        string airline, string bookingReference, RouteNetwork network, AirlineSimDb db, CancellationToken cancellationToken) =>
        await db.Orders.AsNoTracking()
                .FirstOrDefaultAsync(o => o.Airline == airline && o.BookingReference == bookingReference, cancellationToken) is { } order
            ? TypedResults.Json(Views.Order(order, network), SimJson.Default.OrderView)
            : SupplierError.OrderNotFound.ToResult();

    /// <summary>
    /// Finds an order by the caller's idempotency key. This is how a caller whose order creation timed out learns
    /// whether the airline created it anyway, without creating a second one.
    /// </summary>
    public static async Task<IResult> FindByIdempotencyKeyAsync(
        string airline, string? idempotencyKey, RouteNetwork network, AirlineSimDb db, CancellationToken cancellationToken)
    {
        if (string.IsNullOrEmpty(idempotencyKey))
        {
            return SupplierError.Invalid("The idempotencyKey query parameter is required.").ToResult();
        }

        return await FindAsync(db, airline, idempotencyKey, cancellationToken) is { } order
            ? TypedResults.Json(Views.Order(order, network), SimJson.Default.OrderView)
            : SupplierError.OrderNotFound.ToResult();
    }

    public static Task<IResult> TicketAsync(
        string airline, string bookingReference, RouteNetwork network, Behaviours behaviours, AirlineSimDb db, TimeProvider clock,
        ILoggerFactory loggers, CancellationToken cancellationToken)
    {
        AirlineState state = behaviours.Find(airline)!;
        ILogger logger = loggers.CreateLogger(LogCategory);

        return TransitionAsync(
            airline, bookingReference, network, db,
            apply: order =>
            {
                bool refusedBefore = order.TicketingRefused is not null;
                SupplierError? error = order.Ticket(clock.GetUtcNow(), () => state.Chance(state.Knobs.TicketingRefusalRate), state.Airline.TicketPrefix);
                if (error == SupplierError.TicketingRefused && !refusedBefore)
                {
                    Log.TicketingRefused(logger, airline, order.BookingReference);
                    Telemetry.Fault(airline, "ticketing_refused");
                }

                return error;
            },
            transitioned: order =>
            {
                Log.OrderTicketed(logger, airline, order.BookingReference);
                Telemetry.Transition(airline, "ticketed");
            },
            cancellationToken);
    }

    public static Task<IResult> VoidAsync(
        string airline, string bookingReference, RouteNetwork network, AirlineSimDb db, TimeProvider clock, ILoggerFactory loggers,
        CancellationToken cancellationToken)
    {
        ILogger logger = loggers.CreateLogger(LogCategory);
        return TransitionAsync(
            airline, bookingReference, network, db,
            apply: order => order.Void(clock.GetUtcNow()),
            transitioned: order =>
            {
                Log.OrderVoided(logger, airline, order.BookingReference);
                Telemetry.Transition(airline, "voided");
            },
            cancellationToken);
    }

    public static Task<IResult> CancelAsync(
        string airline, string bookingReference, RouteNetwork network, AirlineSimDb db, TimeProvider clock, ILoggerFactory loggers,
        CancellationToken cancellationToken)
    {
        ILogger logger = loggers.CreateLogger(LogCategory);
        return TransitionAsync(
            airline, bookingReference, network, db,
            apply: order => order.Cancel(clock.GetUtcNow()),
            transitioned: order =>
            {
                Log.OrderCancelled(logger, airline, order.BookingReference, order.RefundAmount ?? 0m);
                Telemetry.Transition(airline, "cancelled");
            },
            cancellationToken);
    }

    /// <summary>
    /// Loads an order, applies one transition and saves it. Seats go back to their buckets in the same transaction
    /// when the order leaves a seat holding state, and the xmin check makes a concurrent second transition reload and
    /// re-decide instead of returning the seats twice.
    /// </summary>
    private static async Task<IResult> TransitionAsync(
        string airline,
        string bookingReference,
        RouteNetwork network,
        AirlineSimDb db,
        Func<Order, SupplierError?> apply,
        Action<Order> transitioned,
        CancellationToken cancellationToken)
    {
        const int Attempts = 3;

        for (int attempt = 1; ; attempt++)
        {
            Order? order = await db.Orders.FirstOrDefaultAsync(o => o.Airline == airline && o.BookingReference == bookingReference, cancellationToken);
            if (order is null)
            {
                return SupplierError.OrderNotFound.ToResult();
            }

            OrderStatus before = order.Status;
            SupplierError? error = apply(order);
            bool releasesSeats = before is OrderStatus.Booked or OrderStatus.Ticketed &&
                                 order.Status is OrderStatus.Voided or OrderStatus.Cancelled;

            try
            {
                await using (Microsoft.EntityFrameworkCore.Storage.IDbContextTransaction transaction = await db.Database.BeginTransactionAsync(cancellationToken))
                {
                    if (releasesSeats)
                    {
                        foreach (Leg leg in order.Legs.Order(StringComparer.Ordinal).Select(key => Leg.Parse(key, airline, network)!.Value))
                        {
                            await db.ReturnSeatsAsync(airline, leg.Flight.Number, leg.Date, order.FareClass, order.Seats, cancellationToken);
                        }
                    }

                    // Saved even when the transition failed: a refused ticketing is remembered.
                    await db.SaveChangesAsync(cancellationToken);
                    await transaction.CommitAsync(cancellationToken);
                }
            }
            catch (DbUpdateConcurrencyException) when (attempt < Attempts)
            {
                db.ChangeTracker.Clear();
                continue;
            }

            if (error is not null)
            {
                return error.ToResult();
            }

            if (order.Status != before)
            {
                transitioned(order);
            }

            return TypedResults.Json(Views.Order(order, network), SimJson.Default.OrderView);
        }
    }

    private static Task<Order?> FindAsync(AirlineSimDb db, string airline, string idempotencyKey, CancellationToken cancellationToken) =>
        db.Orders.AsNoTracking().FirstOrDefaultAsync(o => o.Airline == airline && o.IdempotencyKey == idempotencyKey, cancellationToken);

    private static IResult Replay(Order existing, OrderCreateRequest request, RouteNetwork network, ILogger logger)
    {
        if (existing.OfferId != request.OfferId)
        {
            return SupplierError.IdempotencyKeyReused.ToResult();
        }

        Log.OrderReplayed(logger, existing.Airline, existing.BookingReference);
        return TypedResults.Json(Views.Order(existing, network), SimJson.Default.OrderView);
    }

    /// <summary>
    /// Takes the seats on every leg and writes the order in one transaction, or nothing at all. Legs are taken in key
    /// order, so two return trips over the same pair of flights lock the rows in the same order and cannot deadlock.
    /// </summary>
    private static async Task<bool> TryBookAsync(AirlineSimDb db, Order order, Leg[] legs, CancellationToken cancellationToken)
    {
        await using Microsoft.EntityFrameworkCore.Storage.IDbContextTransaction transaction = await db.Database.BeginTransactionAsync(cancellationToken);

        foreach (Leg leg in legs.OrderBy(static leg => leg.Key, StringComparer.Ordinal))
        {
            int capacity = leg.Flight.Bucket(order.FareClass)!.Seats;
            if (!await db.TryTakeSeatsAsync(order.Airline, leg.Flight.Number, leg.Date, order.FareClass, capacity, order.Seats, cancellationToken))
            {
                return false;
            }
        }

        db.Orders.Add(order);
        await db.SaveChangesAsync(cancellationToken);
        await transaction.CommitAsync(cancellationToken);
        return true;
    }

    private static async Task<IResult> TimeOutAsync(string airline, Knobs knobs, bool createdAnyway, ILogger logger, CancellationToken cancellationToken)
    {
        Log.OrderTimeout(logger, airline, createdAnyway);
        Telemetry.Fault(airline, createdAnyway ? "timeout_created" : "timeout");
        await Task.Delay(knobs.TimeoutHangMs, cancellationToken);
        return SupplierError.UpstreamTimeout.ToResult();
    }

    /// <summary>
    /// Checks the passengers match the offer's mix and their ages fit their type on the day of travel. Error texts
    /// never repeat a name or a date: they end up in the caller's logs.
    /// </summary>
    internal static SupplierError? CheckPassengers(IReadOnlyList<PassengerInput>? passengers, PassengerMix mix, DateOnly travelDate)
    {
        if (passengers is null || passengers.Any(static p => p is null))
        {
            return SupplierError.Invalid("Passengers are required.");
        }

        int adults = 0, children = 0, infants = 0;
        for (int i = 0; i < passengers.Count; i++)
        {
            PassengerInput passenger = passengers[i];
            if (!IsName(passenger.GivenName) || !IsName(passenger.Surname))
            {
                return SupplierError.Invalid($"Passenger {i + 1}: GivenName and Surname must be 1 to {MaxNameLength} letters, spaces, hyphens or apostrophes.");
            }

            if (passenger.BirthDate > travelDate)
            {
                return SupplierError.Invalid($"Passenger {i + 1}: BirthDate is after the travel date.");
            }

            int age = travelDate.Year - passenger.BirthDate.Year;
            if (passenger.BirthDate > travelDate.AddYears(-age))
            {
                age--;
            }

            bool fits = passenger.Type switch
            {
                "ADT" => age >= 12,
                "CHD" => age is >= 2 and < 12,
                "INF" => age < 2,
                _ => false,
            };

            if (!fits)
            {
                return SupplierError.Invalid($"Passenger {i + 1}: Type must be ADT (12+), CHD (2-11) or INF (under 2) on the travel date.");
            }

            switch (passenger.Type)
            {
                case "ADT": adults++; break;
                case "CHD": children++; break;
                default: infants++; break;
            }
        }

        return new PassengerMix(adults, children, infants) == mix
            ? null
            : SupplierError.Invalid($"Passengers do not match the offer: it was priced for {mix.Adults} ADT, {mix.Children} CHD, {mix.Infants} INF.");
    }

    private static bool IsName(string? name) =>
        !string.IsNullOrWhiteSpace(name) && name.Length <= MaxNameLength &&
        name.All(static c => char.IsLetter(c) || c is ' ' or '-' or '\'');
}
