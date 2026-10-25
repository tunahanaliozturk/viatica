using System.Buffers.Text;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Moongazing.OrionGuard.Core;
using Moongazing.OrionGuard.DependencyInjection;
using Viatica.Booking.Application;
using Viatica.Booking.Application.Features.Trips;
using Viatica.Booking.Application.Features.Checkout;
using Viatica.Booking.Domain;
using Viatica.Kernel;
using Viatica.Kernel.Pii;

namespace Viatica.Booking.UnitTests;

/// <summary>Quote tokens read without being trusted, traveller data sealed, and the validators that guard the edge.</summary>
public sealed class ApplicationTests
{
    private static readonly JsonSerializerOptions Web = new(JsonSerializerDefaults.Web);

    private static string Token(object payload) =>
        Base64Url.EncodeToString(JsonSerializer.SerializeToUtf8Bytes(payload, Web)) + "." + Base64Url.EncodeToString(RandomNumberGenerator.GetBytes(32));

    private static readonly object StayPayload = new
    {
        v = 1,
        kind = "stay",
        propertyId = Guid.NewGuid(),
        roomTypeId = Guid.NewGuid(),
        checkIn = "2026-12-01",
        checkOut = "2026-12-04",
        quantity = 2,
        total = new { amount = "936.00", currency = "EUR" },
        expiresAt = "2026-11-20T10:15:00+00:00",
    };

    // The quote preview.

    [Fact]
    public void A_stay_quote_is_read_for_its_dates_rooms_price_and_expiry()
    {
        QuotePreview.TryRead(Token(StayPayload), out QuotePreview? preview).ShouldBeTrue();

        preview!.Kind.ShouldBe(ItemKind.Stay);
        preview.Quantity.ShouldBe(2);
        preview.Total.ShouldBe(Money.Eur(936m));
        preview.ExpiresAt.ShouldBe(new DateTimeOffset(2026, 11, 20, 10, 15, 0, TimeSpan.Zero));
        preview.Description.ShouldBe("Stay 2026-12-01 to 2026-12-04, 2 room(s)");
    }

    [Fact]
    public void A_car_quote_is_read_for_its_times_driver_and_price()
    {
        string token = Token(new
        {
            v = 1,
            kind = "car",
            locationId = Guid.NewGuid(),
            categoryId = Guid.NewGuid(),
            dropOffLocationId = Guid.NewGuid(),
            pickUpAt = "2026-12-01T10:00:00+01:00",
            dropOffAt = "2026-12-04T10:00:00+01:00",
            driverAge = 27,
            total = new { amount = "180.50", currency = "EUR" },
            expiresAt = "2026-11-20T10:15:00+00:00",
        });

        QuotePreview.TryRead(token, out QuotePreview? preview).ShouldBeTrue();

        preview!.Kind.ShouldBe(ItemKind.Car);
        preview.DriverAge.ShouldBe(27);
        preview.Total.ShouldBe(Money.Eur(180.50m));
        preview.Quantity.ShouldBe(1);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("no-dot-at-all")]
    [InlineData("two.dots.here")]
    [InlineData("!!!.AAAA")]
    [InlineData("e30.AAAA")]
    public void Anything_that_is_not_a_readable_quote_is_refused(string? token) =>
        QuotePreview.TryRead(token, out _).ShouldBeFalse();

