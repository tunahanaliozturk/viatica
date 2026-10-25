using Viatica.Booking.Domain;
using Viatica.Kernel;

namespace Viatica.Booking.UnitTests;

/// <summary>BKG-4 on the aggregate: the request, the owner's answer, the refund as one payment operation, and what the trip total does.</summary>
public sealed class CancellationTests
{
    private static readonly TimeSpan Timeout = TimeSpan.FromMinutes(2);

    private static TripScenario Confirmed(ItemTerms? stay = null, ItemTerms? car = null)
    {
        var t = new TripScenario();
        t.CheckOut();
        t.StayPlaced(stay);
        t.CarPlaced(car);
        t.FlightCreated();
        t.Authorized();
        t.Ticketed();
        t.StayCommitted();
        t.CarCommitted();
        t.Captured();
        t.Trip.Status.ShouldBe(TripStatus.Confirmed);
        return t;
    }

    [Fact]
    public void Cancelling_a_stay_asks_its_owner_and_refunds_by_its_policy_once_the_owner_confirms()
    {
        TripScenario t = Confirmed();

        IReadOnlyList<SagaDirective> asked = t.Trip.RequestCancellation(t.Stay, t.Now, Timeout);
        asked.OfType<CancelBooking>().Single().ItemId.ShouldBe(t.Stay);
        asked.OfType<RefundPayment>().ShouldBeEmpty("the refund waits for the owner");

        IReadOnlyList<SagaDirective> done = t.Trip.OnBookingCancelled(t.Stay, t.HoldOf(t.Stay), t.Now);

        RefundPayment refund = done.OfType<RefundPayment>().Single();
        refund.Amount.ShouldBe(Money.Eur(300m), "thirty days before the stay, inside the free window");
        refund.PaymentId.ShouldBe(t.Trip.PaymentId);
        done.OfType<ItemCancelledAnnouncement>().Single().Refund.ShouldBe(Money.Eur(300m));
        t.Trip.CurrentTotal.ShouldBe(Money.Eur(240m));
        t.Trip.RefundedTotal.ShouldBe(Money.Eur(300m));
        t.Trip.Status.ShouldBe(TripStatus.Confirmed);
    }

    [Fact]
    public void The_refund_is_worked_out_for_the_moment_the_traveller_asked_not_the_moment_the_owner_answered()
    {
        TripScenario t = Confirmed(stay: new ItemTerms(null, TripScenario.Start.AddHours(72), 3, 48, Penalty.FirstUnit));

        t.Trip.RequestCancellation(t.Stay, TripScenario.Start, Timeout);
        // The owner takes its time: by now the free window is long gone.
        t.Now = TripScenario.Start.AddDays(2);
        IReadOnlyList<SagaDirective> done = t.Trip.OnBookingCancelled(t.Stay, t.HoldOf(t.Stay), t.Now);

        done.OfType<RefundPayment>().Single().Amount.ShouldBe(Money.Eur(300m));
    }

    [Fact]
    public void A_cancellation_with_nothing_to_refund_makes_no_payment_operation_but_is_announced()
    {
        TripScenario t = Confirmed(stay: new ItemTerms(null, TripScenario.Start.AddHours(24), 3, 48, Penalty.FullAmount));
        t.Trip.RequestCancellation(t.Stay, t.Now, Timeout);

        IReadOnlyList<SagaDirective> done = t.Trip.OnBookingCancelled(t.Stay, t.HoldOf(t.Stay), t.Now);

        done.OfType<RefundPayment>().ShouldBeEmpty();
        done.OfType<ItemCancelledAnnouncement>().Single().Refund.ShouldBe(Money.Zero("EUR"));
        t.Trip.Items.Single(i => i.Id == t.Stay).Refund.ShouldBe(RefundStatus.None);
    }

