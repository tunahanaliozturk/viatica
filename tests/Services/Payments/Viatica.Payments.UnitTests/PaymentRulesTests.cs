using Viatica.Kernel;
using Viatica.Payments.Domain;

namespace Viatica.Payments.UnitTests;

/// <summary>The money rules, asked of the aggregate with no database and no provider.</summary>
public sealed class PaymentRulesTests
{
    private static readonly DateTimeOffset Now = new(2026, 11, 2, 8, 0, 0, TimeSpan.Zero);

    private static Payment Authorized(decimal amount = 100m) =>
        Payment.Authorize(Guid.CreateVersion7(), Guid.CreateVersion7(), Money.Eur(amount), "pm_test", "pi_1", Now);

    private static Payment Captured(decimal authorized = 100m, decimal captured = 100m)
    {
        Payment payment = Authorized(authorized);
        payment.Capture(Money.Eur(captured), Now);
        return payment;
    }

    [Fact]
    public void An_authorised_payment_holds_the_amount_and_has_taken_nothing()
    {
        Payment payment = Authorized(312.50m);

        payment.Status.ShouldBe(PaymentStatus.Authorized);
        payment.Authorized.ShouldBe(Money.Eur(312.50m));
        payment.Captured.ShouldBe(Money.Eur(0));
        payment.Refunded.ShouldBe(Money.Eur(0));
        payment.ProviderIntentId.ShouldBe("pi_1");
    }

    [Fact]
    public void A_capture_of_the_whole_authorisation_or_less_is_allowed_and_more_is_not()
    {
        Payment payment = Authorized(100m);

        payment.WhyNotCapture(Money.Eur(100m)).ShouldBeNull();
        payment.WhyNotCapture(Money.Eur(40m)).ShouldBeNull();
        payment.WhyNotCapture(Money.Eur(100.01m)).ShouldBe(PaymentRefusals.ExceedsAuthorization);
    }

    [Fact]
    public void Capturing_less_than_was_authorised_closes_the_authorisation()
    {
        Payment payment = Captured(authorized: 100m, captured: 60m);

        payment.Status.ShouldBe(PaymentStatus.Captured);
        payment.Captured.ShouldBe(Money.Eur(60m));
        payment.WhyNotCapture(Money.Eur(40m)).ShouldBe(PaymentRefusals.AlreadyCaptured);
    }

    [Fact]
    public void Nothing_is_captured_after_a_void()
    {
        Payment payment = Authorized();
        payment.Void(Now);

        payment.WhyNotCapture(Money.Eur(10m)).ShouldBe(PaymentRefusals.PaymentVoided);
        Should.Throw<PaymentRuleException>(() => payment.Capture(Money.Eur(10m), Now)).Refusal.ShouldBe(PaymentRefusals.PaymentVoided);
        payment.Captured.ShouldBe(Money.Eur(0), "the refused capture changed nothing");
    }

    [Fact]
    public void A_captured_payment_cannot_be_voided_it_is_refunded_instead()
    {
        Payment payment = Captured();

        payment.WhyNotVoid().ShouldBe(PaymentRefusals.AlreadyCaptured);
        Should.Throw<PaymentRuleException>(() => payment.Void(Now));
        payment.Status.ShouldBe(PaymentStatus.Captured);
    }

    [Fact]
    public void A_void_is_allowed_once()
    {
        Payment payment = Authorized();

        payment.WhyNotVoid().ShouldBeNull();
        payment.Void(Now);

        payment.Status.ShouldBe(PaymentStatus.Voided);
        payment.WhyNotVoid().ShouldBe(PaymentRefusals.PaymentVoided);
    }

    [Fact]
    public void Refunds_add_up_to_the_capture_and_no_further()
    {
        Payment payment = Captured(authorized: 100m, captured: 80m);

        payment.WhyNotRefund(Money.Eur(30m)).ShouldBeNull();
        payment.Refund(Money.Eur(30m), Now);
        payment.Refund(Money.Eur(50m), Now);

        payment.Refunded.ShouldBe(Money.Eur(80m));
        payment.WhyNotRefund(Money.Eur(0.01m)).ShouldBe(PaymentRefusals.ExceedsCapture);
        Should.Throw<PaymentRuleException>(() => payment.Refund(Money.Eur(0.01m), Now));
        payment.Refunded.ShouldBe(Money.Eur(80m));
    }

    [Fact]
    public void A_refund_is_bounded_by_what_was_captured_not_by_what_was_authorised()
    {
        Payment payment = Captured(authorized: 100m, captured: 60m);

        payment.WhyNotRefund(Money.Eur(60m)).ShouldBeNull();
        payment.WhyNotRefund(Money.Eur(60.01m)).ShouldBe(PaymentRefusals.ExceedsCapture);
    }

