using Viatica.Booking.Domain;
using Viatica.Kernel;

namespace Viatica.Booking.UnitTests;

/// <summary>
/// Requirements section 6, branch by branch: the happy order, and every way out of it. Each test ends in a legal end
/// state and says what the saga sent and, as important, what it never sent.
/// </summary>
public sealed class SagaBranchTests
{
    [Fact]
    public void The_happy_path_holds_everything_then_authorises_then_tickets_then_commits_then_captures()
    {
        var t = new TripScenario();

        IReadOnlyList<SagaDirective> start = t.CheckOut();
        start.OfType<RequestStayHold>().Count().ShouldBe(1);
        start.OfType<RequestCarHold>().Single().DriverAge.ShouldBe(30);
        start.OfType<RequestFlightOrder>().Count().ShouldBe(1);
        start.OfType<AuthorizePayment>().ShouldBeEmpty("nothing is authorised before every item is held");
        t.Trip.Status.ShouldBe(TripStatus.Holding);

        t.StayPlaced().OfType<AuthorizePayment>().ShouldBeEmpty();
        t.CarPlaced().OfType<AuthorizePayment>().ShouldBeEmpty();
        AuthorizePayment authorise = t.FlightCreated().OfType<AuthorizePayment>().Single();
        authorise.Amount.ShouldBe(Money.Eur(540m), "the sum of the authoritative totals");
        t.Trip.Status.ShouldBe(TripStatus.Authorizing);

        IReadOnlyList<SagaDirective> afterAuth = t.Authorized();
        afterAuth.OfType<RequestFlightTicketing>().Single().OrderId.ShouldBe(t.FlightOrder);
        afterAuth.OfType<CommitHold>().ShouldBeEmpty("stays and cars are committed after the flights are ticketed");
        t.Trip.Status.ShouldBe(TripStatus.Ticketing);

        IReadOnlyList<SagaDirective> afterTickets = t.Ticketed();
        afterTickets.OfType<CommitHold>().Select(static c => c.Kind).ShouldBe([ItemKind.Stay, ItemKind.Car], ignoreOrder: true);
        afterTickets.OfType<CapturePayment>().ShouldBeEmpty("capture is last");
        t.Trip.Status.ShouldBe(TripStatus.Committing);

        t.StayCommitted().OfType<CapturePayment>().ShouldBeEmpty();
        t.CarCommitted().OfType<CapturePayment>().Single().Amount.ShouldBe(Money.Eur(540m));
        t.Trip.Status.ShouldBe(TripStatus.Capturing);

        IReadOnlyList<SagaDirective> done = t.Captured();
        t.Trip.Status.ShouldBe(TripStatus.Confirmed);
        done.OfType<TripConfirmedAnnouncement>().Single().Items.Count.ShouldBe(3);
        t.Trip.SealedTravellerData.ShouldBeNull("personal data is erased when the saga ends");
    }

    [Fact]
    public void A_trip_without_flights_skips_ticketing_and_one_without_stays_or_cars_skips_committing()
    {
        var rooms = new TripScenario(flight: false);
        rooms.CheckOut();
        rooms.StayPlaced();
        rooms.CarPlaced();
        rooms.Authorized().OfType<CommitHold>().Count().ShouldBe(2);
        rooms.Trip.Status.ShouldBe(TripStatus.Committing);

        var flight = new TripScenario(stay: false, car: false);
        flight.CheckOut();
        flight.FlightCreated();
        flight.Authorized();
        flight.Ticketed().OfType<CapturePayment>().Count().ShouldBe(1);
        flight.Trip.Status.ShouldBe(TripStatus.Capturing);
    }

