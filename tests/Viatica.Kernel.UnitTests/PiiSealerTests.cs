using System.Buffers.Text;
using System.Security.Cryptography;
using System.Text;
using Viatica.Kernel.Pii;

namespace Viatica.Kernel.UnitTests;

public sealed class PiiSealerTests
{
    private static readonly Guid Trip = Guid.Parse("0192f0c4-7a3b-7cde-8f00-4f7c2b1d9e01");
    private static readonly Guid Item = Guid.Parse("0192f0c4-7a3b-7cde-8f00-4f7c2b1d9e02");

    private static readonly PassengerDetails[] Passengers =
    [
        new(PassengerType.Adult, "Ayse", "Yilmaz", new DateOnly(1988, 4, 2), "U12345678"),
        new(PassengerType.Infant, "Deniz", "Yilmaz", new DateOnly(2026, 2, 14)),
    ];

    private static PiiSealer NewSealer() => new(RandomNumberGenerator.GetBytes(PiiSealer.KeyLength));

    [Fact]
    public void A_sealed_value_opens_to_what_was_sealed()
    {
        PiiSealer sealer = NewSealer();

        string sealedValue = sealer.Seal("passport data"u8, "trip:item");

        Encoding.UTF8.GetString(sealer.Open(sealedValue, "trip:item")).ShouldBe("passport data");
    }

    [Fact]
    public void The_wire_format_is_base64url_of_nonce_ciphertext_and_tag()
    {
        string sealedValue = NewSealer().Seal("12345"u8, "aad");

        // 12-byte nonce + 5 bytes of ciphertext (GCM keeps the length) + 16-byte tag.
        Base64Url.DecodeFromChars(sealedValue).Length.ShouldBe(12 + 5 + 16);
        sealedValue.ShouldNotContain("+");
        sealedValue.ShouldNotContain("/");
        sealedValue.ShouldNotContain("=");
    }

    [Fact]
    public void The_same_plaintext_seals_differently_every_time()
    {
        PiiSealer sealer = NewSealer();

        sealer.Seal("same"u8, "aad").ShouldNotBe(sealer.Seal("same"u8, "aad"));
    }

    [Fact]
    public void The_plaintext_is_not_visible_in_the_sealed_value()
    {
        string sealedValue = NewSealer().Seal("Ayse Yilmaz"u8, "aad");

        Encoding.UTF8.GetString(Base64Url.DecodeFromChars(sealedValue)).ShouldNotContain("Yilmaz");
    }

    [Fact]
    public void Every_changed_bit_is_rejected()
    {
        PiiSealer sealer = NewSealer();
        byte[] original = Base64Url.DecodeFromChars(sealer.Seal("short secret"u8, "aad"));

        // Nonce, ciphertext and tag are all covered: flipping any one bit anywhere must fail authentication.
        for (int bit = 0; bit < original.Length * 8; bit++)
        {
            byte[] tampered = [.. original];
            tampered[bit / 8] ^= (byte)(1 << (bit % 8));

            sealer.TryOpen(Base64Url.EncodeToString(tampered), "aad", out _).ShouldBeFalse($"bit {bit} went unnoticed");
        }
    }

    [Fact]
    public void Different_associated_data_is_rejected()
    {
        PiiSealer sealer = NewSealer();
        string sealedValue = sealer.Seal("secret"u8, "trip-1:item-1");

        sealer.TryOpen(sealedValue, "trip-2:item-1", out _).ShouldBeFalse();
        sealer.TryOpen(sealedValue, "trip-1:item-2", out _).ShouldBeFalse();
        sealer.TryOpen(sealedValue, "", out _).ShouldBeFalse();
    }

    [Fact]
    public void Another_key_is_rejected()
    {
        string sealedValue = NewSealer().Seal("secret"u8, "aad");

        NewSealer().TryOpen(sealedValue, "aad", out _).ShouldBeFalse();
    }

    [Theory]
    [InlineData("")]
    [InlineData("AAAA")]
    [InlineData("not base64 at all!")]
    [InlineData("AAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAA=")]
    [InlineData("AAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAA")]
    public void Garbage_and_truncated_values_are_rejected_without_throwing(string value) =>
        NewSealer().TryOpen(value, "aad", out _).ShouldBeFalse();

    [Fact]
    public void Open_throws_for_a_value_that_does_not_authenticate() =>
        Should.Throw<CryptographicException>(() => NewSealer().Open(NewSealer().Seal("x"u8, "aad"), "aad"));

    [Theory]
    [InlineData(0)]
    [InlineData(16)]
    [InlineData(31)]
    [InlineData(33)]
    public void Only_a_32_byte_key_is_accepted(int length) =>
        Should.Throw<ArgumentException>(() => new PiiSealer(new byte[length]));

    [Fact]
    public void A_key_in_configuration_is_read_from_base64()
    {
        byte[] key = RandomNumberGenerator.GetBytes(PiiSealer.KeyLength);
        string sealedValue = new PiiSealer(key).Seal("secret"u8, "aad");

        PiiSealer.FromBase64(Convert.ToBase64String(key)).Open(sealedValue, "aad").ShouldBe("secret"u8.ToArray());
        Should.Throw<ArgumentException>(() => PiiSealer.FromBase64("%%%"));
    }

    [Fact]
    public void The_error_for_a_bad_key_does_not_repeat_the_key()
    {
        ArgumentException error = Should.Throw<ArgumentException>(() => PiiSealer.FromBase64(Convert.ToBase64String("hunter2-too-short"u8)));

        error.Message.ShouldNotContain("hunter2");
    }

    [Fact]
    public void Passengers_round_trip_for_their_trip_and_item()
    {
        PiiSealer sealer = NewSealer();

        string sealedValue = sealer.SealPassengers(Passengers, Trip, Item);

        sealer.TryOpenPassengers(sealedValue, Trip, Item, out IReadOnlyList<PassengerDetails> opened).ShouldBeTrue();
        opened.ShouldBe(Passengers);
    }

    [Fact]
    public void A_passenger_list_replayed_for_another_trip_or_item_is_rejected()
    {
        PiiSealer sealer = NewSealer();
        string sealedValue = sealer.SealPassengers(Passengers, Trip, Item);

        sealer.TryOpenPassengers(sealedValue, Guid.CreateVersion7(), Item, out _).ShouldBeFalse();
        sealer.TryOpenPassengers(sealedValue, Trip, Guid.CreateVersion7(), out _).ShouldBeFalse();
    }

    [Fact]
    public void The_associated_data_is_the_one_the_design_names() =>
        PassengerSealing.AssociatedData(Trip, Item).ShouldBe("0192f0c47a3b7cde8f004f7c2b1d9e01:0192f0c47a3b7cde8f004f7c2b1d9e02");

    [Fact]
    public void Something_that_authenticates_but_is_not_a_passenger_list_is_rejected()
    {
        PiiSealer sealer = NewSealer();

        sealer.TryOpenPassengers(sealer.Seal("{}"u8, "aad"), "aad", out _).ShouldBeFalse();
        sealer.TryOpenPassengers(sealer.Seal("[]"u8, "aad"), "aad", out _).ShouldBeFalse();
        sealer.TryOpenPassengers(sealer.Seal("not json"u8, "aad"), "aad", out _).ShouldBeFalse();
    }

    [Fact]
    public void A_passenger_prints_nothing_about_the_person()
    {
        string printed = Passengers[0].ToString() + $"{Passengers[0]}";

        printed.ShouldNotContain("Ayse");
        printed.ShouldNotContain("Yilmaz");
        printed.ShouldNotContain("1988");
        printed.ShouldNotContain("U12345678");
    }
}