    [Fact]
    public void A_quote_that_is_unreadable_in_its_details_is_refused()
    {
        QuotePreview.TryRead(Token(new { v = 2, kind = "stay" }), out _).ShouldBeFalse("another version");
        QuotePreview.TryRead(Token(new { v = 1, kind = "train" }), out _).ShouldBeFalse("another kind");
        QuotePreview.TryRead(Token(new { v = 1, kind = "stay", checkIn = "2026-12-04", checkOut = "2026-12-01", quantity = 1, total = new { amount = "1.00", currency = "EUR" }, expiresAt = "2026-11-20T10:15:00+00:00" }), out _)
            .ShouldBeFalse("it ends before it starts");
        QuotePreview.TryRead(Token(new { v = 1, kind = "stay", checkIn = "2026-12-01", checkOut = "2026-12-04", quantity = 0, total = new { amount = "1.00", currency = "EUR" }, expiresAt = "2026-11-20T10:15:00+00:00" }), out _)
            .ShouldBeFalse("no rooms");
        QuotePreview.TryRead(Token(new { v = 1, kind = "stay", checkIn = "2026-12-01", checkOut = "2026-12-04", quantity = 1, total = new { amount = "1.005", currency = "EUR" }, expiresAt = "2026-11-20T10:15:00+00:00" }), out _)
            .ShouldBeFalse("a price with more places than a cent");
        QuotePreview.TryRead(new string('a', 3000) + ".AAAA", out _).ShouldBeFalse("longer than any real token");
    }

    // Sealing.

    private static TravellerDetails Details(string given = "Zeynebnur") => new(
        new ContactDetails("zeynebnur@example.test", "+905551112233"),
        [new PassengerDetails(PassengerType.Adult, given, "Karabulutoglu", new DateOnly(1987, 5, 17), "TR8675309X")],
        "pm_test_visa");

    private static TravellerVault NewVault(out byte[] passengerKey, out byte[] storageKey)
    {
        passengerKey = RandomNumberGenerator.GetBytes(32);
        storageKey = RandomNumberGenerator.GetBytes(32);
        return new TravellerVault(passengerKey, storageKey);
    }

    [Fact]
    public void Traveller_data_round_trips_under_the_storage_key_and_opens_for_its_own_trip_only()
    {
        TravellerVault vault = NewVault(out _, out byte[] storageKey);
        Guid trip = Guid.NewGuid();

        string sealedData = vault.Seal(trip, Details());

        sealedData.ShouldNotContain("Karabulutoglu");
        sealedData.ShouldNotContain("pm_test_visa");
        vault.TryOpen(trip, sealedData, out TravellerDetails opened).ShouldBeTrue();
        opened.Passengers.Single().Surname.ShouldBe("Karabulutoglu");
        opened.Contact.Email.ShouldBe("zeynebnur@example.test");
        vault.TryOpen(Guid.NewGuid(), sealedData, out _).ShouldBeFalse("copied onto another trip");
        new TravellerVault(RandomNumberGenerator.GetBytes(32), RandomNumberGenerator.GetBytes(32)).TryOpen(trip, sealedData, out _).ShouldBeFalse("another key");
        vault.TryOpen(trip, sealedData[..^2] + (sealedData[^1] == 'A' ? "BB" : "AA"), out _).ShouldBeFalse("a changed value");
        vault.TryOpen(trip, null, out _).ShouldBeFalse("an erased trip");
        _ = storageKey;
    }

    [Fact]
    public void Two_seals_of_the_same_data_differ_because_the_nonce_is_fresh()
    {
        TravellerVault vault = NewVault(out _, out _);
        Guid trip = Guid.NewGuid();

        vault.Seal(trip, Details()).ShouldNotBe(vault.Seal(trip, Details()));
    }

    [Fact]
    public void The_passenger_list_for_the_airline_opens_with_the_shared_key_for_its_trip_and_item_only()
    {
        TravellerVault vault = NewVault(out byte[] passengerKey, out _);
        Guid trip = Guid.NewGuid();
        Guid item = Guid.NewGuid();

        string sealedList = vault.SealPassengersForFlights(trip, item, Details());

        // Flights opens it with the same helper, the shared key and the same associated data.
        var flights = new PiiSealer(passengerKey);
        flights.TryOpenPassengers(sealedList, trip, item, out IReadOnlyList<PassengerDetails> list).ShouldBeTrue();
        list.Single().GivenName.ShouldBe("Zeynebnur");
        flights.TryOpenPassengers(sealedList, trip, Guid.NewGuid(), out _).ShouldBeFalse("another item");
        flights.TryOpenPassengers(sealedList, Guid.NewGuid(), item, out _).ShouldBeFalse("another trip");
        new PiiSealer(RandomNumberGenerator.GetBytes(32)).TryOpenPassengers(sealedList, trip, item, out _).ShouldBeFalse("another key");
        sealedList.ShouldNotContain("Karabulutoglu");
    }