    [Fact]
    public void A_rejected_hold_releases_everything_already_held_and_charges_nothing()
    {
        var t = new TripScenario();
        t.CheckOut();
        t.StayPlaced();
        t.FlightCreated();

        IReadOnlyList<SagaDirective> rejected = t.Track(t.Trip.OnHoldRejected(t.Car, t.HoldOf(t.Car), "SoldOut", t.Now));

        t.Trip.Status.ShouldBe(TripStatus.Unwinding);
        t.Trip.Failure.ShouldBe(FailureReason.ItemRejected);
        rejected.OfType<ReleaseHold>().Single().ItemId.ShouldBe(t.Stay);
        rejected.OfType<VoidFlightOrder>().Single().OrderId.ShouldBe(t.FlightOrder);
        rejected.OfType<ReleaseHold>().Select(static r => r.ItemId).ShouldNotContain(t.Car, "a refused hold has nothing to release");

        t.Track(t.Trip.OnHoldReleased(t.Stay, t.HoldOf(t.Stay), t.Now)).OfType<TripFailedAnnouncement>().ShouldBeEmpty("still waiting for the flight");
        t.Trip.Status.ShouldBe(TripStatus.Unwinding);
        IReadOnlyList<SagaDirective> last = t.Track(t.Trip.OnFlightOrderVoided(t.Flight, t.FlightOrder, t.Now));

        t.Trip.Status.ShouldBe(TripStatus.Failed);
        last.OfType<TripFailedAnnouncement>().Single().Reason.ShouldBe(FailureReason.ItemRejected);
        t.AllSent().OfType<AuthorizePayment>().ShouldBeEmpty();
        t.AllSent().OfType<VoidPayment>().ShouldBeEmpty("nothing was authorised, so nothing is voided");
        t.AllSent().OfType<CapturePayment>().ShouldBeEmpty();
    }

    [Fact]
    public void A_rejected_flight_order_is_an_ordinary_rejection_including_invalid_passengers()
    {
        var t = new TripScenario(car: false);
        t.CheckOut();
        t.StayPlaced();

        t.Trip.OnFlightOrderRejected(t.Flight, "InvalidPassengers", t.Now).OfType<ReleaseHold>().Single().ItemId.ShouldBe(t.Stay);
        t.Trip.Status.ShouldBe(TripStatus.Unwinding);
        t.Trip.Items.Single(i => i.Id == t.Flight).Refusal.ShouldBe("InvalidPassengers");
    }

    [Fact]
    public void A_declined_authorisation_releases_everything_and_voids_nothing()
    {
        var t = new TripScenario();
        t.Through(TripStatus.Authorizing);

        IReadOnlyList<SagaDirective> declined = t.Trip.OnPaymentDeclined(t.Trip.PaymentId, t.Now);

        t.Trip.Status.ShouldBe(TripStatus.Unwinding);
        t.Trip.Failure.ShouldBe(FailureReason.PaymentDeclined);
        declined.OfType<ReleaseHold>().Count().ShouldBe(2);
        declined.OfType<VoidFlightOrder>().Count().ShouldBe(1);
        declined.OfType<VoidPayment>().ShouldBeEmpty("a declined authorisation holds no money");
        declined.OfType<RequestFlightTicketing>().ShouldBeEmpty();

        t.Trip.OnHoldReleased(t.Stay, t.HoldOf(t.Stay), t.Now);
        t.Trip.OnHoldReleased(t.Car, t.HoldOf(t.Car), t.Now);
        t.Trip.OnFlightOrderVoided(t.Flight, t.FlightOrder, t.Now).OfType<TripFailedAnnouncement>().Single().Reason.ShouldBe(FailureReason.PaymentDeclined);
        t.Trip.Status.ShouldBe(TripStatus.Failed);
    }

