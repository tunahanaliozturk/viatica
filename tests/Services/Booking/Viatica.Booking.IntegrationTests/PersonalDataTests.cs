using System.Text;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Viatica.Booking.Application;
using Viatica.Contracts;
using Viatica.Kernel.Messaging;
using Viatica.Testing;

namespace Viatica.Booking.IntegrationTests;

/// <summary>
/// Requirements section 12: passenger personal data is encrypted at rest in Booking and never logged. The decision
/// (docs/services/booking.md, "Personal data"): traveller details are sealed under Booking's own key for as long as
/// the saga runs and erased when it ends; the passenger list crosses the broker sealed under the key Booking shares with
/// Flights; nothing personal is in an event, a view or a log line.
/// </summary>
public sealed class PersonalDataTests(WorldFixture fixture) : IClassFixture<WorldFixture>, IAsyncLifetime
{
    // Unusual enough that a match can only be the traveller leaking. Not "1987": hex ids contain digits.
    private static readonly string[] Secrets =
    [
        "Zeynebnur", "Karabulutoglu", "TR8675309X", "1987-05-17", "05/17/1987", "17.05.1987", "17/05/1987",
        "zeynebnur@example.test", "+905551112233", "pm_test_visa", "pm_test_mastercard",
    ];

    private BookingWorld world => fixture.World;

    public async ValueTask InitializeAsync() => await world.Owners.ResumeAllAsync();

    public ValueTask DisposeAsync() => ValueTask.CompletedTask;

    private Task<string?> SealedAsync(Guid trip) =>
        world.Host.InDbAsync(db => db.Trips.AsNoTracking().Where(t => t.Id == trip).Select(static t => t.SealedTravellerData).SingleAsync());

    [Fact]
    public async Task Traveller_details_are_sealed_while_the_saga_runs_open_only_for_their_own_trip_and_erased_when_it_ends()
    {
        (Guid trip, _, _, _) = await world.BasketAsync();
        world.Owners.Pause<StayHoldRequested>();
        await world.CheckoutAsync(trip);
        await world.Owners.WaitForAsync<StayHoldRequested>(e => e.TripId == trip);

        string? stored = await SealedAsync(trip);

        stored.ShouldNotBeNull();
        foreach (string secret in Secrets)
        {
            stored.ShouldNotContain(secret);
        }

        // It opens with Booking's key for this trip, and for no other.
        var vault = new TravellerVault(world.Host.PassengerKey, world.Host.StorageKey);
        vault.TryOpen(trip, stored, out TravellerDetails details).ShouldBeTrue();
        details.Passengers.Single().Surname.ShouldBe("Karabulutoglu");
        details.PaymentMethodToken.ShouldBe("pm_test_visa");
        vault.TryOpen(Guid.NewGuid(), stored, out _).ShouldBeFalse("the trip id is bound into the ciphertext");
        new TravellerVault(world.Host.PassengerKey, new byte[32]).TryOpen(trip, stored, out _).ShouldBeFalse("another key opens nothing");

        // And nowhere else in the database: not in the outbox, not on the items.
        (await world.Host.InDbAsync(db => db.Set<OutboxMessage>().AsNoTracking().Select(static m => m.Payload).ToListAsync()))
            .ShouldAllBe(payload => !payload.Contains("Karabulutoglu", StringComparison.Ordinal) && !payload.Contains("zeynebnur", StringComparison.Ordinal));

        await world.Owners.ResumeAsync<StayHoldRequested>();
        await world.WaitForTripAsync(trip, "Confirmed");
        (await SealedAsync(trip)).ShouldBeNull("nothing after the saga needs the details, so none are kept");
    }

    [Fact]
    public async Task A_failed_trip_keeps_no_traveller_details_either()
    {
        (Guid trip, _, _, _) = await world.BasketAsync(car: new ItemPlan { Total = 90m, Fate = Fate.Refuse });
        await world.CheckoutAsync(trip);
        await world.WaitForTripAsync(trip, "Failed");

        (await SealedAsync(trip)).ShouldBeNull();
    }

    [Fact]
    public async Task A_draft_holds_no_personal_data_at_all()
    {
        (Guid trip, _, _, _) = await world.BasketAsync();

        (await SealedAsync(trip)).ShouldBeNull();
        (await world.TripAsync(trip)).GetRawText().ShouldNotContain("Karabulutoglu");
    }

    [Fact]
    public async Task The_passenger_list_leaves_sealed_for_the_airline_and_nothing_personal_is_on_the_topic()
    {
        (Guid trip, _, _, Guid flight) = await world.BasketAsync();
        await world.CheckoutAsync(trip);
        await world.WaitForTripAsync(trip, "Confirmed");
        await world.Owners.WaitForAsync<TripConfirmed>(e => e.TripId == trip);

        ProbedMessage order = await world.NextAsync(nameof(FlightOrderRequested), trip);
        order.DataString("encryptedPassengers").ShouldNotBeNullOrEmpty();

        // Every message Booking put on its topic for this trip, as bytes.
        IReadOnlyList<ProbedMessage> all = await world.Probe.WaitForCountAsync(1, m => m.Key == trip.ToString("N") && m.Type == nameof(TripConfirmed));
        all.ShouldNotBeEmpty();
        foreach (ProbedMessage message in world.Probe.Received.Where(m => m.Key == trip.ToString("N")))
        {
            string text = Encoding.UTF8.GetString(message.Value);
            foreach (string secret in Secrets.Where(static s => s != "pm_test_visa"))
            {
                text.ShouldNotContain(secret, Case.Insensitive, $"{message.Type} carries personal data");
            }
        }

        // Flights, which holds the key, could open it; it saw the person.
        world.Owners.PassengersOf(flight)!.Single().GivenName.ShouldBe("Zeynebnur");
    }

