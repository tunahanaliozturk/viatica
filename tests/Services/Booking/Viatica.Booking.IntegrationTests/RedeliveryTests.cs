using Microsoft.EntityFrameworkCore;
using Viatica.Booking.Domain;
using Viatica.Contracts;
using Viatica.Testing;

namespace Viatica.Booking.IntegrationTests;

/// <summary>
/// Redelivered and reordered events (requirements section 6: "at least once delivery is assumed"). The same message
/// twice, the same content under a new event id, and the answers of one trip arriving in every order the owners can be
/// made to give them: the trip ends where it would have, and a trip that has moved on is left exactly as it is.
/// </summary>
public sealed class RedeliveryTests(WorldFixture fixture, KafkaFixture kafka) : IClassFixture<WorldFixture>, IAsyncLifetime
{
    private BookingWorld world => fixture.World;

    // Nothing paused by an earlier test is still holding answers back.
    public async ValueTask InitializeAsync() => await world.Owners.ResumeAllAsync();

    public ValueTask DisposeAsync() => ValueTask.CompletedTask;

    private sealed record Snapshot(int Revision, TripStatus Status, string Items, int Requests);

    private async Task<Snapshot> SnapshotAsync(Guid trip)
    {
        (int revision, TripStatus status, string items) = await world.Host.InDbAsync(async db =>
        {
            Trip t = await db.Trips.AsNoTracking().Include(x => x.Items).SingleAsync(x => x.Id == trip);
            return (t.Revision, t.Status, string.Join(",", t.Items.OrderBy(i => i.Id).Select(i => $"{i.Status}/{i.Awaiting}/{t.Payment}")));
        });

        return new Snapshot(revision, status, items, world.Owners.ReceivedFor(trip).Count);
    }

    [Fact]
    public async Task The_same_messages_delivered_a_second_time_change_nothing()
    {
        (Guid trip, _, _, _) = await world.BasketAsync();
        await world.CheckoutAsync(trip);
        await world.WaitForTripAsync(trip, "Confirmed");
        await world.Owners.WaitForAsync<TripConfirmed>(e => e.TripId == trip);
        await world.SettledAsync(trip);
        Snapshot before = await SnapshotAsync(trip);
        int inboxBefore = await world.InboxCountAsync();

        // Every message the owners put on their topics for this trip, put there again, byte for byte.
        int replayed = 0;
        foreach ((string topic, string owner) in new[] { (world.Topics.Stays, "stays.v1"), (world.Topics.Cars, "cars.v1"), (world.Topics.Flights, "flights.v1"), (world.Topics.Payments, "payments.v1") })
        {
            int expected = world.Owners.Said.Count(e => EventCatalog.TopicByType[e.GetType()] == owner && TripOf(e) == trip);
            using EventProbe probe = kafka.Probe(topic);
            foreach (ProbedMessage message in await probe.WaitForCountAsync(expected, m => m.Key == trip.ToString("N")))
            {
                await world.ReplayAsync(topic, message);
                replayed++;
            }
        }

        replayed.ShouldBeGreaterThan(8);
        await world.SettledAsync(trip);

        // The inbox took each replay as the message it already had: the only new rows are the barrier's own four.
        (await world.InboxCountAsync()).ShouldBe(inboxBefore + 4);
        (await SnapshotAsync(trip)).ShouldBe(before);
    }

    [Fact]
    public async Task The_same_content_under_new_event_ids_changes_nothing_on_a_finished_trip()
    {
        (Guid trip, _, _, _) = await world.BasketAsync();
        await world.CheckoutAsync(trip);
        await world.WaitForTripAsync(trip, "Confirmed");
        await world.Owners.WaitForAsync<TripConfirmed>(e => e.TripId == trip);
        await world.SettledAsync(trip);
        Snapshot before = await SnapshotAsync(trip);

        foreach (IIntegrationEvent answer in world.Owners.Said.Where(e => TripOf(e) == trip).ToArray())
        {
            await world.Owners.SayAsync(answer);
        }

        await world.SettledAsync(trip);
        (await SnapshotAsync(trip)).ShouldBe(before);
    }