    [Fact]
    public void Nothing_is_refunded_before_a_capture_or_after_a_void()
    {
        Payment authorized = Authorized();
        Payment voided = Authorized();
        voided.Void(Now);

        authorized.WhyNotRefund(Money.Eur(1m)).ShouldBe(PaymentRefusals.NotCaptured);
        voided.WhyNotRefund(Money.Eur(1m)).ShouldBe(PaymentRefusals.NotCaptured);
    }

    [Fact]
    public void A_declined_payment_can_do_nothing()
    {
        var payment = Payment.Decline(Guid.CreateVersion7(), Guid.CreateVersion7(), Money.Eur(100m), "pm_test", "insufficient_funds", Now);

        payment.Status.ShouldBe(PaymentStatus.Declined);
        payment.DeclineReason.ShouldBe("insufficient_funds");
        payment.ProviderIntentId.ShouldBeNull();
        payment.WhyNotCapture(Money.Eur(1m)).ShouldBe(PaymentRefusals.NotAuthorized);
        payment.WhyNotVoid().ShouldBe(PaymentRefusals.NotAuthorized);
        payment.WhyNotRefund(Money.Eur(1m)).ShouldBe(PaymentRefusals.NotCaptured);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-5)]
    public void An_amount_that_is_not_positive_is_refused(int amount)
    {
        Payment payment = Captured();

        payment.WhyNotCapture(Money.Eur(amount)).ShouldNotBeNull();
        payment.WhyNotRefund(Money.Eur(amount)).ShouldBe(PaymentRefusals.InvalidAmount);
        Should.Throw<PaymentRuleException>(() => Payment.Authorize(Guid.CreateVersion7(), Guid.CreateVersion7(), Money.Eur(amount), "pm_test", "pi_1", Now));
    }

    [Fact]
    public void Money_in_another_currency_is_refused_not_added_to_euros()
    {
        Payment payment = Captured();
        var dollars = new Money(10m, "USD");

        payment.WhyNotCapture(dollars).ShouldBe(PaymentRefusals.CurrencyMismatch);
        payment.WhyNotRefund(dollars).ShouldBe(PaymentRefusals.CurrencyMismatch);
    }

    [Fact]
    public void Whatever_order_requests_arrive_in_the_money_invariants_hold()
    {
        // The three rules the system tests reconcile, under 3000 random sequences of requests: captured <= authorised,
        // refunded <= captured, and a voided payment never has money taken.
        var random = new Random(20261025);
        for (int run = 0; run < 3000; run++)
        {
            decimal authorized = random.Next(1, 100_000) / 100m;
            Payment payment = Authorized(authorized);
            bool voided = false;

            for (int step = 0; step < 12; step++)
            {
                switch (random.Next(3))
                {
                    case 0:
                        var capture = Money.Eur(random.Next(1, 120_000) / 100m);
                        if (payment.WhyNotCapture(capture) is null)
                        {
                            payment.Capture(capture, Now);
                        }

                        break;
                    case 1:
                        if (payment.WhyNotVoid() is null)
                        {
                            payment.Void(Now);
                            voided = true;
                        }

                        break;
                    default:
                        var refund = Money.Eur(random.Next(1, 120_000) / 100m);
                        if (payment.WhyNotRefund(refund) is null)
                        {
                            payment.Refund(refund, Now);
                        }

                        break;
                }

                payment.CapturedAmount.ShouldBeLessThanOrEqualTo(payment.AuthorizedAmount);
                payment.RefundedAmount.ShouldBeLessThanOrEqualTo(payment.CapturedAmount);
                if (voided)
                {
                    payment.CapturedAmount.ShouldBe(0m);
                }
            }
        }
    }

    [Fact]
    public void The_idempotency_key_depends_on_the_trip_the_kind_and_the_operation_id_and_on_nothing_else()
    {
        Guid trip = Guid.CreateVersion7();
        Guid id = Guid.CreateVersion7();

        OperationRecord.KeyFor(trip, OperationKind.Capture, id).ShouldBe(OperationRecord.KeyFor(trip, OperationKind.Capture, id));
        OperationRecord.KeyFor(trip, OperationKind.Capture, id).ShouldNotBe(OperationRecord.KeyFor(trip, OperationKind.Void, id), "one payment id names several operations");
        OperationRecord.KeyFor(trip, OperationKind.Capture, id).ShouldNotBe(OperationRecord.KeyFor(Guid.CreateVersion7(), OperationKind.Capture, id));
        OperationRecord.KeyFor(trip, OperationKind.Refund, id).ShouldNotBe(OperationRecord.KeyFor(trip, OperationKind.Refund, Guid.CreateVersion7()));
        OperationRecord.KeyFor(trip, OperationKind.Capture, id).Length.ShouldBeLessThan(100);
    }
}