    [Fact]
    public void An_item_with_no_terms_on_record_refunds_nothing_rather_than_guess()
    {
        TripScenario t = Confirmed(stay: new ItemTerms(null, null, 3, null, null));
        t.Trip.RequestCancellation(t.Stay, t.Now, Timeout);

        t.Trip.OnBookingCancelled(t.Stay, t.HoldOf(t.Stay), t.Now).OfType<RefundPayment>().ShouldBeEmpty();
    }

    [Fact]
    public void A_flight_refunds_what_the_airline_says_but_never_more_than_it_cost()
    {
        TripScenario t = Confirmed();
        t.Trip.RequestCancellation(t.Flight, t.Now, Timeout).OfType<CancelFlightOrder>().Single().OrderId.ShouldBe(t.FlightOrder);

        IReadOnlyList<SagaDirective> done = t.Trip.OnFlightOrderCancelled(t.Flight, t.FlightOrder, Money.Eur(400m), t.Now);

        done.OfType<RefundPayment>().Single().Amount.ShouldBe(Money.Eur(150m));
    }

    [Fact]
    public void A_flight_cancelled_for_the_fare_rules_refund_uses_the_fares_amount()
    {
        TripScenario t = Confirmed();
        t.Trip.RequestCancellation(t.Flight, t.Now, Timeout);

        t.Trip.OnFlightOrderCancelled(t.Flight, t.FlightOrder, Money.Eur(75m), t.Now).OfType<RefundPayment>().Single().Amount.ShouldBe(Money.Eur(75m));
    }

    [Fact]
    public void A_flight_that_was_voided_instead_of_cancelled_refunds_nothing()
    {
        TripScenario t = Confirmed();
        t.Trip.RequestCancellation(t.Flight, t.Now, Timeout);

        IReadOnlyList<SagaDirective> done = t.Trip.OnFlightOrderVoided(t.Flight, t.FlightOrder, t.Now);

        done.OfType<RefundPayment>().ShouldBeEmpty();
        t.Trip.Items.Single(i => i.Id == t.Flight).Status.ShouldBe(ItemStatus.Cancelled);
    }

    [Fact]
    public void Asking_again_while_it_is_pending_and_the_owner_answering_twice_change_nothing()
    {
        TripScenario t = Confirmed();
        t.Trip.RequestCancellation(t.Stay, t.Now, Timeout).ShouldNotBeEmpty();

        t.Trip.RequestCancellation(t.Stay, t.Now, Timeout).ShouldBeEmpty();
        t.Trip.OnBookingCancelled(t.Stay, t.HoldOf(t.Stay), t.Now).OfType<RefundPayment>().ShouldHaveSingleItem();
        t.Trip.OnBookingCancelled(t.Stay, t.HoldOf(t.Stay), t.Now).ShouldBeEmpty();
        t.Trip.RequestCancellation(t.Stay, t.Now, Timeout).ShouldBeEmpty("it is cancelled already");
    }

    [Fact]
    public void A_cancel_nobody_answers_times_out_can_be_asked_again_and_a_late_answer_is_still_taken()
    {
        TripScenario t = Confirmed();
        t.Trip.RequestCancellation(t.Flight, t.Now, Timeout);
        t.Trip.NextCheckAt.ShouldBe(t.Now + Timeout);

        IReadOnlyList<SagaDirective> due = t.Trip.RunScheduledChecks(t.Now + Timeout);

        due.OfType<ItemStatusAnnouncement>().Single().Cancel.ShouldBe(CancelState.TimedOut);
        t.Trip.Items.Single(i => i.Id == t.Flight).Status.ShouldBe(ItemStatus.Ticketed);

        t.Trip.RequestCancellation(t.Flight, t.Now + Timeout, Timeout).OfType<CancelFlightOrder>().ShouldHaveSingleItem();
        t.Trip.OnFlightOrderCancelled(t.Flight, t.FlightOrder, Money.Eur(75m), t.Now).OfType<RefundPayment>().ShouldHaveSingleItem();
        t.Trip.OnFlightOrderCancelled(t.Flight, t.FlightOrder, Money.Eur(75m), t.Now).ShouldBeEmpty();

        var late = Confirmed();
        late.Trip.RequestCancellation(late.Flight, late.Now, Timeout);
        late.Trip.RunScheduledChecks(late.Now + Timeout);
        late.Trip.OnFlightOrderCancelled(late.Flight, late.FlightOrder, Money.Eur(75m), late.Now).OfType<RefundPayment>().ShouldHaveSingleItem();
    }

