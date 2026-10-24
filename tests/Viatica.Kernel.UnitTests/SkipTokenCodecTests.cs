using System.Buffers.Text;
using Microsoft.AspNetCore.WebUtilities;
using Viatica.Kernel.Web;

namespace Viatica.Kernel.UnitTests;

public sealed class SkipTokenCodecTests
{
    private static readonly SkipTokenCodec Codec = new(Keys.SkipToken);

    private static string Query(string path, string query) =>
        SkipTokenCodec.NormalizeQuery(path, QueryHelpers.ParseQuery(query));

    private static readonly string Lisbon = Query("/stays", "?$filter=city eq 'lisbon'&api-version=2026-11-01");

    [Fact]
    public void Round_trips_a_position()
    {
        string[] position = ["312.50", "O'Brien Town", "ünïcode ✓", ""];
        string token = Codec.Encode(position, Lisbon);

        Codec.TryDecode(token, Lisbon, out string[] read).ShouldBeTrue();
        read.ShouldBe(position);
        token.ShouldNotContain("+");
        token.ShouldNotContain("/");
        token.ShouldNotContain("=");
    }

    [Fact]
    public void A_token_replayed_against_another_query_fails()
    {
        string token = Codec.Encode(["80", "17"], Lisbon);

        Codec.TryDecode(token, Query("/stays", "?$filter=city eq 'porto'&api-version=2026-11-01"), out _).ShouldBeFalse();
        Codec.TryDecode(token, Query("/cars", "?$filter=city eq 'lisbon'&api-version=2026-11-01"), out _).ShouldBeFalse();
        Codec.TryDecode(token, Query("/stays", "?$filter=city eq 'lisbon'&api-version=2027-01-01"), out _).ShouldBeFalse();
        Codec.TryDecode(token, Query("/stays", "?$filter=city eq 'lisbon'&api-version=2026-11-01&$orderby=id"), out _).ShouldBeFalse();
    }

    [Fact]
    public void Every_flipped_bit_is_detected()
    {
        string token = Codec.Encode(["80", "17"], Lisbon);
        byte[] bytes = Base64Url.DecodeFromChars(token);

        for (int i = 0; i < bytes.Length * 8; i++)
        {
            byte[] tampered = (byte[])bytes.Clone();
            tampered[i / 8] ^= (byte)(1 << (i % 8));
            Codec.TryDecode(Base64Url.EncodeToString(tampered), Lisbon, out _).ShouldBeFalse($"bit {i}");
        }
    }

    [Fact]
    public void A_token_signed_with_another_key_fails()
    {
        string forged = new SkipTokenCodec(Keys.Random()).Encode(["0"], Lisbon);

        Codec.TryDecode(forged, Lisbon, out _).ShouldBeFalse();
    }

    [Theory]
    [InlineData("")]
    [InlineData("not base64 !")]
    [InlineData("AQ")]
    [InlineData("AAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAA")]
    public void Garbage_is_refused_without_throwing(string token) =>
        Codec.TryDecode(token, Lisbon, out _).ShouldBeFalse();

    [Fact]
    public void An_oversized_token_is_refused_before_any_work() =>
        Codec.TryDecode(new string('A', SkipTokenCodec.MaxTokenLength + 1), Lisbon, out _).ShouldBeFalse();

    [Fact]
    public void Normalisation_ignores_paging_parameters_case_and_order() =>
        Query("/Stays", "?api-version=2026-11-01&$top=5&$skiptoken=abc&$FILTER=city eq 'lisbon'").ShouldBe(Lisbon);

    [Fact]
    public void Normalisation_cannot_be_fooled_by_moving_a_separator() =>
        Query("/stays", "?a=b%26c").ShouldNotBe(Query("/stays", "?a=b&c"));
}

internal static class Keys
{
    public static readonly byte[] SkipToken = Convert.FromBase64String(SkipTokenBase64);

    public const string SkipTokenBase64 = "dGVzdC1rZXktdGhhdC1pcy1hdC1sZWFzdC0zMi1ieXRlcy1sb25n";

    public static byte[] Random() => System.Security.Cryptography.RandomNumberGenerator.GetBytes(32);
}
