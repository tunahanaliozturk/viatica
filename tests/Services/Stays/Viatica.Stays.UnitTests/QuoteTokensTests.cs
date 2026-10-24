using System.Buffers.Text;
using System.Security.Cryptography;
using System.Text;
using Viatica.Kernel;
using Viatica.Stays.Domain;

namespace Viatica.Stays.UnitTests;

public sealed class QuoteTokensTests
{
    private static readonly DateTimeOffset Now = new(2026, 11, 20, 10, 0, 0, TimeSpan.Zero);
    private static readonly byte[] Key = RandomNumberGenerator.GetBytes(32);

    private readonly QuoteTokens _tokens = new(Key);

    private static QuotePayload Payload(DateTimeOffset? expiresAt = null) => new(
        QuotePayload.CurrentVersion,
        QuotePayload.StayKind,
        Guid.Parse("0192f0c4-7a3b-7cde-8f00-4f7c2b1d9e01"),
        Guid.Parse("0192f0c4-7a3b-7cde-8f00-4f7c2b1d9e02"),
        new DateOnly(2026, 12, 1),
        new DateOnly(2026, 12, 4),
        Quantity: 2,
        Money.Eur(936.00m),
        expiresAt ?? Now + QuoteTokens.Lifetime);

    [Fact]
    public void A_token_reads_back_what_was_issued()
    {
        string token = _tokens.Issue(Payload());

        _tokens.TryRead(token, Now, out QuotePayload? payload, out _).ShouldBeTrue();
        payload.ShouldBe(Payload());
    }

    [Fact]
    public void The_payload_is_the_documented_json_and_readable_without_the_key()
    {
        string token = _tokens.Issue(Payload());

        string json = Encoding.UTF8.GetString(Base64Url.DecodeFromChars(token.Split('.')[0]));
        json.ShouldContain("\"kind\":\"stay\"");
        json.ShouldContain("\"total\":{\"amount\":\"936.00\",\"currency\":\"EUR\"}");
        json.ShouldContain("\"checkIn\":\"2026-12-01\"");
        json.ShouldContain("\"expiresAt\":\"2026-11-20T10:15:00+00:00\"");
    }

    [Fact]
    public void A_token_changed_anywhere_is_invalid()
    {
        string token = _tokens.Issue(Payload());
        int dot = token.IndexOf('.', StringComparison.Ordinal);

        // Every position of the payload and of the MAC: one flipped character must never verify.
        for (int i = 0; i < token.Length; i++)
        {
            if (i == dot)
            {
                continue;
            }

            char replacement = token[i] == 'A' ? 'B' : 'A';
            string tampered = string.Concat(token.AsSpan(0, i), replacement.ToString(), token.AsSpan(i + 1));

            _tokens.TryRead(tampered, Now, out QuotePayload? payload, out QuoteFailure failure)
                .ShouldBeFalse($"position {i}");
            payload.ShouldBeNull();
            failure.ShouldBe(QuoteFailure.Invalid, $"position {i}");
        }
    }

    [Fact]
    public void A_cheaper_total_under_the_old_mac_is_invalid()
    {
        string token = _tokens.Issue(Payload());
        string mac = token.Split('.')[1];
        string cheaper = Base64Url.EncodeToString(
            Encoding.UTF8.GetBytes(Encoding.UTF8.GetString(Base64Url.DecodeFromChars(token.Split('.')[0])).Replace("936.00", "1.00", StringComparison.Ordinal)));

        _tokens.TryRead($"{cheaper}.{mac}", Now, out _, out QuoteFailure failure).ShouldBeFalse();
        failure.ShouldBe(QuoteFailure.Invalid);
    }

    [Fact]
    public void A_token_signed_with_another_key_is_invalid()
    {
        string foreign = new QuoteTokens(RandomNumberGenerator.GetBytes(32)).Issue(Payload());

        _tokens.TryRead(foreign, Now, out _, out QuoteFailure failure).ShouldBeFalse();
        failure.ShouldBe(QuoteFailure.Invalid);
    }

    [Fact]
    public void An_expired_token_is_expired_not_invalid_and_the_boundary_is_expired()
    {
        DateTimeOffset expiry = Now + QuoteTokens.Lifetime;
        string token = _tokens.Issue(Payload(expiry));

        _tokens.TryRead(token, expiry.AddTicks(-1), out _, out _).ShouldBeTrue();

        _tokens.TryRead(token, expiry, out QuotePayload? payload, out QuoteFailure failure).ShouldBeFalse();
        payload.ShouldBeNull();
        failure.ShouldBe(QuoteFailure.Expired);
    }

    [Fact]
    public void A_forged_token_cannot_learn_whether_it_would_have_expired()
    {
        // Past its expiry and forged: only a genuine token earns the answer "expired".
        string token = _tokens.Issue(Payload(Now.AddDays(-1)));
        string forged = token[..^2] + (token[^2] == 'A' ? "B" : "A") + token[^1];

        _tokens.TryRead(forged, Now, out _, out QuoteFailure failure).ShouldBeFalse();
        failure.ShouldBe(QuoteFailure.Invalid);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("no-dot-at-all")]
    [InlineData(".")]
    [InlineData("a.")]
    [InlineData(".b")]
    [InlineData("a.b.c")]
    [InlineData("not base64!.also not")]
    public void Anything_that_is_not_a_token_is_invalid(string? token)
    {
        _tokens.TryRead(token, Now, out _, out QuoteFailure failure).ShouldBeFalse();
        failure.ShouldBe(QuoteFailure.Invalid);
    }

    [Fact]
    public void An_oversized_token_is_refused_before_any_work()
    {
        _tokens.TryRead(new string('A', 100_000) + "." + new string('A', 43), Now, out _, out QuoteFailure failure).ShouldBeFalse();
        failure.ShouldBe(QuoteFailure.Invalid);
    }

    [Fact]
    public void A_genuine_token_of_another_kind_or_version_is_invalid()
    {
        _tokens.TryRead(_tokens.Issue(Payload() with { Kind = "car" }), Now, out _, out QuoteFailure kind).ShouldBeFalse();
        _tokens.TryRead(_tokens.Issue(Payload() with { V = 2 }), Now, out _, out QuoteFailure version).ShouldBeFalse();

        kind.ShouldBe(QuoteFailure.Invalid);
        version.ShouldBe(QuoteFailure.Invalid);
    }

    [Fact]
    public void A_short_key_is_refused()
    {
        Should.Throw<ArgumentException>(() => new QuoteTokens(new byte[31]));
    }
}