    [Fact]
    public void A_refused_ticket_voids_the_flight_releases_the_rooms_and_voids_the_payment_without_committing_anything()
    {
        var t = new TripScenario();
        t.Through(TripStatus.Ticketing);

        IReadOnlyList<SagaDirective> refused = t.Track(t.Trip.OnFlightTicketingRefused(t.Flight, t.FlightOrder, "Refused", t.Now));

        t.Trip.Failure.ShouldBe(FailureReason.TicketingRefused);
        refused.OfType<VoidFlightOrder>().Single().OrderId.ShouldBe(t.FlightOrder);
        refused.OfType<ReleaseHold>().Count().ShouldBe(2);
        refused.OfType<VoidPayment>().Count().ShouldBe(1);
        t.AllSent().OfType<CommitHold>().ShouldBeEmpty("ticketing comes first so that a refusal never has to undo a committed stay");
        t.AllSent().OfType<CapturePayment>().ShouldBeEmpty();

        t.Track(t.Trip.OnFlightOrderVoided(t.Flight, t.FlightOrder, t.Now));
        t.Track(t.Trip.OnHoldReleased(t.Stay, t.HoldOf(t.Stay), t.Now));
        t.Track(t.Trip.OnHoldReleased(t.Car, t.HoldOf(t.Car), t.Now));
        t.Trip.Status.ShouldBe(TripStatus.Unwinding, "the payment has not said it is void");
        t.Track(t.Trip.OnPaymentVoided(t.Trip.PaymentId, t.Now)).OfType<TripFailedAnnouncement>().ShouldHaveSingleItem();
        t.Trip.Status.ShouldBe(TripStatus.Failed);
    }

    [Fact]
    public void A_refusal_with_two_flights_voids_the_ticket_already_issued_as_well_as_the_refused_order()
    {
        var t = new TripScenario(stay: false, car: false);
        Guid second = Guid.CreateVersion7();
        Guid secondOrder = Guid.CreateVersion7();
        t.Trip.AddItem(new NewItem(second, ItemKind.Flight, "offer-2", "Return", Money.Eur(100m), 1, null, null), t.Now);
        t.CheckOut();
        t.FlightCreated();
        t.Trip.OnFlightOrderCreated(second, secondOrder, Money.Eur(100m), t.Now.AddHours(1), t.Now);
        t.Authorized();

        t.Ticketed();
        IReadOnlyList<SagaDirective> refused = t.Trip.OnFlightTicketingRefused(second, secondOrder, "Refused", t.Now);

        refused.OfType<VoidFlightOrder>().Select(static v => v.OrderId).ShouldBe([t.FlightOrder, secondOrder], ignoreOrder: true);
    }

    [Fact]
    public void A_commit_lost_to_the_owners_sweeper_after_the_flights_were_ticketed_unwinds_the_whole_trip()
    {
        var t = new TripScenario();
        t.Through(TripStatus.Committing);
        t.StayCommitted();

        IReadOnlyList<SagaDirective> lost = t.Track(t.Trip.OnHoldExpired(t.Car, t.HoldOf(t.Car), t.Now));

        t.Trip.Status.ShouldBe(TripStatus.Unwinding);
        t.Trip.Failure.ShouldBe(FailureReason.DeadlineReached);
        lost.OfType<CancelBooking>().Single().ItemId.ShouldBe(t.Stay, "the stay was committed, so it is given back, not released");
        lost.OfType<VoidFlightOrder>().Single().OrderId.ShouldBe(t.FlightOrder);
        lost.OfType<VoidPayment>().ShouldHaveSingleItem();
        lost.OfType<ReleaseHold>().ShouldBeEmpty();
        t.AllSent().OfType<CapturePayment>().ShouldBeEmpty("nothing was captured");

        t.Track(t.Trip.OnBookingCancelled(t.Stay, t.HoldOf(t.Stay), t.Now));
        t.Track(t.Trip.OnFlightOrderVoided(t.Flight, t.FlightOrder, t.Now));
        t.Track(t.Trip.OnPaymentVoided(t.Trip.PaymentId, t.Now));
        t.Trip.Status.ShouldBe(TripStatus.Failed);
        t.Trip.Items.Single(i => i.Id == t.Stay).Status.ShouldBe(ItemStatus.Cancelled);
    }

