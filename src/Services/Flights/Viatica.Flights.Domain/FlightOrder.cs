using System.Globalization;
using Viatica.Kernel;

namespace Viatica.Flights.Domain;

public enum FlightOrderStatus
{
    /// <summary>The airline holds the seats until <see cref="FlightOrder.TicketingTimeLimit"/>.</summary>
    Booked,
    Ticketed,
    Voided,
    Cancelled,
}

/// <summary>What happened when a transition was asked for, so a redelivered request is a no-op and not an error.</summary>
public enum Transition
{
    Applied,

    /// <summary>The order is already in the state the request wanted. Answer again, change nothing.</summary>
    AlreadyApplied,

    /// <summary>The order has moved to a state this request cannot follow, a void after a cancel for instance.</summary>
    NotAllowed,
}

/// <summary>
/// Our mirror of an order at an airline. The airline owns the truth; this row exists so Booking can ask for tickets,
/// voids and cancellations by our id, and so a redelivered request finds what the first one did.
/// </summary>
/// <remarks>
/// The passenger list is kept sealed (AES-256-GCM, <see cref="SealedPassengers"/>) and nothing in this class can
/// read it. Only the handler that created the order holds the key and the plain text, and only for as long as the
/// airline call takes.
/// </remarks>
public sealed class FlightOrder
{
    private FlightOrder()
    {
    }

    public Guid Id { get; private set; }

    public Guid TripId { get; private set; }

    /// <summary>Booking's id for the trip item. Also our idempotency key at the airline, and unique here.</summary>
    public Guid ItemId { get; private set; }

    public string AirlineCode { get; private set; } = "";

    public string BookingReference { get; private set; } = "";

    /// <summary>The key the airline knows this order by. It equals <see cref="ItemId"/> in text form.</summary>
    public string IdempotencyKey { get; private set; } = "";

    /// <summary>The offer id Booking sent, kept for support: it names the search the traveller saw.</summary>
    public string OfferId { get; private set; } = "";

    public decimal TotalAmount { get; private set; }

    public string Currency { get; private set; } = "";

    public Money Total => new(TotalAmount, Currency);

    public DateTimeOffset TicketingTimeLimit { get; private set; }

    public FlightOrderStatus Status { get; private set; }

    public string[] TicketNumbers { get; private set; } = [];

    /// <summary>Why the airline last refused to ticket, for operators. Null if it never did.</summary>
    public string? TicketingRefusal { get; private set; }

    public decimal? RefundAmount { get; private set; }

    public Money? Refund => RefundAmount is { } amount ? new Money(amount, Currency) : null;

    /// <summary>Ciphertext of the passenger list. Never logged and never returned by any endpoint.</summary>
    public string SealedPassengers { get; private set; } = "";

    public DateTimeOffset CreatedAt { get; private set; }

    public DateTimeOffset UpdatedAt { get; private set; }

    /// <summary>Postgres <c>xmin</c>: the row version, and the ETag.</summary>
    public uint Version { get; private set; }

    public static FlightOrder Create(
        Guid tripId,
        Guid itemId,
        string airlineCode,
        string bookingReference,
        string offerId,
        Money total,
        DateTimeOffset ticketingTimeLimit,
        string sealedPassengers,
        DateTimeOffset now)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(airlineCode);
        ArgumentException.ThrowIfNullOrWhiteSpace(bookingReference);
        ArgumentException.ThrowIfNullOrWhiteSpace(sealedPassengers);
        ArgumentNullException.ThrowIfNull(total);

        return new FlightOrder
        {
            Id = Guid.CreateVersion7(),
            TripId = tripId,
            ItemId = itemId,
            AirlineCode = airlineCode,
            BookingReference = bookingReference,
            IdempotencyKey = IdempotencyKeyFor(itemId),
            OfferId = offerId,
            TotalAmount = total.Amount,
            Currency = total.Currency,
            TicketingTimeLimit = ticketingTimeLimit,
            Status = FlightOrderStatus.Booked,
            SealedPassengers = sealedPassengers,
            CreatedAt = now,
            UpdatedAt = now,
        };
    }

    /// <summary>The key we give the airline for an item. One item is one order, however often it is asked for.</summary>
    public static string IdempotencyKeyFor(Guid itemId) => itemId.ToString("D", CultureInfo.InvariantCulture);

    public Transition Ticket(IReadOnlyList<string> ticketNumbers, DateTimeOffset now)
    {
        ArgumentNullException.ThrowIfNull(ticketNumbers);

        switch (Status)
        {
            case FlightOrderStatus.Ticketed:
                return Transition.AlreadyApplied;
            case FlightOrderStatus.Booked:
                Status = FlightOrderStatus.Ticketed;
                TicketNumbers = [.. ticketNumbers];
                TicketingRefusal = null;
                UpdatedAt = now;
                return Transition.Applied;
            default:
                return Transition.NotAllowed;
        }
    }

    /// <summary>Remembers why the airline said no. The order stays booked until Booking asks for a void.</summary>
    public void RecordTicketingRefusal(string reason, DateTimeOffset now)
    {
        if (Status != FlightOrderStatus.Booked)
        {
            return;
        }

        TicketingRefusal = reason;
        UpdatedAt = now;
    }

    /// <summary>A booked order is released, or a ticketed one is voided inside the airline's void window.</summary>
    public Transition Void(DateTimeOffset now)
    {
        switch (Status)
        {
            case FlightOrderStatus.Voided:
                return Transition.AlreadyApplied;
            case FlightOrderStatus.Booked or FlightOrderStatus.Ticketed:
                Status = FlightOrderStatus.Voided;
                UpdatedAt = now;
                return Transition.Applied;
            default:
                return Transition.NotAllowed;
        }
    }

    /// <summary>A ticketed order is cancelled under the fare rules. <paramref name="refund"/> may be zero.</summary>
    public Transition Cancel(Money refund, DateTimeOffset now)
    {
        ArgumentNullException.ThrowIfNull(refund);

        switch (Status)
        {
            case FlightOrderStatus.Cancelled:
                return Transition.AlreadyApplied;
            case FlightOrderStatus.Ticketed:
                Status = FlightOrderStatus.Cancelled;
                RefundAmount = refund.Amount;
                UpdatedAt = now;
                return Transition.Applied;
            default:
                return Transition.NotAllowed;
        }
    }
}
