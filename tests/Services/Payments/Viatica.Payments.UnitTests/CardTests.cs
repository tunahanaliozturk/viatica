using Microsoft.Extensions.Time.Testing;
using Moongazing.OrionGuard.Core;
using Viatica.Payments.Application.Features.TokeniseCard;
using Viatica.Payments.Domain.Cards;

namespace Viatica.Payments.UnitTests;

public sealed class CardTests
{
    private static readonly DateOnly Today = new(2026, 11, 2);

    [Theory]
    [InlineData("4242424242424242", "visa", "4242")]
    [InlineData("4242 4242 4242 4242", "visa", "4242")]
    [InlineData("4242-4242-4242-4242", "visa", "4242")]
    [InlineData("5555555555554444", "mastercard", "4444")]
    [InlineData("2223003122003222", "mastercard", "3222")]
    [InlineData("378282246310005", "amex", "0005")]
    [InlineData("6011111111111117", "unknown", "1117")]
    public void A_number_is_read_the_way_people_type_it_and_the_brand_comes_from_the_leading_digits(string text, string brand, string last4)
    {
        CardNumber.TryParse(text, out CardNumber? number).ShouldBeTrue();

        number!.Brand.ShouldBe(brand);
        number.Last4.ShouldBe(last4);
    }

    [Theory]
    [InlineData("4242424242424241")] // one digit off: the Luhn check
    [InlineData("424242424243")] // right length, fails the check
    [InlineData("4242 4242 4242 424a")]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData(null)]
    [InlineData("42424242424242424242")] // too long
    public void A_number_that_is_not_a_card_number_is_refused(string? text)
    {
        CardNumber.TryParse(text, out CardNumber? number).ShouldBeFalse();

        number.ShouldBeNull();
    }

    [Fact]
    public void A_card_prints_as_what_it_is_and_never_as_what_it_holds()
    {
        CardNumber.TryParse("4242424242424242", out CardNumber? number).ShouldBeTrue();
        CardDetails card = CardDetails.TryCreate("4242424242424242", 12, 2030, "123", Today)!;

        // Every way a framework formats an object: ToString, interpolation, string.Format and a collection's printout.
        string[] printed =
        [
            number!.ToString(),
            $"{number}",
            string.Format(System.Globalization.CultureInfo.InvariantCulture, "{0}", number),
            card.ToString(),
            $"{card}",
            string.Join(",", new object[] { number, card }),
        ];

        foreach (string text in printed)
        {
            text.ShouldNotContain("4242");
            text.ShouldNotContain("123");
        }

        number.Reveal().ShouldBe("4242424242424242", "the one way to the digits is deliberate and named");
    }

    [Fact]
    public void A_good_card_has_no_problems()
    {
        CardDetails.Check("4242424242424242", 12, 2030, "123", Today).ShouldBeEmpty();
        CardDetails.Check("378282246310005", 1, 2027, "1234", Today).ShouldBeEmpty();
        CardDetails.Check("4242424242424242", 11, 2026, null, Today).ShouldBeEmpty("a card is good through its expiry month, and a missing code is allowed");
    }

    [Fact]
    public void Every_problem_is_reported_at_once()
    {
        IReadOnlyList<CardProblem> problems = CardDetails.Check("1234", 13, 1999, "12", Today);

        problems.ShouldBe(
        [
            new CardProblem(CardProblemCodes.InvalidCardNumber, "number"),
            new CardProblem(CardProblemCodes.InvalidExpiry, "expMonth"),
            new CardProblem(CardProblemCodes.InvalidExpiry, "expYear"),
            new CardProblem(CardProblemCodes.InvalidCvc, "cvc"),
        ]);
    }

    [Fact]
    public void A_card_that_expired_last_month_is_expired_and_one_that_expires_this_month_is_not()
    {
        CardDetails.Check("4242424242424242", 10, 2026, null, Today).ShouldBe([new CardProblem(CardProblemCodes.CardExpired, "expYear")]);
        CardDetails.Check("4242424242424242", 11, 2026, null, Today).ShouldBeEmpty();
    }

    [Theory]
    [InlineData("12")]
    [InlineData("12345")]
    [InlineData("12a")]
    public void A_security_code_is_three_or_four_digits(string cvc)
    {
        CardDetails.Check("4242424242424242", 12, 2030, cvc, Today).ShouldBe([new CardProblem(CardProblemCodes.InvalidCvc, "cvc")]);
    }

    [Fact]
    public void A_year_far_in_the_future_is_not_a_card()
    {
        CardDetails.Check("4242424242424242", 12, 2090, null, Today).ShouldBe([new CardProblem(CardProblemCodes.InvalidExpiry, "expYear")]);
    }

    [Fact]
    public async Task The_validator_reports_a_code_and_a_field_per_problem_and_never_the_value()
    {
        var validator = new TokeniseCardValidator(new FakeTimeProvider(new DateTimeOffset(2026, 11, 2, 8, 0, 0, TimeSpan.Zero)));

        GuardResult result = await validator.ValidateAsync(new TokeniseCardCommand("1234567890123456", 13, 1999, "12"), CancellationToken.None);

        result.IsInvalid.ShouldBeTrue();
        result.Errors.Select(static e => (e.ErrorCode, e.ParameterName)).ShouldBe(
        [
            (CardProblemCodes.InvalidCardNumber, "Number"),
            (CardProblemCodes.InvalidExpiry, "ExpMonth"),
            (CardProblemCodes.InvalidExpiry, "ExpYear"),
            (CardProblemCodes.InvalidCvc, "Cvc"),
        ]);
        result.Errors.ShouldAllBe(e => !e.Message.Contains("1234567890123456") && !e.Message.Contains("1999") && !e.Message.Contains("12 "));
    }

    [Fact]
    public async Task The_validator_accepts_a_good_card()
    {
        var validator = new TokeniseCardValidator(new FakeTimeProvider(new DateTimeOffset(2026, 11, 2, 8, 0, 0, TimeSpan.Zero)));

        GuardResult result = await validator.ValidateAsync(new TokeniseCardCommand("4242 4242 4242 4242", 12, 2030, "123"), CancellationToken.None);

        result.IsInvalid.ShouldBeFalse();
    }

    [Fact]
    public void The_command_prints_as_its_name()
    {
        new TokeniseCardCommand("4242424242424242", 12, 2030, "123").ToString().ShouldBe("TokeniseCardCommand");
    }
}