    [Fact]
    public void A_commit_still_waiting_when_another_is_lost_is_not_released_underneath_itself_but_cancelled_when_it_lands()
    {
        var t = new TripScenario();
        t.Through(TripStatus.Committing);

        IReadOnlyList<SagaDirective> lost = t.Trip.OnHoldExpired(t.Car, t.HoldOf(t.Car), t.Now);
        lost.OfType<ReleaseHold>().ShouldBeEmpty("the owner ignores a release for a hold that is being committed");
        lost.OfType<CancelBooking>().ShouldBeEmpty();

        IReadOnlyList<SagaDirective> landed = t.StayCommitted();
        landed.OfType<CancelBooking>().Single().ItemId.ShouldBe(t.Stay);
    }

    [Fact]
    public void A_flight_void_answered_as_a_cancellation_closes_the_item_because_the_void_window_had_passed()
    {
        var t = new TripScenario(stay: false, car: false);
        t.CheckOut();
        t.FlightCreated();
        t.Authorized();
        t.Ticketed();
        t.Trip.OnPaymentCaptured(t.Trip.PaymentId, t.Now);
        t.Trip.Status.ShouldBe(TripStatus.Confirmed);

        var failing = new TripScenario(stay: false);
        failing.Through(TripStatus.Committing);
        failing.Track(failing.Trip.OnHoldExpired(failing.Car, failing.HoldOf(failing.Car), failing.Now));
        failing.Track(failing.Trip.OnFlightOrderCancelled(failing.Flight, failing.FlightOrder, Money.Eur(60m), failing.Now));
        failing.Track(failing.Trip.OnPaymentVoided(failing.Trip.PaymentId, failing.Now));

        failing.Trip.Status.ShouldBe(TripStatus.Failed);
        failing.Trip.Items.Single(i => i.Id == failing.Flight).Status.ShouldBe(ItemStatus.Cancelled);
        failing.AllSent().OfType<RefundPayment>().ShouldBeEmpty("the traveller was never charged");
    }

    // The deadline (docs/design.md: "A scheduled check in Booking drives this; it does not depend on any event arriving").

    [Fact]
    public void The_deadline_is_the_shortest_expiry_known_less_two_minutes_and_follows_what_is_reported()
    {
        var t = new TripScenario { TicketingLimit = TimeSpan.FromMinutes(10) };
        t.CheckOut();
        t.Trip.Deadline.ShouldBe(TripScenario.Start.AddMinutes(13), "nothing reported yet: a hold lasts 15 minutes, less the margin");

        t.StayPlaced();
        t.Trip.Deadline.ShouldBe(TripScenario.Start.AddMinutes(13));

        t.FlightCreated();
        t.Trip.Deadline.ShouldBe(TripScenario.Start.AddMinutes(8), "the airline gave 10 minutes, less the margin");
        t.Trip.NextCheckAt.ShouldBe(t.Trip.Deadline);
    }

    [Fact]
    public void Reaching_the_deadline_while_holding_unwinds_and_a_hold_that_arrives_later_is_released()
    {
        var t = new TripScenario();
        t.CheckOut();
        t.StayPlaced();

        IReadOnlyList<SagaDirective> due = t.Trip.RunScheduledChecks(TripScenario.Start.AddMinutes(13));

        t.Trip.Status.ShouldBe(TripStatus.Unwinding);
        t.Trip.Failure.ShouldBe(FailureReason.DeadlineReached);
        due.OfType<ReleaseHold>().Select(static r => r.ItemId).ShouldBe([t.Stay, t.Car], ignoreOrder: true);
        due.OfType<VoidFlightOrder>().ShouldBeEmpty("the order is not known yet, so there is nothing to void by id");

        t.Trip.OnHoldReleased(t.Stay, t.HoldOf(t.Stay), t.Now);
        t.Trip.OnHoldReleased(t.Car, t.HoldOf(t.Car), t.Now);
        t.Trip.Status.ShouldBe(TripStatus.Unwinding, "the flight has not answered");

        t.FlightCreated().OfType<VoidFlightOrder>().Single().OrderId.ShouldBe(t.FlightOrder);
        t.Trip.OnFlightOrderVoided(t.Flight, t.FlightOrder, t.Now);
        t.Trip.Status.ShouldBe(TripStatus.Failed);
    }

