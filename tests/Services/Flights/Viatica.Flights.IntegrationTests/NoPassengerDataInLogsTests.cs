using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Viatica.Contracts;
using Viatica.Kernel.Pii;

namespace Viatica.Flights.IntegrationTests;

/// <summary>
/// FLT-5: passenger details are personal data and are never logged. The host here writes every log entry of every
/// category at Trace, entries are kept with their structured values and their exceptions, and a full run (orders,
/// ticketing, cancellation, and the paths that fail) is searched for the passenger.
/// </summary>
[Collection(DefaultFlightsGroup.Name)]
public sealed class NoPassengerDataInLogsTests(DefaultFlights flights) : IAsyncLifetime
{
    private readonly Scenario scenario = new(flights);

    public async ValueTask InitializeAsync() => await flights.Sim.QuietAsync();

    public ValueTask DisposeAsync() => ValueTask.CompletedTask;

    // Unusual enough that a match can only be the passenger leaking. Not "1987": hex ids contain digits.
    private static readonly string[] Secrets = ["Zeynebnur", "Karabulutoglu", "TR8675309X", "1987-05-17", "05/17/1987", "17.05.1987"];

    [Fact]
    public void The_detector_would_see_a_leak()
    {
        // A control for the assertions below: this proves the capture holds messages, structured values and
        // exceptions, so an empty search result means the passenger was not there and not that nothing was captured.
#pragma warning disable CA1873 // The arguments are constants; the rule cannot see that the logger is a capture.
        ILogger canary = flights.Host.Services.GetRequiredService<ILoggerFactory>().CreateLogger("canary");
        canary.LogInformation("in the message: {Value}", "CanaryInMessage");
        canary.LogDebug("only in the state: {Hidden}", "CanaryInState");
        canary.LogError(new InvalidOperationException("CanaryInException"), "failed");
#pragma warning restore CA1873

        flights.Host.Logs.Containing("CanaryInMessage").ShouldNotBeEmpty();
        flights.Host.Logs.Containing("CanaryInState").ShouldNotBeEmpty();
        flights.Host.Logs.Containing("CanaryInException").ShouldNotBeEmpty();
        flights.Host.Logs.Entries.Count.ShouldBeGreaterThan(10);
    }

    [Fact]
    public async Task A_full_run_logs_everything_about_the_orders_and_nothing_about_the_passenger()
    {
        // Orders, ticketing, cancellation: the happy path.
        PlacedOrder ticketed = await scenario.TicketedOrderAsync("ZC");
        await flights.Booking.SendAsync(new FlightOrderCancelRequested(ticketed.TripId, ticketed.ItemId, ticketed.OrderId));
        await scenario.NextAsync(nameof(FlightOrderCancelled), ticketed.TripId);

        // Voids, and the airline refusing.
        PlacedOrder refused = await scenario.OrderAsync("JG");
        await flights.Sim.SetKnobsAsync("JG", new { TicketingRefusalRate = 1.0 });
        await flights.Booking.SendAsync(new FlightTicketingRequested(refused.TripId, refused.ItemId, refused.OrderId));
        await scenario.NextAsync(nameof(FlightTicketingRefused), refused.TripId);
        await flights.Booking.SendAsync(new FlightOrderVoidRequested(refused.TripId, refused.ItemId, refused.OrderId));
        await scenario.NextAsync(nameof(FlightOrderVoided), refused.TripId);

        // The failure paths that handle the passenger list: wrong age, wrong type, replayed ciphertext, garbage.
        PricedOffer offer = await scenario.PriceAsync("ZC");
        PassengerDetails toddler = Scenario.Zeynebnur with { BirthDate = DateOnly.FromDateTime(DateTime.UtcNow).AddYears(-3) };
        string stolen = scenario.RequestFor(offer, Guid.CreateVersion7(), Guid.CreateVersion7()).EncryptedPassengers;
        List<Guid> trips = [];
        foreach (FlightOrderRequested bad in new[]
        {
            scenario.RequestFor(offer, Guid.CreateVersion7(), Guid.CreateVersion7(), [toddler]),
            scenario.RequestFor(offer, Guid.CreateVersion7(), Guid.CreateVersion7(), [Scenario.Zeynebnur with { GivenName = "Zeynebnur 2" }]),
            scenario.RequestFor(offer, Guid.CreateVersion7(), Guid.CreateVersion7()) with { EncryptedPassengers = stolen },
            scenario.RequestFor(offer, Guid.CreateVersion7(), Guid.CreateVersion7()) with { EncryptedPassengers = "garbage" },
        })
        {
            trips.Add(bad.TripId);
            await flights.Booking.SendAsync(bad);
        }

        foreach (Guid trip in trips)
        {
            (await scenario.NextAsync(nameof(FlightOrderRejected), trip)).DataString("reason").ShouldBe("InvalidPassengers");
        }

        // And an airline that is down while an order is placed.
        PricedOffer second = await scenario.PriceAsync("YZ");
        await flights.Sim.SetKnobsAsync("YZ", new { Outage = true });
        Guid downTrip = Guid.CreateVersion7();
        await flights.Booking.SendAsync(scenario.RequestFor(second, downTrip, Guid.CreateVersion7()));
        await scenario.NextAsync(nameof(FlightOrderRejected), downTrip);

        // The log is full of what happened...
        flights.Host.Logs.Containing("created as").ShouldNotBeEmpty();
        flights.Host.Logs.Containing("were refused").ShouldNotBeEmpty();
        flights.Host.Logs.Containing("did not authenticate").ShouldNotBeEmpty();
        flights.Host.Logs.Containing("ticketed").ShouldNotBeEmpty();
        flights.Host.Logs.Entries.Count(static e => e.StartsWith("Viatica.Flights", StringComparison.Ordinal)).ShouldBeGreaterThan(10);

        // ...and the passenger is nowhere in it, nor are the broker's ciphertext or either key.
        foreach (string secret in Secrets)
        {
            flights.Host.Logs.Containing(secret).ShouldBeEmpty($"'{secret}' was logged");
        }

        flights.Host.Logs.Containing(stolen).ShouldBeEmpty("a sealed passenger list was logged");
        flights.Host.Logs.Containing(Convert.ToBase64String(flights.Host.PassengerKey)).ShouldBeEmpty("the broker key was logged");
        flights.Host.Logs.Containing(Convert.ToBase64String(flights.Host.StorageKey)).ShouldBeEmpty("the storage key was logged");
    }
}