    [Fact]
    public void The_fingerprint_of_a_checkout_is_the_same_for_the_same_request_and_different_for_any_change()
    {
        TravellerVault vault = NewVault(out _, out _);
        Guid trip = Guid.NewGuid();
        string baseline = vault.Fingerprint(trip, Details());

        vault.Fingerprint(trip, Details()).ShouldBe(baseline);
        vault.Fingerprint(trip, Details("Someone")).ShouldNotBe(baseline);
        vault.Fingerprint(trip, Details() with { PaymentMethodToken = "pm_other" }).ShouldNotBe(baseline);
        vault.Fingerprint(Guid.NewGuid(), Details()).ShouldNotBe(baseline, "a copy cannot be replayed onto another trip");
        baseline.ShouldNotContain("Zeynebnur");
    }

    [Fact]
    public void Printing_traveller_data_says_nothing_about_the_person()
    {
        TravellerDetails details = Details();

        details.ToString().ShouldNotContain("Zeynebnur");
        details.Contact.ToString().ShouldNotContain("example.test");
        $"{details}{details.Passengers[0]}".ShouldNotContain("Karabulutoglu");
    }

    // Validators.

    private static async Task<string[]> Codes<T>(AbstractValidator<T> validator, T request) =>
        [.. (await validator.ValidateAsync(request, CancellationToken.None)).Errors.Select(static e => e.ErrorCode ?? "")];

    private static StartCheckout Checkout(TravellerDetails details, Guid? traveller = null) =>
        new(Guid.NewGuid(), new Caller(traveller ?? Guid.NewGuid(), false, null), details);

    [Fact]
    public async Task A_valid_checkout_passes()
    {
        (await Codes(new StartCheckoutValidator(), Checkout(Details()))).ShouldBeEmpty();
    }

    [Fact]
    public async Task A_checkout_reports_every_broken_field_at_once_and_never_the_values()
    {
        var broken = new TravellerDetails(
            new ContactDetails("nobody"),
            [new PassengerDetails(PassengerType.Child, " ", new string('x', 101), new DateOnly(1800, 1, 1), new string('9', 41))],
            "");

        string[] codes = await Codes(new StartCheckoutValidator(), Checkout(broken));

        codes.ShouldBe(
            ["InvalidEmail", "InvalidPassengerName", "InvalidBirthDate", "InvalidDocumentNumber", "NoAdult", "PaymentMethodTokenRequired"],
            ignoreOrder: true);

        var result = await new StartCheckoutValidator().ValidateAsync(Checkout(broken), CancellationToken.None);
        string.Join(' ', result.Errors.Select(static e => e.Message)).ShouldNotContain("nobody");
    }

    [Fact]
    public async Task Ten_passengers_and_none_are_both_too_many_and_too_few()
    {
        TravellerDetails ten = Details() with { Passengers = [.. Enumerable.Repeat(Details().Passengers[0], 10)] };
        TravellerDetails none = Details() with { Passengers = [] };

        (await Codes(new StartCheckoutValidator(), Checkout(ten))).ShouldContain("InvalidPassengerCount");
        (await Codes(new StartCheckoutValidator(), Checkout(none))).ShouldContain("InvalidPassengerCount");
    }

