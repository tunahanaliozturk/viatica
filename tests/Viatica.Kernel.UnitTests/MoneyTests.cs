using System.Globalization;
using System.Text.Json;

namespace Viatica.Kernel.UnitTests;

public sealed class MoneyTests
{
    private static readonly JsonSerializerOptions Options = new(JsonSerializerDefaults.Web);

    [Theory]
    [InlineData("312", "EUR", """{"amount":"312.00","currency":"EUR"}""")]
    [InlineData("0.5", "EUR", """{"amount":"0.50","currency":"EUR"}""")]
    [InlineData("-12.3", "EUR", """{"amount":"-12.30","currency":"EUR"}""")]
    [InlineData("1500", "JPY", """{"amount":"1500","currency":"JPY"}""")]
    [InlineData("1.234", "KWD", """{"amount":"1.234","currency":"KWD"}""")]
    public void Writes_the_amount_as_a_string_with_the_currency_minor_units(string amount, string currency, string json)
    {
        var money = new Money(decimal.Parse(amount, CultureInfo.InvariantCulture), currency);

        JsonSerializer.Serialize(money, Options).ShouldBe(json);
        JsonSerializer.Deserialize<Money>(json, Options).ShouldBe(money);
    }

    [Fact]
    public void Round_trips_inside_another_object_regardless_of_property_order()
    {
        var read = JsonSerializer.Deserialize<Priced>("""{"price":{"currency":"EUR","amount":"104.90"}}""", Options)!;

        read.Price.ShouldBe(Money.Eur(104.90m));
        JsonSerializer.Serialize(read, Options).ShouldBe("""{"price":{"amount":"104.90","currency":"EUR"}}""");
    }

    [Theory]
    [InlineData("""{"amount":312.00,"currency":"EUR"}""")]          // a number, which a client would parse as a float
    [InlineData("""{"amount":"312","currency":"EUR"}""")]           // too few decimals for EUR
    [InlineData("""{"amount":"312.0","currency":"EUR"}""")]
    [InlineData("""{"amount":"312.001","currency":"EUR"}""")]       // more precision than the currency has
    [InlineData("""{"amount":"312.00","currency":"eur"}""")]        // not an ISO code
    [InlineData("""{"amount":"312.00","currency":"EURO"}""")]
    [InlineData("""{"amount":"312.00"}""")]
    [InlineData("""{"currency":"EUR"}""")]
    [InlineData("""{"amount":"312.00","currency":"EUR","rate":1}""")]
    [InlineData("""{"amount":"1.00","amount":"2.00","currency":"EUR"}""")]
    [InlineData("""{"amount":"0312.00","currency":"EUR"}""")]
    [InlineData("""{"amount":"+312.00","currency":"EUR"}""")]
    [InlineData("""{"amount":"3.12e2","currency":"EUR"}""")]
    [InlineData("""{"amount":" 312.00","currency":"EUR"}""")]
    [InlineData("""{"amount":"1,312.00","currency":"EUR"}""")]
    [InlineData("""{"amount":"312.00","currency":null}""")]
    [InlineData("""{"amount":"1500.00","currency":"JPY"}""")]       // JPY has no minor unit
    [InlineData("\"312.00 EUR\"")]
    public void Refuses_anything_but_the_exact_shape(string json) =>
        Should.Throw<JsonException>(() => JsonSerializer.Deserialize<Money>(json, Options));

    [Fact]
    public void Adds_and_subtracts_within_one_currency()
    {
        (Money.Eur(100.10m) + Money.Eur(0.90m)).ShouldBe(Money.Eur(101m));
        (Money.Eur(100m) - Money.Eur(0.01m)).ShouldBe(Money.Eur(99.99m));
        (Money.Eur(104.90m) * 3).ShouldBe(Money.Eur(314.70m));
        (Money.Eur(2m) > Money.Eur(1.99m)).ShouldBeTrue();
    }

    [Fact]
    public void Refuses_to_mix_currencies()
    {
        Money euros = Money.Eur(10m);
        var dollars = new Money(10m, "USD");

        Should.Throw<InvalidOperationException>(() => euros + dollars).Message.ShouldContain("EUR and USD");
        Should.Throw<InvalidOperationException>(() => euros - dollars);
        Should.Throw<InvalidOperationException>(() => euros < dollars);
        Should.Throw<InvalidOperationException>(() => euros.CompareTo(dollars));
    }

    [Fact]
    public void Cannot_hold_more_decimals_than_the_currency_has()
    {
        Should.Throw<ArgumentException>(() => Money.Eur(1.005m));
        Should.Throw<ArgumentException>(() => new Money(1.5m, "JPY"));
        Should.NotThrow(() => new Money(1.005m, "KWD"));
    }

    [Theory]
    [InlineData("eur")]
    [InlineData("EU")]
    [InlineData("")]
    [InlineData("EUR\n")]      // $ in a .NET regex also matches before a final newline
    [InlineData("EUR\r\n")]
    [InlineData("\nEUR")]
    [InlineData("E\nR")]
    [InlineData("EUR ")]
    [InlineData("ÉUR")]
    public void Needs_an_upper_case_three_letter_code(string currency) =>
        Should.Throw<Exception>(() => new Money(1m, currency));

    [Fact]
    public void Equal_amounts_with_different_scale_are_equal()
    {
        Money.Eur(312m).ShouldBe(Money.Eur(312.00m));
        Money.Eur(312m).GetHashCode().ShouldBe(Money.Eur(312.00m).GetHashCode());
        Money.Eur(312m).ToString().ShouldBe("312.00 EUR");
    }

    private sealed record Priced(Money Price);
}
