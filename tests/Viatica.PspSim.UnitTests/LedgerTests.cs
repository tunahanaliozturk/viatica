using System.Text.Json;
using Microsoft.Extensions.Time.Testing;
using Viatica.PspSim.Api;
using Viatica.PspSim.Ledger;

namespace Viatica.PspSim.UnitTests;

/// <summary>The provider's rules without HTTP: what a token, an authorisation, a capture and a refund may do.</summary>
public sealed class LedgerTests
{
    private readonly FakeTimeProvider clock = new(new DateTimeOffset(2026, 10, 25, 9, 0, 0, TimeSpan.Zero));
    private readonly ProviderLedger ledger;

    public LedgerTests() => ledger = new ProviderLedger(clock);

    private string Token(string number = "4242424242424242", string key = "tok-key")
    {
        Execution result = ledger.Tokenise(key, new TokenRequest(new CardInput(number, 12, 2030, "123")));
        result.Reply.Status.ShouldBe(200, result.Reply.Body.ToString());
        return result.Reply.Body.GetProperty("id").GetString()!;
    }

    private static PaymentIntentRequest Authorisation(string token, long amount = 10_000) =>
        new(amount, "eur", token, "manual", new Dictionary<string, string> { ["trip_id"] = "t1" });

    private string Authorised(string number = "4242424242424242", long amount = 10_000, string key = "auth-key")
    {
        Execution result = ledger.Authorise(key, Authorisation(Token(number, key + "-tok"), amount));
        result.Reply.Status.ShouldBe(200, result.Reply.Body.ToString());
        return result.Reply.Body.GetProperty("id").GetString()!;
    }

    private static string Code(Execution result) => result.Reply.Body.GetProperty("error").GetProperty("code").GetString()!;

    [Fact]
    public void A_token_carries_the_brand_and_last_four_and_never_the_number()
    {
        Execution result = ledger.Tokenise("k", new TokenRequest(new CardInput("4242424242424242", 12, 2030, "123")));

        result.Reply.Body.GetProperty("card").GetProperty("last4").GetString().ShouldBe("4242");
        result.Reply.Body.GetProperty("card").GetProperty("brand").GetString().ShouldBe("visa");
        result.Reply.Body.ToString().ShouldNotContain("4242424242424242");
    }

    [Theory]
    [InlineData("4242424242424241", 12, 2030, "123", "incorrect_number")]
    [InlineData("4111111111111111", 12, 2030, "123", "incorrect_number")] // valid, but not a test card
    [InlineData("4242424242424242", 13, 2030, "123", "invalid_expiry_month")]
    [InlineData("4242424242424242", 9, 2026, "123", "expired_card")]
    [InlineData("4242424242424242", 12, 2030, "12", "invalid_cvc")]
    public void A_bad_card_is_refused_with_a_card_error(string number, int month, int year, string cvc, string code)
    {
        Execution result = ledger.Tokenise("k", new TokenRequest(new CardInput(number, month, year, cvc)));

        result.Reply.Status.ShouldBe(402);
        Code(result).ShouldBe(code);
        result.Reply.Body.ToString().ShouldNotContain(number);
    }

    [Fact]
    public void The_current_month_is_not_yet_expired()
    {
        Execution result = ledger.Tokenise("k", new TokenRequest(new CardInput("4242424242424242", 10, 2026, null)));

        result.Reply.Status.ShouldBe(200);
    }

    [Theory]
    [InlineData("4000000000000002", "generic_decline")]
    [InlineData("4000000000009995", "insufficient_funds")]
    public void A_declining_card_is_declined_at_authorisation_with_its_decline_code(string number, string declineCode)
    {
        Execution result = ledger.Authorise("a", Authorisation(Token(number)));

        result.Reply.Status.ShouldBe(402);
        Code(result).ShouldBe("card_declined");
        result.Reply.Body.GetProperty("error").GetProperty("decline_code").GetString().ShouldBe(declineCode);
        ledger.Intents().ShouldBeEmpty("a declined authorisation creates no intent");
    }

    [Fact]
    public void An_authorisation_holds_the_amount_and_a_full_capture_takes_it()
    {
        string intent = Authorised(amount: 31_250);

        Execution captured = ledger.Capture("c", intent, new CaptureRequest(null));

        captured.Reply.Status.ShouldBe(200);
        PaymentIntentView view = ledger.FindIntent(intent)!;
        view.Status.ShouldBe("succeeded");
        view.AmountReceived.ShouldBe(31_250);
    }

    [Fact]
    public void A_partial_capture_releases_the_rest_and_cannot_be_followed_by_another()
    {
        string intent = Authorised(amount: 10_000);

        ledger.Capture("c1", intent, new CaptureRequest(6_000)).Reply.Status.ShouldBe(200);
        Execution second = ledger.Capture("c2", intent, new CaptureRequest(4_000));

        second.Reply.Status.ShouldBe(400);
        Code(second).ShouldBe("payment_intent_unexpected_state");
        ledger.FindIntent(intent)!.AmountReceived.ShouldBe(6_000);
    }

    [Fact]
    public void A_capture_above_the_authorisation_is_refused()
    {
        string intent = Authorised(amount: 10_000);

        Execution result = ledger.Capture("c", intent, new CaptureRequest(10_001));

        Code(result).ShouldBe("amount_too_large");
        ledger.FindIntent(intent)!.Status.ShouldBe("requires_capture");
    }

    [Fact]
    public void A_cancelled_authorisation_cannot_be_captured_and_a_captured_one_cannot_be_cancelled()
    {
        string cancelled = Authorised(key: "a1");
        string captured = Authorised(key: "a2");

        ledger.Cancel("x1", cancelled).Reply.Status.ShouldBe(200);
        ledger.Capture("x2", captured, new CaptureRequest(null));

        Code(ledger.Capture("x3", cancelled, new CaptureRequest(null))).ShouldBe("payment_intent_unexpected_state");
        Code(ledger.Cancel("x4", captured)).ShouldBe("payment_intent_unexpected_state");
    }