    [Fact]
    public async Task An_anonymous_trip_needs_a_key_of_enough_length_and_a_signed_in_one_needs_none()
    {
        var validator = new CreateTripValidator();

        (await Codes(validator, new CreateTrip(Guid.NewGuid(), new Caller(Guid.NewGuid(), false, null)))).ShouldBeEmpty();
        (await Codes(validator, new CreateTrip(Guid.NewGuid(), new Caller(null, false, new string('k', 22))))).ShouldBeEmpty();
        (await Codes(validator, new CreateTrip(Guid.NewGuid(), new Caller(null, false, new string('k', 21))))).ShouldBe(["AccessKeyRequired"]);
        (await Codes(validator, new CreateTrip(Guid.NewGuid(), new Caller(null, false, null)))).ShouldBe(["AccessKeyRequired"]);
        (await Codes(validator, new CreateTrip(Guid.Empty, Caller.Anonymous))).ShouldContain("InvalidTripId");
    }

    [Fact]
    public async Task An_item_needs_what_its_kind_needs()
    {
        var validator = new AddItemValidator();
        Caller caller = Caller.Anonymous;
        Guid trip = Guid.NewGuid();
        Guid item = Guid.NewGuid();

        (await Codes(validator, new AddItem(trip, item, new ItemSpec(ItemKind.Stay, "token"), caller))).ShouldBeEmpty();
        (await Codes(validator, new AddItem(trip, item, new ItemSpec(ItemKind.Stay), caller))).ShouldBe(["QuoteTokenRequired"]);
        (await Codes(validator, new AddItem(trip, item, new ItemSpec(ItemKind.Flight, OfferId: "offer", ExpectedTotal: Money.Eur(10m)), caller))).ShouldBeEmpty();
        (await Codes(validator, new AddItem(trip, item, new ItemSpec(ItemKind.Flight), caller))).ShouldBe(["OfferIdRequired", "ExpectedTotalRequired"], ignoreOrder: true);
        (await Codes(validator, new AddItem(trip, item, new ItemSpec(ItemKind.Flight, OfferId: "o", ExpectedTotal: Money.Zero("EUR")), caller))).ShouldBe(["ExpectedTotalRequired"]);
        (await Codes(validator, new AddItem(trip, item, new ItemSpec(ItemKind.Flight, OfferId: "o", ExpectedTotal: new Money(10m, "USD")), caller))).ShouldBe(["ExpectedTotalRequired"]);
        (await Codes(validator, new AddItem(trip, Guid.Empty, new ItemSpec(ItemKind.Stay, "t"), caller))).ShouldBe(["InvalidItemId"]);
        _ = Encoding.UTF8;
    }

    // The access to a trip.

    [Fact]
    public void A_signed_in_trip_belongs_to_its_traveller_and_an_anonymous_one_to_the_holder_of_its_key()
    {
        Guid owner = Guid.NewGuid();
        string key = new('k', 32);
        var signedIn = Trip.Create(Guid.NewGuid(), owner, null, TripScenario.Start);
        var anonymous = Trip.Create(Guid.NewGuid(), null, Caller.HashOf(key), TripScenario.Start);

        new Caller(owner, false, null).Owns(signedIn).ShouldBeTrue();
        new Caller(Guid.NewGuid(), false, key).Owns(signedIn).ShouldBeFalse("a key is no use on a signed-in trip");
        new Caller(null, true, null).Owns(signedIn).ShouldBeFalse("an admin reads, never changes");
        new Caller(null, true, null).CanRead(signedIn).ShouldBeTrue();

        new Caller(null, false, key).Owns(anonymous).ShouldBeTrue();
        new Caller(Guid.NewGuid(), false, key).Owns(anonymous).ShouldBeTrue("a signed-in holder of the key");
        new Caller(null, false, key + "x").Owns(anonymous).ShouldBeFalse();
        new Caller(null, false, "short").Owns(anonymous).ShouldBeFalse();
        new Caller(Guid.NewGuid(), false, null).Owns(anonymous).ShouldBeFalse();

        anonymous.Claim(owner, TripScenario.Start);
        new Caller(null, false, key).Owns(anonymous).ShouldBeFalse("the key stops working once the trip is claimed");
        new Caller(owner, false, null).Owns(anonymous).ShouldBeTrue();
    }
}
