using Viatica.Contracts;
using Viatica.Kernel;
using Viatica.Payments.Application.Operations;

namespace Viatica.Payments.UnitTests;

/// <summary>A price on the wire is untrusted: whatever is in it, the handler gets money or null and never an exception.</summary>
public sealed class MoneyConversionTests
{
    [Fact]
    public void A_good_price_becomes_money()
    {
        PaymentEvents.ToMoney(new Price(312.50m, "EUR")).ShouldBe(Money.Eur(312.50m));
    }

    [Theory]
    [InlineData("euro")]
    [InlineData("eur")]
    [InlineData("")]
    [InlineData("E1R")]
    public void A_currency_that_is_not_three_upper_case_letters_is_not_money(string currency)
    {
        PaymentEvents.ToMoney(new Price(10m, currency)).ShouldBeNull();
    }

    [Fact]
    public void An_amount_with_more_decimals_than_the_currency_has_is_not_money()
    {
        PaymentEvents.ToMoney(new Price(10.005m, "EUR")).ShouldBeNull();
        PaymentEvents.ToMoney(new Price(10.5m, "JPY")).ShouldBeNull();
    }

    [Fact]
    public void A_missing_currency_is_not_money()
    {
        PaymentEvents.ToMoney(new Price(10m, null!)).ShouldBeNull();
    }
}