    [Fact]
    public void Reaching_the_deadline_while_authorising_unwinds_and_the_late_authorisation_is_voided_once()
    {
        var t = new TripScenario();
        t.Through(TripStatus.Authorizing);

        IReadOnlyList<SagaDirective> due = t.Trip.RunScheduledChecks(t.Trip.Deadline!.Value);

        t.Trip.Status.ShouldBe(TripStatus.Unwinding);
        due.OfType<ReleaseHold>().Count().ShouldBe(2);
        due.OfType<VoidFlightOrder>().Count().ShouldBe(1);
        due.OfType<VoidPayment>().ShouldBeEmpty("the authorisation has not been granted yet");

        t.Trip.OnPaymentAuthorized(t.Trip.PaymentId, t.Now).OfType<VoidPayment>().ShouldHaveSingleItem();
        t.Trip.OnPaymentAuthorized(t.Trip.PaymentId, t.Now).ShouldBeEmpty("the same answer again changes nothing");
    }

    [Fact]
    public void Reaching_the_deadline_while_ticketing_unwinds_and_a_ticket_that_arrives_later_is_voided()
    {
        var t = new TripScenario();
        t.Through(TripStatus.Ticketing);

        IReadOnlyList<SagaDirective> due = t.Trip.RunScheduledChecks(t.Trip.Deadline!.Value);

        t.Trip.Status.ShouldBe(TripStatus.Unwinding);
        due.OfType<VoidFlightOrder>().ShouldBeEmpty("a ticketing request is still open; voiding now would race it");
        due.OfType<VoidPayment>().ShouldHaveSingleItem();
        due.OfType<ReleaseHold>().Count().ShouldBe(2);

        t.Ticketed().OfType<VoidFlightOrder>().Single().OrderId.ShouldBe(t.FlightOrder);
    }

    [Theory]
    [InlineData(TripStatus.Committing)]
    [InlineData(TripStatus.Capturing)]
    public void A_trip_that_has_started_committing_does_not_unwind_at_the_deadline(TripStatus stage)
    {
        var t = new TripScenario();
        t.Through(stage);

        t.Trip.Deadline.ShouldBeNull();
        t.Trip.RunScheduledChecks(TripScenario.Start.AddDays(1)).ShouldBeEmpty();
        t.Trip.Status.ShouldBe(stage);
    }

    [Fact]
    public void A_capture_answered_long_after_the_deadline_still_confirms_the_trip()
    {
        var t = new TripScenario();
        t.Through(TripStatus.Capturing);
        t.Now = TripScenario.Start.AddHours(5);

        t.Captured().OfType<TripConfirmedAnnouncement>().ShouldHaveSingleItem();
        t.Trip.Status.ShouldBe(TripStatus.Confirmed);
    }

    // Unwinding that waits, and resends what nobody answered.

    [Fact]
    public void An_unanswered_release_is_sent_again_after_the_resend_interval_and_not_before()
    {
        var t = new TripScenario();
        t.CheckOut();
        t.StayPlaced();
        t.CarPlaced();
        t.Trip.OnFlightOrderRejected(t.Flight, "SoldOut", t.Now);
        t.Trip.Status.ShouldBe(TripStatus.Unwinding);

        t.Trip.RunScheduledChecks(t.Now.AddSeconds(29)).ShouldBeEmpty();

        IReadOnlyList<SagaDirective> again = t.Trip.RunScheduledChecks(t.Now.AddSeconds(30));
        again.OfType<ReleaseHold>().Count().ShouldBe(2);

        t.Trip.RunScheduledChecks(t.Now.AddSeconds(31)).ShouldBeEmpty("the interval starts again from the resend");
    }

    [Fact]
    public void Nothing_scheduled_is_due_for_a_draft_or_a_confirmed_trip()
    {
        var draft = new TripScenario();
        draft.Trip.NextCheckAt.ShouldBeNull();
        draft.Trip.RunScheduledChecks(TripScenario.Start.AddDays(9)).ShouldBeEmpty();

        var confirmed = new TripScenario().Through(TripStatus.Confirmed);
        confirmed.Trip.NextCheckAt.ShouldBeNull();
        confirmed.Trip.RunScheduledChecks(TripScenario.Start.AddDays(9)).ShouldBeEmpty();
    }