    [Fact]
    public void The_refund_is_confirmed_by_the_payment_and_only_for_that_refund()
    {
        TripScenario t = Confirmed();
        t.Trip.RequestCancellation(t.Stay, t.Now, Timeout);
        Guid refundId = t.Trip.OnBookingCancelled(t.Stay, t.HoldOf(t.Stay), t.Now).OfType<RefundPayment>().Single().RefundId;

        t.Trip.OnPaymentRefunded(t.Trip.PaymentId, Guid.NewGuid(), t.Now).ShouldBeEmpty();
        t.Trip.Items.Single(i => i.Id == t.Stay).Refund.ShouldBe(RefundStatus.Requested);

        t.Trip.OnPaymentRefunded(t.Trip.PaymentId, refundId, t.Now);
        t.Trip.Items.Single(i => i.Id == t.Stay).Refund.ShouldBe(RefundStatus.Refunded);
    }

    [Fact]
    public void Cancelling_everything_cancels_the_trip_and_leaves_nothing_to_pay()
    {
        TripScenario t = Confirmed();
        foreach (Guid item in new[] { t.Stay, t.Car })
        {
            t.Trip.RequestCancellation(item, t.Now, Timeout);
            t.Trip.OnBookingCancelled(item, t.HoldOf(item), t.Now);
        }

        t.Trip.Status.ShouldBe(TripStatus.Confirmed, "the flight is still booked");
        t.Trip.RequestCancellation(t.Flight, t.Now, Timeout);
        IReadOnlyList<SagaDirective> last = t.Trip.OnFlightOrderCancelled(t.Flight, t.FlightOrder, Money.Eur(75m), t.Now);

        t.Trip.Status.ShouldBe(TripStatus.Cancelled);
        last.OfType<TripStatusAnnouncement>().Single().Status.ShouldBe(TripStatus.Cancelled);
        t.Trip.CurrentTotal.ShouldBe(Money.Zero("EUR"));
    }

    [Fact]
    public void What_cannot_be_cancelled_says_why()
    {
        var draft = new TripScenario();
        ErrorOf(() => draft.Trip.RequestCancellation(draft.Stay, draft.Now, Timeout)).ShouldBe(BookingErrorCode.ItemNotCancellable);

        var running = new TripScenario().Through(TripStatus.Authorizing);
        ErrorOf(() => running.Trip.RequestCancellation(running.Stay, running.Now, Timeout)).ShouldBe(BookingErrorCode.CheckoutInProgress);

        TripScenario t = Confirmed();
        ErrorOf(() => t.Trip.RequestCancellation(Guid.NewGuid(), t.Now, Timeout)).ShouldBe(BookingErrorCode.ItemNotFound);
        ErrorOf(() => t.Trip.RequestCancellation(t.Stay, TripScenario.Start.AddDays(60), Timeout)).ShouldBe(BookingErrorCode.ItemNotCancellable);

        var failed = new TripScenario();
        failed.CheckOut();
        failed.Trip.OnHoldRejected(failed.Stay, failed.HoldOf(failed.Stay), "SoldOut", failed.Now);
        ErrorOf(() => failed.Trip.RequestCancellation(failed.Stay, failed.Now, Timeout)).ShouldBe(BookingErrorCode.CheckoutInProgress);
    }

    private static string ErrorOf(Action act) => Should.Throw<ApiErrorException>(act).Error.Code;
}
