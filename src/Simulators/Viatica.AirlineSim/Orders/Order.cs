using System.Security.Cryptography;
using System.Text.Json.Serialization;
using Viatica.AirlineSim.Network;

namespace Viatica.AirlineSim.Orders;

[JsonConverter(typeof(JsonStringEnumConverter<OrderStatus>))]
internal enum OrderStatus
{
    /// <summary>Seats are taken and the ticketing time limit is running.</summary>
    Booked,
    Ticketed,
    Voided,
    Cancelled,
}

/// <summary>
/// An order at a simulated airline. It holds no passenger names or birth dates: the sim validates them on the way in
/// and keeps only the counts, because nothing it does later needs to know who is flying.
/// </summary>
internal sealed class Order
{
    /// <summary>How long after ticketing a ticket can still be voided for nothing.</summary>
    public static readonly TimeSpan VoidWindow = TimeSpan.FromHours(24);

    /// <summary>How long a booked order may wait for ticketing, unless departure comes first.</summary>
    public static readonly TimeSpan TicketingWindow = TimeSpan.FromHours(24);

    public Guid Id { get; init; }

    public required string Airline { get; init; }

    public required string BookingReference { get; init; }

    public required string IdempotencyKey { get; init; }

    public required string OfferId { get; init; }

    public char FareClass { get; init; }

    public required string[] Legs { get; init; }

    public int Adults { get; init; }

    public int Children { get; init; }

    public int Infants { get; init; }

    public decimal BaseAmount { get; init; }

    public decimal TaxAmount { get; init; }

    public decimal TotalAmount { get; init; }

    public OrderStatus Status { get; private set; }

    public DateTimeOffset CreatedAt { get; init; }

    public DateTimeOffset TicketingTimeLimit { get; init; }

    /// <summary>Null until the first ticketing attempt, then the airline's answer for good.</summary>
    public bool? TicketingRefused { get; private set; }

    public DateTimeOffset? TicketedAt { get; private set; }

    public string[] TicketNumbers { get; private set; } = [];

    public DateTimeOffset? VoidedAt { get; private set; }

    public DateTimeOffset? CancelledAt { get; private set; }

    public decimal? RefundAmount { get; private set; }

    /// <summary>Postgres xmin, so two concurrent transitions on one order cannot both win.</summary>
    public uint Version { get; private set; }

    public int Seats => Adults + Children;

    public static Order Book(
        string airline, string bookingReference, string idempotencyKey, string offerId, char fareClass, Leg[] legs, PassengerMix mix, Price price, DateTimeOffset now)
    {
        DateTimeOffset firstDeparture = legs.Min(static leg => leg.Flight.DepartureUtc(leg.Date));
        DateTimeOffset limit = now + TicketingWindow;

        return new Order
        {
            Id = Guid.CreateVersion7(now),
            Airline = airline,
            BookingReference = bookingReference,
            IdempotencyKey = idempotencyKey,
            OfferId = offerId,
            FareClass = fareClass,
            Legs = [.. legs.Select(static leg => leg.Key)],
            Adults = mix.Adults,
            Children = mix.Children,
            Infants = mix.Infants,
            BaseAmount = price.Base,
            TaxAmount = price.Taxes,
            TotalAmount = price.Total,
            Status = OrderStatus.Booked,
            CreatedAt = now,
            TicketingTimeLimit = firstDeparture.AddHours(-1) < limit ? firstDeparture.AddHours(-1) : limit,
        };
    }

    /// <summary>
    /// Issues tickets, or says why not. The refusal is drawn once and remembered, so a caller that retries after a
    /// lost response hears the same answer rather than a second roll of the dice.
    /// </summary>
    public SupplierError? Ticket(DateTimeOffset now, Func<bool> refuse, string ticketPrefix)
    {
        switch (Status)
        {
            case OrderStatus.Ticketed:
                return null;
            case OrderStatus.Voided or OrderStatus.Cancelled:
                return SupplierError.OrderClosed(Status);
        }

        if (now > TicketingTimeLimit)
        {
            return SupplierError.TicketingTimeLimitExpired;
        }

        TicketingRefused ??= refuse();
        if (TicketingRefused.Value)
        {
            return SupplierError.TicketingRefused;
        }

        Status = OrderStatus.Ticketed;
        TicketedAt = now;
        TicketNumbers =
        [
            .. Enumerable.Range(0, Adults + Children + Infants)
                .Select(_ => ticketPrefix + RandomNumberGenerator.GetString("0123456789", 10)),
        ];
        return null;
    }

    /// <summary>Voids a booked order at any time, or a ticketed one inside the void window.</summary>
    public SupplierError? Void(DateTimeOffset now)
    {
        switch (Status)
        {
            case OrderStatus.Voided:
                return null;
            case OrderStatus.Cancelled:
                return SupplierError.OrderClosed(Status);
            case OrderStatus.Ticketed when now > TicketedAt + VoidWindow:
                return SupplierError.VoidWindowClosed;
        }

        Status = OrderStatus.Voided;
        VoidedAt = now;
        return null;
    }

    /// <summary>Cancels a ticketed order and refunds it by its fare rules. A booked order is voided instead.</summary>
    public SupplierError? Cancel(DateTimeOffset now)
    {
        switch (Status)
        {
            case OrderStatus.Cancelled:
                return null;
            case OrderStatus.Voided:
                return SupplierError.OrderClosed(Status);
            case OrderStatus.Booked:
                return SupplierError.OrderNotTicketed;
        }

        Status = OrderStatus.Cancelled;
        CancelledAt = now;
        RefundAmount = Network.FareClass.Find(FareClass)!.Refund(TotalAmount, TaxAmount, Seats);
        return null;
    }
}
