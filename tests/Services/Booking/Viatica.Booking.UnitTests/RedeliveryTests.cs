using Viatica.Booking.Domain;
using Viatica.Kernel;

namespace Viatica.Booking.UnitTests;

/// <summary>
/// The saga under redelivery and reordering, on the aggregate (the same checks against real topics are in the
/// integration tests). The same cause twice changes nothing: no directive, and not even the trip's revision. A late
/// answer for a trip that failed gives back exactly what it carries, once.
/// </summary>
public sealed class RedeliveryTests
{
    /// <summary>Every answer the owners give in a full run, each as a call that can be made again.</summary>
    private static List<(string Name, Func<TripScenario, IReadOnlyList<SagaDirective>> Answer)> Answers() =>
    [
        ("StayHoldPlaced", t => t.Trip.OnHoldPlaced(t.Stay, t.HoldOf(t.Stay), Money.Eur(300m), t.Now.AddMinutes(15), TripScenario.DefaultTerms, t.Now)),
        ("CarHoldPlaced", t => t.Trip.OnHoldPlaced(t.Car, t.HoldOf(t.Car), Money.Eur(90m), t.Now.AddMinutes(15), TripScenario.DefaultTerms, t.Now)),
        ("FlightOrderCreated", t => t.Trip.OnFlightOrderCreated(t.Flight, t.FlightOrder, Money.Eur(150m), t.Now.AddHours(1), t.Now)),
        ("PaymentAuthorized", t => t.Trip.OnPaymentAuthorized(t.Trip.PaymentId, t.Now)),
        ("FlightTicketed", t => t.Trip.OnFlightTicketed(t.Flight, t.FlightOrder, t.Now)),
        ("StayHoldCommitted", t => t.Trip.OnHoldCommitted(t.Stay, t.HoldOf(t.Stay), t.Now)),
        ("CarHoldCommitted", t => t.Trip.OnHoldCommitted(t.Car, t.HoldOf(t.Car), t.Now)),
        ("PaymentCaptured", t => t.Trip.OnPaymentCaptured(t.Trip.PaymentId, t.Now)),
    ];

    [Fact]
    public void Every_answer_given_a_second_time_changes_nothing_at_the_stage_it_was_given_for()
    {
        var t = new TripScenario();
        t.CheckOut();

        foreach ((string name, Func<TripScenario, IReadOnlyList<SagaDirective>> answer) in Answers())
        {
            answer(t).ShouldNotBeEmpty($"{name} the first time moves the trip");
            int revision = t.Trip.Revision;
            TripStatus status = t.Trip.Status;

            answer(t).ShouldBeEmpty($"{name} again");
            t.Trip.Revision.ShouldBe(revision, $"{name} again");
            t.Trip.Status.ShouldBe(status, $"{name} again");
        }

        t.Trip.Status.ShouldBe(TripStatus.Confirmed);
    }

    [Fact]
    public void Every_answer_given_again_long_after_the_trip_confirmed_changes_nothing()
    {
        TripScenario t = new TripScenario().Through(TripStatus.Confirmed);
        int revision = t.Trip.Revision;

        foreach ((string name, Func<TripScenario, IReadOnlyList<SagaDirective>> answer) in Answers())
        {
            answer(t).ShouldBeEmpty(name);
        }

        t.Trip.Revision.ShouldBe(revision);
        t.Trip.Status.ShouldBe(TripStatus.Confirmed);
    }

    [Fact]
    public void Every_answer_given_before_it_was_asked_for_changes_nothing()
    {
        // A commit nobody requested, a ticket nobody asked for, a capture before the authorisation: noise, not progress.
        var t = new TripScenario();
        t.CheckOut();
        int revision = t.Trip.Revision;

        t.Trip.OnHoldCommitted(t.Stay, t.HoldOf(t.Stay), t.Now).ShouldBeEmpty();
        t.Trip.OnFlightTicketed(t.Flight, t.FlightOrder, t.Now).ShouldBeEmpty();
        t.Trip.OnPaymentCaptured(t.Trip.PaymentId, t.Now).ShouldBeEmpty();
        t.Trip.OnPaymentVoided(t.Trip.PaymentId, t.Now).ShouldBeEmpty();
        t.Trip.OnHoldReleased(t.Stay, t.HoldOf(t.Stay), t.Now).ShouldBeEmpty();
        t.Trip.OnFlightOrderVoided(t.Flight, t.FlightOrder, t.Now).ShouldBeEmpty();

        t.Trip.Revision.ShouldBe(revision);
        t.Trip.Status.ShouldBe(TripStatus.Holding);
    }

    [Fact]
    public void Answers_for_an_item_or_a_payment_that_is_not_on_the_trip_are_ignored()
    {
        var t = new TripScenario();
        t.CheckOut();
        int revision = t.Trip.Revision;

        t.Trip.OnHoldPlaced(Guid.NewGuid(), Guid.NewGuid(), Money.Eur(1m), t.Now.AddMinutes(15), TripScenario.DefaultTerms, t.Now).ShouldBeEmpty();
        t.Trip.OnHoldPlaced(t.Stay, Guid.NewGuid(), Money.Eur(1m), t.Now.AddMinutes(15), TripScenario.DefaultTerms, t.Now).ShouldBeEmpty("another hold id");
        t.Trip.OnFlightOrderCreated(Guid.NewGuid(), Guid.NewGuid(), Money.Eur(1m), t.Now.AddHours(1), t.Now).ShouldBeEmpty();
        t.Trip.OnPaymentAuthorized(Guid.NewGuid(), t.Now).ShouldBeEmpty();
        t.Trip.Revision.ShouldBe(revision);
    }

