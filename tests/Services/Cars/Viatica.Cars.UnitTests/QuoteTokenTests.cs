using System.Buffers.Text;
using System.Security.Cryptography;
using System.Text;
using Microsoft.Extensions.Time.Testing;
using Viatica.Cars.Application.Quotes;
using Viatica.Kernel;

namespace Viatica.Cars.UnitTests;

public sealed class QuoteTokenTests
{
    private static readonly byte[] Key = [.. Enumerable.Range(1, 32).Select(static i => (byte)i)];

    private static readonly DateTimeOffset PickUp = new(2026, 12, 7, 9, 0, 0, TimeSpan.FromHours(1));

    private readonly FakeTimeProvider clock = new(Fixtures.Now);

    private QuoteTokenCodec Codec => new(Key, clock);

    private string Issue(QuoteTokenCodec? codec = null) =>
        (codec ?? Codec).Issue(Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), PickUp, PickUp.AddDays(2), 30, Money.Eur(135m)).Token;

    [Fact]
    public void A_fresh_token_reads_back_what_was_quoted()
    {
        Guid location = Guid.NewGuid();
        Guid category = Guid.NewGuid();
        Guid dropOff = Guid.NewGuid();
        string token = Codec.Issue(location, category, dropOff, PickUp, PickUp.AddDays(2), 27, Money.Eur(135m)).Token;

        QuoteTokenStatus status = Codec.Read(token, out CarQuote? quote);

        status.ShouldBe(QuoteTokenStatus.Valid);
        quote.ShouldNotBeNull();
        quote.LocationId.ShouldBe(location);
        quote.CategoryId.ShouldBe(category);
        quote.DropOffLocationId.ShouldBe(dropOff);
        quote.PickUpAt.ShouldBe(PickUp);
        quote.PickUpAt.Offset.ShouldBe(PickUp.Offset);
        quote.DriverAge.ShouldBe(27);
        quote.Total.ShouldBe(Money.Eur(135m));
        quote.Kind.ShouldBe("car");
        quote.ExpiresAt.ShouldBe(Fixtures.Now + TimeSpan.FromMinutes(15));
    }

    [Fact]
    public void The_payload_is_readable_json_so_booking_can_show_the_item_without_calling_cars()
    {
        string token = Issue();

        string json = Encoding.UTF8.GetString(Base64Url.DecodeFromChars(token.Split('.')[0]));

        json.ShouldContain("\"kind\":\"car\"");
        json.ShouldContain("\"total\":{\"amount\":\"135.00\",\"currency\":\"EUR\"}");
    }

    [Fact]
    public void A_token_is_good_until_the_fifteenth_minute()
    {
        string token = Issue();

        clock.Advance(TimeSpan.FromMinutes(15) - TimeSpan.FromSeconds(1));
        Codec.Read(token, out _).ShouldBe(QuoteTokenStatus.Valid);

        clock.Advance(TimeSpan.FromSeconds(1));
        Codec.Read(token, out CarQuote? quote).ShouldBe(QuoteTokenStatus.Expired);
        quote.ShouldNotBeNull();
    }

    [Fact]
    public void Changing_any_character_of_the_payload_or_the_signature_is_caught()
    {
        string token = Issue();
        int separator = token.IndexOf('.', StringComparison.Ordinal);

        // The last character of each half carries padding bits that decode to the same bytes, so it is skipped.
        for (int i = 0; i < token.Length - 1; i++)
        {
            if (i == separator || i == separator - 1)
            {
                continue;
            }

            char replacement = token[i] == 'A' ? 'B' : 'A';
            string tampered = string.Concat(token.AsSpan(0, i), replacement.ToString(), token.AsSpan(i + 1));

            Codec.Read(tampered, out _).ShouldBe(QuoteTokenStatus.Invalid, $"position {i}");
        }
    }

    [Fact]
    public void A_payload_edited_to_a_lower_price_does_not_verify_under_the_old_signature()
    {
        string token = Issue();
        string[] parts = token.Split('.');
        string json = Encoding.UTF8.GetString(Base64Url.DecodeFromChars(parts[0])).Replace("135.00", "1.00", StringComparison.Ordinal);

        string forged = Base64Url.EncodeToString(Encoding.UTF8.GetBytes(json)) + "." + parts[1];

        Codec.Read(forged, out _).ShouldBe(QuoteTokenStatus.Invalid);
    }

    [Fact]
    public void A_token_signed_with_another_key_is_invalid()
    {
        var other = new QuoteTokenCodec([.. Key.Reverse()], clock);

        Codec.Read(Issue(other), out _).ShouldBe(QuoteTokenStatus.Invalid);
    }

    [Fact]
    public void A_stay_quote_signed_with_the_same_key_is_still_not_a_car_quote()
    {
        // The services do not share a key, but the kind is checked too: defence in depth, and the test of it.
        string payload = $$"""{"v":1,"kind":"stay","expiresAt":"{{(Fixtures.Now + TimeSpan.FromMinutes(5)):O}}"}""";
        byte[] bytes = Encoding.UTF8.GetBytes(payload);
        string token = Base64Url.EncodeToString(bytes) + "." + Base64Url.EncodeToString(HMACSHA256.HashData(Key, bytes));

        Codec.Read(token, out _).ShouldBe(QuoteTokenStatus.Invalid);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("no-dot")]
    [InlineData("a.b.c")]
    [InlineData("!!!.???")]
    [InlineData(".")]
    public void Garbage_is_invalid_not_an_exception(string? token) =>
        Codec.Read(token, out _).ShouldBe(QuoteTokenStatus.Invalid);

    [Fact]
    public void An_oversized_token_is_refused_before_any_work_is_done() =>
        Codec.Read(new string('A', 5000) + "." + new string('A', 5000), out _).ShouldBe(QuoteTokenStatus.Invalid);

    [Fact]
    public void A_short_key_is_refused() =>
        Should.Throw<ArgumentException>(() => new QuoteTokenCodec(new byte[31], clock));
}
