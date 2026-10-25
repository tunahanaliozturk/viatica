using Viatica.Booking.Domain;
using Viatica.Kernel;

namespace Viatica.Booking.UnitTests;

/// <summary>
/// A trip with one stay, one car and one flight, checked out at a known instant, and one-line helpers for what the
/// owners say next. Every helper returns the directives the trip decided in answer, so a test reads as a conversation.
/// </summary>
internal sealed class TripScenario
{
    public static readonly DateTimeOffset Start = new(2026, 12, 1, 9, 0, 0, TimeSpan.Zero);

    public TripScenario(bool stay = true, bool car = true, bool flight = true)
    {
        Now = Start;
        Trip = Trip.Create(Guid.CreateVersion7(), Guid.CreateVersion7(), null, Now);
        Trip.TakeDirectives();

        if (stay)
        {
            Stay = Guid.CreateVersion7();
            Trip.AddItem(new NewItem(Stay, ItemKind.Stay, "stay-token", "A stay", Money.Eur(300m), 1, null, Now.AddMinutes(15)), Now);
        }

        if (car)
        {
            Car = Guid.CreateVersion7();
            Trip.AddItem(new NewItem(Car, ItemKind.Car, "car-token", "A car", Money.Eur(90m), 1, 30, Now.AddMinutes(15)), Now);
        }

        if (flight)
        {
            Flight = Guid.CreateVersion7();
            Trip.AddItem(new NewItem(Flight, ItemKind.Flight, "offer-1", "A flight", Money.Eur(150m), 1, null, null), Now);
        }

        Trip.TakeDirectives();
    }

    public Trip Trip { get; }

    /// <summary>Everything the trip decided so far through this scenario's helpers and <see cref="Track"/>.</summary>
    public List<SagaDirective> Sent { get; } = [];

    public IReadOnlyList<SagaDirective> Track(IReadOnlyList<SagaDirective> directives)
    {
        Sent.AddRange(directives);
        return directives;
    }

    public IReadOnlyList<SagaDirective> AllSent() => Sent;

    public DateTimeOffset Now { get; set; }

    public Guid Stay { get; }

    public Guid Car { get; }

    public Guid Flight { get; }

    public Guid FlightOrder { get; } = Guid.CreateVersion7();

    public TimeSpan TicketingLimit { get; set; } = TimeSpan.FromMinutes(60);

    public IReadOnlyList<SagaDirective> CheckOut() =>
        Track(Trip.Checkout("fingerprint", "sealed-data", Guid.CreateVersion7(), Now, SagaTimings.Default).Directives);

    public Guid HoldOf(Guid item) => Trip.Items.Single(i => i.Id == item).HoldId;

    public IReadOnlyList<SagaDirective> StayPlaced(ItemTerms? terms = null) =>
        Track(Trip.OnHoldPlaced(Stay, HoldOf(Stay), Money.Eur(300m), Now.AddMinutes(15), terms ?? DefaultTerms, Now));

    public IReadOnlyList<SagaDirective> CarPlaced(ItemTerms? terms = null) =>
        Track(Trip.OnHoldPlaced(Car, HoldOf(Car), Money.Eur(90m), Now.AddMinutes(15), terms ?? DefaultTerms, Now));

    public IReadOnlyList<SagaDirective> FlightCreated() =>
        Track(Trip.OnFlightOrderCreated(Flight, FlightOrder, Money.Eur(150m), Now + TicketingLimit, Now));

    public IReadOnlyList<SagaDirective> HoldsAll() =>
    [
        .. Stay == default ? [] : StayPlaced(),
        .. Car == default ? [] : CarPlaced(),
        .. Flight == default ? [] : FlightCreated(),
    ];

    public IReadOnlyList<SagaDirective> Authorized() => Track(Trip.OnPaymentAuthorized(Trip.PaymentId, Now));

    public IReadOnlyList<SagaDirective> Ticketed() => Track(Trip.OnFlightTicketed(Flight, FlightOrder, Now));

    public IReadOnlyList<SagaDirective> StayCommitted() => Track(Trip.OnHoldCommitted(Stay, HoldOf(Stay), Now));

    public IReadOnlyList<SagaDirective> CarCommitted() => Track(Trip.OnHoldCommitted(Car, HoldOf(Car), Now));

    public IReadOnlyList<SagaDirective> Captured() => Track(Trip.OnPaymentCaptured(Trip.PaymentId, Now));

    /// <summary>Plays the happy path up to, and including, the given stage.</summary>
    public TripScenario Through(TripStatus stage)
    {
        CheckOut();
        if (stage == TripStatus.Holding)
        {
            return this;
        }

        HoldsAll();
        if (stage == TripStatus.Authorizing)
        {
            return this;
        }

        Authorized();
        if (stage == TripStatus.Ticketing)
        {
            return this;
        }

        if (Flight != default)
        {
            Ticketed();
        }

        if (stage == TripStatus.Committing)
        {
            return this;
        }

        if (Stay != default)
        {
            StayCommitted();
        }

        if (Car != default)
        {
            CarCommitted();
        }

        if (stage == TripStatus.Capturing)
        {
            return this;
        }

        Captured();
        return this;
    }

    public static ItemTerms DefaultTerms { get; } = new(Guid.Parse("c0000000-0000-4000-8000-000000000001"), Start.AddDays(30), 3, 48, Penalty.FirstUnit);
}