    [Fact]
    public void The_three_hold_answers_in_any_order_authorise_the_trip_once_for_the_same_amount()
    {
        List<(string Name, Func<TripScenario, IReadOnlyList<SagaDirective>> Answer)> holds = [.. Answers().Take(3)];

        foreach (int[] order in Permutations([0, 1, 2]))
        {
            var t = new TripScenario();
            t.CheckOut();
            List<SagaDirective> sent = [];
            foreach (int i in order)
            {
                sent.AddRange(holds[i].Answer(t));
            }

            string which = string.Join(",", order.Select(i => holds[i].Name));
            t.Trip.Status.ShouldBe(TripStatus.Authorizing, which);
            sent.OfType<AuthorizePayment>().Single().Amount.ShouldBe(Money.Eur(540m), which);
        }
    }

    [Fact]
    public void The_two_commit_answers_in_either_order_capture_once()
    {
        foreach (bool stayFirst in new[] { true, false })
        {
            TripScenario t = new TripScenario().Through(TripStatus.Committing);
            IEnumerable<SagaDirective> sent = stayFirst ? [.. t.StayCommitted(), .. t.CarCommitted()] : [.. t.CarCommitted(), .. t.StayCommitted()];

            sent.OfType<CapturePayment>().Count().ShouldBe(1);
            t.Trip.Status.ShouldBe(TripStatus.Capturing);
        }
    }

    [Fact]
    public void A_hold_that_arrives_late_for_a_failed_trip_gets_one_release_and_the_same_hold_again_gets_none()
    {
        var t = new TripScenario();
        t.CheckOut();
        t.Trip.OnHoldRejected(t.Stay, t.HoldOf(t.Stay), "SoldOut", t.Now);
        t.Trip.OnHoldRejected(t.Car, t.HoldOf(t.Car), "SoldOut", t.Now);
        t.Trip.OnFlightOrderRejected(t.Flight, "SoldOut", t.Now);
        t.Trip.Status.ShouldBe(TripStatus.Failed);

        IReadOnlyList<SagaDirective> late = t.StayPlaced();

        ReleaseHold release = late.OfType<ReleaseHold>().Single();
        release.ItemId.ShouldBe(t.Stay);
        release.HoldId.ShouldBe(t.HoldOf(t.Stay));
        late.Where(static d => d is not (ReleaseHold or ItemStatusAnnouncement)).ShouldBeEmpty();
        t.StayPlaced().ShouldBeEmpty("the release is already on its way");
        t.Trip.Status.ShouldBe(TripStatus.Failed);

        t.Trip.OnHoldReleased(t.Stay, t.HoldOf(t.Stay), t.Now);
        t.Trip.Items.Single(i => i.Id == t.Stay).Awaiting.ShouldBe(ItemRequest.None);
    }

    [Fact]
    public void An_order_that_arrives_late_for_a_failed_trip_gets_one_void_and_the_same_order_again_gets_none()
    {
        var t = new TripScenario(stay: false, car: false);
        t.CheckOut();
        t.Trip.OnFlightOrderRejected(t.Flight, "SupplierUnavailable", t.Now);
        t.Trip.Status.ShouldBe(TripStatus.Failed);

        IReadOnlyList<SagaDirective> late = t.FlightCreated();

        late.OfType<VoidFlightOrder>().Single().OrderId.ShouldBe(t.FlightOrder);
        t.FlightCreated().ShouldBeEmpty();
        t.Trip.OnFlightOrderVoided(t.Flight, t.FlightOrder, t.Now);
        t.Trip.Items.Single().Status.ShouldBe(ItemStatus.Voided);
        t.Trip.Status.ShouldBe(TripStatus.Failed);
    }

    [Fact]
    public void An_authorisation_that_arrives_late_for_a_failed_trip_gets_one_void_and_the_same_one_again_gets_none()
    {
        var t = new TripScenario(stay: false, car: false);
        t.CheckOut();
        t.Trip.OnFlightOrderRejected(t.Flight, "SoldOut", t.Now);
        t.Trip.Status.ShouldBe(TripStatus.Failed);

        t.Trip.OnPaymentAuthorized(t.Trip.PaymentId, t.Now).OfType<VoidPayment>().Single().PaymentId.ShouldBe(t.Trip.PaymentId);
        t.Trip.OnPaymentAuthorized(t.Trip.PaymentId, t.Now).ShouldBeEmpty();
        t.Trip.OnPaymentVoided(t.Trip.PaymentId, t.Now);
        t.Trip.Payment.ShouldBe(PaymentStatus.Voided);
        t.Trip.OnPaymentAuthorized(t.Trip.PaymentId, t.Now).ShouldBeEmpty("a voided payment is not voided again");
        t.Trip.Status.ShouldBe(TripStatus.Failed);
    }

    [Fact]
    public void A_ticket_that_arrives_after_the_trip_gave_up_is_voided_once()
    {
        TripScenario t = new TripScenario(stay: false, car: false).Through(TripStatus.Ticketing);
        t.Trip.RunScheduledChecks(t.Trip.Deadline!.Value);

        t.Ticketed().OfType<VoidFlightOrder>().ShouldHaveSingleItem();
        t.Ticketed().ShouldBeEmpty();
    }

    private static IEnumerable<int[]> Permutations(int[] items)
    {
        if (items.Length <= 1)
        {
            yield return items;
            yield break;
        }

        for (int i = 0; i < items.Length; i++)
        {
            int[] rest = [.. items.Where((_, index) => index != i)];
            foreach (int[] tail in Permutations(rest))
            {
                yield return [items[i], .. tail];
            }
        }
    }
}