    [Fact]
    public async Task The_same_content_under_new_event_ids_changes_nothing_on_a_trip_in_the_middle_of_its_saga()
    {
        (Guid trip, _, _, _) = await world.BasketAsync();
        world.Owners.Pause<PaymentCaptureRequested>();
        await world.CheckoutAsync(trip);
        await world.Owners.WaitForAsync<PaymentCaptureRequested>(e => e.TripId == trip);
        await world.SettledAsync(trip);
        Snapshot before = await SnapshotAsync(trip);
        before.Status.ShouldBe(TripStatus.Capturing);

        foreach (IIntegrationEvent answer in world.Owners.Said.Where(e => TripOf(e) == trip).ToArray())
        {
            await world.Owners.SayAsync(answer);
        }

        await world.SettledAsync(trip);
        Snapshot after = await SnapshotAsync(trip);
        after.ShouldBe(before, "no second authorisation, no second commit, no second capture request");

        await world.Owners.ResumeAsync<PaymentCaptureRequested>();
        await world.WaitForTripAsync(trip, "Confirmed");
    }

    [Theory]
    [InlineData("stay,car,flight")]
    [InlineData("stay,flight,car")]
    [InlineData("car,stay,flight")]
    [InlineData("car,flight,stay")]
    [InlineData("flight,stay,car")]
    [InlineData("flight,car,stay")]
    public async Task The_hold_answers_arriving_in_any_order_end_in_the_same_confirmed_trip(string order)
    {
        (Guid trip, _, _, _) = await world.BasketAsync();
        world.Owners.Pause<StayHoldRequested>();
        world.Owners.Pause<CarHoldRequested>();
        world.Owners.Pause<FlightOrderRequested>();

        await world.CheckoutAsync(trip);
        await world.Owners.WaitForAsync<StayHoldRequested>(e => e.TripId == trip);
        await world.Owners.WaitForAsync<CarHoldRequested>(e => e.TripId == trip);
        await world.Owners.WaitForAsync<FlightOrderRequested>(e => e.TripId == trip);

        foreach (string owner in order.Split(','))
        {
            await (owner switch
            {
                "stay" => world.Owners.ResumeAsync<StayHoldRequested>(),
                "car" => world.Owners.ResumeAsync<CarHoldRequested>(),
                _ => world.Owners.ResumeAsync<FlightOrderRequested>(),
            });
            await Task.Delay(120);
        }

        JsonElementExtensions.Total(await world.WaitForTripAsync(trip, "Confirmed")).ShouldBe("540.00");
        world.Owners.ReceivedFor(trip).OfType<PaymentAuthorizationRequested>().Count().ShouldBe(1);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task The_commit_answers_arriving_in_either_order_end_in_the_same_confirmed_trip(bool stayFirst)
    {
        (Guid trip, _, _, _) = await world.BasketAsync();
        world.Owners.Pause<StayHoldCommitRequested>();
        world.Owners.Pause<CarHoldCommitRequested>();

        await world.CheckoutAsync(trip);
        await world.Owners.WaitForAsync<StayHoldCommitRequested>(e => e.TripId == trip);
        await world.Owners.WaitForAsync<CarHoldCommitRequested>(e => e.TripId == trip);

        if (stayFirst)
        {
            await world.Owners.ResumeAsync<StayHoldCommitRequested>();
            await Task.Delay(150);
            await world.Owners.ResumeAsync<CarHoldCommitRequested>();
        }
        else
        {
            await world.Owners.ResumeAsync<CarHoldCommitRequested>();
            await Task.Delay(150);
            await world.Owners.ResumeAsync<StayHoldCommitRequested>();
        }

        JsonElementExtensions.Total(await world.WaitForTripAsync(trip, "Confirmed")).ShouldBe("540.00");
        world.Owners.ReceivedFor(trip).OfType<PaymentCaptureRequested>().Count().ShouldBe(1);
    }

    private static Guid? TripOf(IIntegrationEvent e) => e.GetType().GetProperty("TripId")?.GetValue(e) as Guid?;
}

internal static class JsonElementExtensions
{
    public static string? Total(System.Text.Json.JsonElement trip) => trip.GetProperty("total").GetProperty("amount").GetString();
}