    [Fact]
    public void The_detector_would_see_a_leak()
    {
        // A control for the assertions below: this proves the capture holds messages, structured values and
        // exceptions, so an empty search result means the traveller was not there and not that nothing was captured.
        ILogger canary = world.Host.Services.GetRequiredService<ILoggerFactory>().CreateLogger("canary");
#pragma warning disable CA1873 // The arguments are constants; the rule cannot see that the logger is a capture.
        canary.LogInformation("in the message: {Value}", "CanaryInMessage");
        canary.LogDebug("only in the state: {Hidden}", "CanaryInState");
        canary.LogError(new InvalidOperationException("CanaryInException"), "failed");
#pragma warning restore CA1873

        world.Host.Logs.Containing("CanaryInMessage").ShouldNotBeEmpty();
        world.Host.Logs.Containing("CanaryInState").ShouldNotBeEmpty();
        world.Host.Logs.Containing("CanaryInException").ShouldNotBeEmpty();
        world.Host.Logs.Entries.Count.ShouldBeGreaterThan(10);
    }

    [Fact]
    public async Task A_full_run_logs_everything_about_the_trips_and_nothing_about_the_traveller()
    {
        // The happy path, a cancellation, the paths that fail, and the requests that are refused with the traveller's
        // details in the body.
        (Guid ok, Guid stay, _, _) = await world.BasketAsync();
        await world.CheckoutAsync(ok);
        await world.WaitForTripAsync(ok, "Confirmed");
        await world.SendAsync(HttpMethod.Post, $"/trips/{ok}/items/{stay}/cancel", token: world.Traveller);

        (Guid refused, _, _, _) = await world.BasketAsync(car: new ItemPlan { Total = 90m, Fate = Fate.Refuse });
        await world.CheckoutAsync(refused);
        await world.WaitForTripAsync(refused, "Failed");

        (Guid declined, _, _, _) = await world.BasketAsync();
        world.Owners.Decline(declined);
        await world.CheckoutAsync(declined);
        await world.WaitForTripAsync(declined, "Failed");

        (Guid conflict, _, _, _) = await world.BasketAsync();
        await world.CheckoutAsync(conflict);
        await world.CheckoutAsync(conflict, BookingWorld.Details(given: "Zeynebnur 2", payment: "pm_test_mastercard"));
        await world.WaitForTripAsync(conflict, "Confirmed");

        (Guid invalid, _, _, _) = await world.BasketAsync();
        await world.CheckoutAsync(invalid, new
        {
            contact = new { email = "zeynebnur-not-an-email" },
            passengers = new[] { new { type = "Adult", givenName = "Zeynebnur", surname = "Karabulutoglu", birthDate = "1987-05-17", documentNumber = "TR8675309X" } },
            paymentMethodToken = "",
        });
        await world.SendAsync(
            HttpMethod.Put, $"/trips/{invalid}/checkout",
            JsonElement.Parse("""{"contact":{"email":"zeynebnur@example.test"},"passengers":[{"type":"Adult","givenName":"Zeynebnur","surname":"Karabulutoglu","birthDate":"17/05/1987"}],"paymentMethodToken":"pm_test_visa"}"""),
            world.Traveller);

        // The log is full of what happened...
        world.Host.Logs.Containing("ended Confirmed").ShouldNotBeEmpty();
        world.Host.Logs.Containing("ended Failed").ShouldNotBeEmpty();
        world.Host.Logs.Containing("compensating request").ShouldNotBeEmpty();
        world.Host.Logs.Entries.Count(static e => e.StartsWith("Viatica.Booking", StringComparison.Ordinal)).ShouldBeGreaterThan(8);

        // ...and the traveller is nowhere in it, nor are the keys or the sealed blob.
        foreach (string secret in Secrets)
        {
            world.Host.Logs.Containing(secret).ShouldBeEmpty($"'{secret}' was logged");
        }

        world.Host.Logs.Containing(Convert.ToBase64String(world.Host.PassengerKey)).ShouldBeEmpty("the broker key was logged");
        world.Host.Logs.Containing(Convert.ToBase64String(world.Host.StorageKey)).ShouldBeEmpty("the storage key was logged");
        string? sealedBlob = await SealedAsync(invalid);
        sealedBlob.ShouldBeNull();
        FlightOrderRequested sentToFlights = world.Owners.Received.OfType<FlightOrderRequested>().First(e => e.TripId == ok);
        world.Host.Logs.Containing(sentToFlights.EncryptedPassengers).ShouldBeEmpty("a sealed passenger list was logged");
    }
}