    [Fact]
    public void Refunds_add_up_to_at_most_what_was_captured()
    {
        string intent = Authorised(amount: 10_000);
        ledger.Capture("c", intent, new CaptureRequest(8_000));

        ledger.Refund("r1", new RefundRequest(intent, 3_000, null)).Reply.Status.ShouldBe(200);
        ledger.Refund("r2", new RefundRequest(intent, 5_000, null)).Reply.Status.ShouldBe(200);
        Execution tooMuch = ledger.Refund("r3", new RefundRequest(intent, 1, null));

        Code(tooMuch).ShouldBe("amount_too_large");
        ledger.FindIntent(intent)!.AmountRefunded.ShouldBe(8_000);
        ledger.Refunds().Sum(static r => r.Amount).ShouldBe(8_000);
    }

    [Fact]
    public void A_refund_without_an_amount_refunds_what_is_left()
    {
        string intent = Authorised(amount: 10_000);
        ledger.Capture("c", intent, new CaptureRequest(null));
        ledger.Refund("r1", new RefundRequest(intent, 2_500, null));

        Execution rest = ledger.Refund("r2", new RefundRequest(intent, null, null));

        rest.Reply.Body.GetProperty("amount").GetInt64().ShouldBe(7_500);
    }

    [Fact]
    public void An_uncaptured_authorisation_cannot_be_refunded()
    {
        string intent = Authorised();

        Code(ledger.Refund("r", new RefundRequest(intent, 100, null))).ShouldBe("payment_intent_unexpected_state");
    }

    [Fact]
    public void A_repeat_under_the_same_key_returns_the_recorded_answer_and_changes_nothing()
    {
        string intent = Authorised(amount: 10_000);

        Execution first = ledger.Capture("cap", intent, new CaptureRequest(4_000));
        Execution second = ledger.Capture("cap", intent, new CaptureRequest(4_000));

        first.Replayed.ShouldBeFalse();
        second.Replayed.ShouldBeTrue();
        second.Reply.ShouldBe(first.Reply);
        ledger.FindIntent(intent)!.AmountReceived.ShouldBe(4_000);
    }

    [Fact]
    public void A_repeated_refund_is_one_refund()
    {
        string intent = Authorised(amount: 10_000);
        ledger.Capture("cap", intent, new CaptureRequest(null));

        ledger.Refund("ref", new RefundRequest(intent, 1_000, null));
        ledger.Refund("ref", new RefundRequest(intent, 1_000, null)).Replayed.ShouldBeTrue();

        ledger.Refunds().Count.ShouldBe(1);
        ledger.FindIntent(intent)!.AmountRefunded.ShouldBe(1_000);
    }

    [Fact]
    public void A_repeated_decline_is_still_a_decline_and_is_recorded_for_the_lookup()
    {
        string token = Token("4000000000000002");

        ledger.Authorise("a", Authorisation(token));
        Execution again = ledger.Authorise("a", Authorisation(token));

        again.Replayed.ShouldBeTrue();
        again.Reply.Status.ShouldBe(402);
        ledger.Find("a")!.Reply.Status.ShouldBe(402);
    }

    [Fact]
    public void The_same_key_for_a_different_request_is_refused_not_answered_with_the_first_result()
    {
        string intent = Authorised(amount: 10_000);
        ledger.Capture("cap", intent, new CaptureRequest(4_000));

        Execution other = ledger.Capture("cap", intent, new CaptureRequest(9_000));

        other.Reply.Status.ShouldBe(400);
        Code(other).ShouldBe("idempotency_key_in_use");
        ledger.FindIntent(intent)!.AmountReceived.ShouldBe(4_000);
    }

    [Fact]
    public void A_key_that_was_never_used_has_no_recorded_operation()
    {
        ledger.Find("nobody").ShouldBeNull();
    }

    [Fact]
    public void A_recorded_operation_holds_the_original_status_and_body()
    {
        string intent = Authorised(amount: 10_000);
        ledger.Capture("cap", intent, new CaptureRequest(null));

        Recorded recorded = ledger.Find("cap")!;

        recorded.Type.ShouldBe("capture");
        recorded.Reply.Status.ShouldBe(200);
        recorded.Reply.Body.GetProperty("status").GetString().ShouldBe("succeeded");
        recorded.Reply.Body.ValueKind.ShouldBe(JsonValueKind.Object);
    }

    [Fact]
    public void Attempts_are_counted_per_key()
    {
        ledger.NextAttempt("a").ShouldBe(1);
        ledger.NextAttempt("a").ShouldBe(2);
        ledger.NextAttempt("b").ShouldBe(1);
    }

    [Theory]
    [InlineData(0L)]
    [InlineData(100_000_000L)]
    public void An_amount_outside_the_range_is_refused(long amount)
    {
        Execution result = ledger.Authorise("a", Authorisation(Token(), amount));

        result.Reply.Status.ShouldBe(400);
    }

    [Fact]
    public void Only_manual_capture_is_simulated_and_an_unknown_token_is_not_found()
    {
        string token = Token();

        Code(ledger.Authorise("a", Authorisation(token) with { CaptureMethod = "automatic" })).ShouldBe("parameter_invalid_enum");
        ledger.Authorise("b", Authorisation("tok_nope")).Reply.Status.ShouldBe(404);
    }

    [Fact]
    public void The_card_number_is_not_in_the_cards_printed_form()
    {
        new CardInput("4242424242424242", 12, 2030, "123").ToString().ShouldNotContain("4242");
    }
}