    [Fact]
    public void The_draft_rules_hold_an_item_cap_idempotent_adds_and_no_edits_after_checkout()
    {
        var t = new TripScenario(stay: false, car: false, flight: false);
        Guid id = Guid.CreateVersion7();
        var item = new NewItem(id, ItemKind.Stay, "tok", "A stay", Money.Eur(10m), 1, null, TripScenario.Start.AddMinutes(15));

        t.Trip.AddItem(item, t.Now).ShouldBe(AddItemOutcome.Added);
        t.Trip.AddItem(item, t.Now).ShouldBe(AddItemOutcome.AlreadyThere);
        Should.Throw<ApiErrorException>(() => t.Trip.AddItem(item with { Reference = "other" }, t.Now)).Error.Code.ShouldBe(BookingErrorCode.ItemConflict);

        for (int i = 1; i < Trip.MaxItems; i++)
        {
            t.Trip.AddItem(item with { ItemId = Guid.CreateVersion7() }, t.Now);
        }

        Should.Throw<ApiErrorException>(() => t.Trip.AddItem(item with { ItemId = Guid.CreateVersion7() }, t.Now)).Error.Code.ShouldBe(BookingErrorCode.TripFull);

        t.CheckOut();
        Should.Throw<ApiErrorException>(() => t.Trip.AddItem(item with { ItemId = Guid.CreateVersion7() }, t.Now)).Error.Code.ShouldBe(BookingErrorCode.TripNotEditable);
        Should.Throw<ApiErrorException>(() => t.Trip.RemoveItem(id, t.Now)).Error.Code.ShouldBe(BookingErrorCode.TripNotEditable);
    }

    [Fact]
    public void A_checkout_repeated_changes_nothing_and_one_with_another_body_is_a_conflict()
    {
        var t = new TripScenario();
        Guid payment = Guid.CreateVersion7();

        CheckoutResult first = t.Trip.Checkout("fingerprint-a", "sealed-a", payment, t.Now, SagaTimings.Default);
        int revision = t.Trip.Revision;
        CheckoutResult again = t.Trip.Checkout("fingerprint-a", "sealed-a", Guid.CreateVersion7(), t.Now.AddSeconds(5), SagaTimings.Default);
        CheckoutResult other = t.Trip.Checkout("fingerprint-b", "sealed-b", Guid.CreateVersion7(), t.Now.AddSeconds(6), SagaTimings.Default);

        first.Outcome.ShouldBe(CheckoutOutcome.Started);
        first.Directives.OfType<RequestStayHold>().ShouldHaveSingleItem();
        again.Outcome.ShouldBe(CheckoutOutcome.Repeated);
        again.Directives.ShouldBeEmpty();
        other.Outcome.ShouldBe(CheckoutOutcome.Conflict);
        other.Directives.ShouldBeEmpty();
        t.Trip.Revision.ShouldBe(revision);
        t.Trip.PaymentId.ShouldBe(payment, "the payment id of the first request stands");
        t.Trip.CheckoutFingerprint.ShouldBe("fingerprint-a");
    }

    [Fact]
    public void An_expired_quote_stops_the_checkout_before_anything_is_sent()
    {
        var t = new TripScenario();
        t.Now = TripScenario.Start.AddMinutes(16);

        ApiErrorException error = Should.Throw<ApiErrorException>(() => t.CheckOut());

        error.Error.Code.ShouldBe(BookingErrorCode.QuoteExpired);
        t.Trip.Status.ShouldBe(TripStatus.Draft);
    }

    [Fact]
    public void An_empty_trip_cannot_be_checked_out()
    {
        var t = new TripScenario(stay: false, car: false, flight: false);

        Should.Throw<ApiErrorException>(() => t.CheckOut()).Error.Code.ShouldBe(BookingErrorCode.TripEmpty);
    }
}
