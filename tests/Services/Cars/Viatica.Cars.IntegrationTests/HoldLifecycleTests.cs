using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Viatica.Cars.Application.Features.Holds;
using Viatica.Cars.Domain;
using Viatica.Contracts;
using Viatica.Testing;

namespace Viatica.Cars.IntegrationTests;

/// <summary>
/// A hold's life: placed, then committed, released, cancelled or expired. Every request is safe to repeat, every
/// request that no longer applies changes nothing, and the numbers in the inventory follow the holds exactly.
/// </summary>
public sealed class HoldLifecycleTests(PostgresFixture postgres, KafkaFixture kafka, ValkeyFixture valkey) : IAsyncLifetime
{
    private CarsHost host = null!;
    private Marketplace m = null!;
    private string token = null!;

    private static DateTimeOffset Monday => Scenario.Monday;

    public async ValueTask InitializeAsync()
    {
        host = await CarsHost.StartAsync(postgres, kafka, valkey);
        m = await Scenario.ArrangeAsync(host, allotment: 3);
        token = await Scenario.TokenAsync(host.Anonymous(), m, Monday, Monday.AddHours(48));   // two days: the 7th and the 8th
    }

    public ValueTask DisposeAsync()
    {
        host.Dispose();
        return ValueTask.CompletedTask;
    }

    private async Task<(int Held, int Sold)> TodayAsync()
    {
        InventoryDay day = (await host.DaysAsync(m, "2026-12-07", "2026-12-07")).Single();
        return (day.Held, day.Sold);
    }

    private async Task<CarHold> PlaceAsync()
    {
        HoldOutcome outcome = await host.PlaceAsync(token);
        outcome.Describe().ShouldBe("placed");
        return outcome.HoldOf();
    }

    [Fact]
    public async Task A_hold_takes_one_car_on_every_rental_day_and_records_what_booking_will_charge()
    {
        CarHold hold = await PlaceAsync();

        (await host.DaysAsync(m, "2026-12-07", "2026-12-08")).Select(static d => d.Held).ShouldBe([1, 1]);
        (await host.DaysAsync(m, "2026-12-09", "2026-12-09")).Single().Held.ShouldBe(0);
        CarHold stored = await host.HoldRowAsync(hold.Id);
        stored.Status.ShouldBe(HoldStatus.Held);
        stored.Days.ShouldBe(2);
        stored.Total.ShouldBe(80m);
        stored.ExpiresAt.ShouldBe(host.Clock.GetUtcNow() + TimeSpan.FromMinutes(15));
        stored.PickUpAt.ShouldBe(Monday);
    }

    [Fact]
    public async Task Asking_for_the_same_hold_twice_holds_one_car()
    {
        Guid holdId = Guid.CreateVersion7();
        Guid tripId = Guid.CreateVersion7();

        HoldOutcome first = await host.PlaceAsync(token, holdId: holdId, tripId: tripId);
        HoldOutcome second = await host.PlaceAsync(token, holdId: holdId, tripId: tripId);

        first.Describe().ShouldBe("placed");
        second.Describe().ShouldBe("placed");
        second.HoldOf().ExpiresAt.ShouldBe(first.HoldOf().ExpiresAt);
        (await TodayAsync()).ShouldBe((1, 0));
        (await host.QueryAsync(db => db.Holds.CountAsync())).ShouldBe(1);
    }

    [Fact]
    public async Task Asking_again_for_a_hold_that_has_moved_on_answers_nothing()
    {
        CarHold hold = await PlaceAsync();
        (await host.SendAsync(new ReleaseHold(hold.TripId, hold.ItemId, hold.Id))).ShouldBe(Transition.Applied);

        HoldOutcome again = await host.PlaceAsync(token, holdId: hold.Id, tripId: hold.TripId);

        again.Describe().ShouldBe("superseded");
        (await TodayAsync()).ShouldBe((0, 0));
    }

    [Fact]
    public async Task Committing_turns_held_into_sold_and_repeating_it_changes_nothing()
    {
        CarHold hold = await PlaceAsync();

        (await host.SendAsync(new CommitHold(hold.TripId, hold.ItemId, hold.Id))).ShouldBe(Transition.Applied);
        (await host.SendAsync(new CommitHold(hold.TripId, hold.ItemId, hold.Id))).ShouldBe(Transition.Repeated);

        (await TodayAsync()).ShouldBe((0, 1));
        (await host.HoldRowAsync(hold.Id)).Status.ShouldBe(HoldStatus.Committed);
        await host.AssertInventoryIsConsistentAsync(m);
    }

    [Fact]
    public async Task Releasing_gives_the_car_back_and_repeating_it_changes_nothing()
    {
        CarHold hold = await PlaceAsync();

        (await host.SendAsync(new ReleaseHold(hold.TripId, hold.ItemId, hold.Id))).ShouldBe(Transition.Applied);
        (await host.SendAsync(new ReleaseHold(hold.TripId, hold.ItemId, hold.Id))).ShouldBe(Transition.Repeated);

        (await TodayAsync()).ShouldBe((0, 0));
        await host.AssertInventoryIsConsistentAsync(m);
    }

    [Fact]
    public async Task Releasing_a_hold_that_never_existed_is_confirmed_because_the_car_is_not_held()
    {
        // Booking unwinding a failed trip releases every item it sent a request for, including a request that was
        // lost or refused. The answer it needs is "that car is not held", and it is true.
        (await host.SendAsync(new ReleaseHold(Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid()))).ShouldBe(Transition.Repeated);
    }

    [Fact]
    public async Task A_committed_booking_is_not_released_and_can_be_cancelled_once()
    {
        CarHold hold = await PlaceAsync();
        await host.SendAsync(new CommitHold(hold.TripId, hold.ItemId, hold.Id));

        (await host.SendAsync(new ReleaseHold(hold.TripId, hold.ItemId, hold.Id))).ShouldBe(Transition.Ignored);
        (await TodayAsync()).ShouldBe((0, 1));

        (await host.SendAsync(new CancelBooking(hold.TripId, hold.ItemId, hold.Id))).ShouldBe(Transition.Applied);
        (await host.SendAsync(new CancelBooking(hold.TripId, hold.ItemId, hold.Id))).ShouldBe(Transition.Repeated);
        (await TodayAsync()).ShouldBe((0, 0));
        (await host.HoldRowAsync(hold.Id)).Status.ShouldBe(HoldStatus.Cancelled);
        await host.AssertInventoryIsConsistentAsync(m);
    }

    [Fact]
    public async Task A_request_that_names_another_trips_hold_finds_nothing_to_act_on()
    {
        CarHold hold = await PlaceAsync();

        (await host.SendAsync(new CommitHold(Guid.CreateVersion7(), hold.ItemId, hold.Id))).ShouldBe(Transition.Ignored);
        (await host.SendAsync(new ReleaseHold(hold.TripId, Guid.CreateVersion7(), hold.Id))).ShouldBe(Transition.Ignored);
        (await host.SendAsync(new CancelBooking(hold.TripId, hold.ItemId, hold.Id))).ShouldBe(Transition.Ignored);

        (await host.HoldRowAsync(hold.Id)).Status.ShouldBe(HoldStatus.Held);
        (await TodayAsync()).ShouldBe((1, 0));
    }

    [Fact]
    public async Task The_sweeper_expires_a_hold_after_fifteen_minutes_and_not_before()
    {
        CarHold hold = await PlaceAsync();

        host.Clock.Advance(TimeSpan.FromMinutes(15) - TimeSpan.FromSeconds(1));
        (await host.Sweeper.SweepAsync(CancellationToken.None)).ShouldBe(0);
        (await TodayAsync()).ShouldBe((1, 0));

        host.Clock.Advance(TimeSpan.FromSeconds(1));
        (await host.Sweeper.SweepAsync(CancellationToken.None)).ShouldBe(1);
        (await host.Sweeper.SweepAsync(CancellationToken.None)).ShouldBe(0);

        (await host.HoldRowAsync(hold.Id)).Status.ShouldBe(HoldStatus.Expired);
        (await TodayAsync()).ShouldBe((0, 0));
        await host.AssertInventoryIsConsistentAsync(m);

        ProbedMessage expired = await host.Probe.WaitForAsync(e => e.Type == "CarHoldExpired");
        expired.DataString("holdId").ShouldBe(hold.Id.ToString());
        expired.Key.ShouldBe(hold.TripId.ToString("N"));
    }

    [Fact]
    public async Task The_sweeper_leaves_a_committed_booking_alone_however_old_it_is()
    {
        CarHold hold = await PlaceAsync();
        await host.SendAsync(new CommitHold(hold.TripId, hold.ItemId, hold.Id));

        host.Clock.Advance(TimeSpan.FromDays(3));

        (await host.Sweeper.SweepAsync(CancellationToken.None)).ShouldBe(0);
        (await host.HoldRowAsync(hold.Id)).Status.ShouldBe(HoldStatus.Committed);
        (await TodayAsync()).ShouldBe((0, 1));
    }

    [Fact]
    public async Task An_expired_hold_cannot_be_committed_late()
    {
        CarHold hold = await PlaceAsync();
        host.Clock.Advance(TimeSpan.FromMinutes(16));
        await host.Sweeper.SweepAsync(CancellationToken.None);

        (await host.SendAsync(new CommitHold(hold.TripId, hold.ItemId, hold.Id))).ShouldBe(Transition.Ignored);

        (await host.HoldRowAsync(hold.Id)).Status.ShouldBe(HoldStatus.Expired);
        (await TodayAsync()).ShouldBe((0, 0));
    }

    [Fact]
    public async Task Releasing_an_expired_hold_is_confirmed()
    {
        CarHold hold = await PlaceAsync();
        host.Clock.Advance(TimeSpan.FromMinutes(16));
        await host.Sweeper.SweepAsync(CancellationToken.None);

        (await host.SendAsync(new ReleaseHold(hold.TripId, hold.ItemId, hold.Id))).ShouldBe(Transition.Repeated);
    }

    [Fact]
    public async Task Cars_given_back_can_be_held_again()
    {
        for (int i = 0; i < 3; i++)
        {
            await PlaceAsync();
        }

        (await host.PlaceAsync(token)).Describe().ShouldBe("SoldOut");

        host.Clock.Advance(TimeSpan.FromMinutes(16));
        (await host.Sweeper.SweepAsync(CancellationToken.None)).ShouldBe(3);

        // The token is old by now; a fresh one for the same rental.
        string fresh = await Scenario.TokenAsync(host.Anonymous(), m, Monday, Monday.AddHours(48));
        (await host.PlaceAsync(fresh)).Describe().ShouldBe("placed");
    }

    [Fact]
    public async Task A_tampered_token_is_refused_as_invalid()
    {
        string[] parts = token.Split('.');
        string tampered = parts[0] + "." + parts[1][..^2] + (parts[1][^2] == 'A' ? "B" : "A") + parts[1][^1];

        (await host.PlaceAsync(tampered)).Describe().ShouldBe("QuoteInvalid");
        (await TodayAsync()).ShouldBe((0, 0));
    }

    [Fact]
    public async Task An_expired_token_is_refused_as_expired()
    {
        host.Clock.Advance(TimeSpan.FromMinutes(15));

        (await host.PlaceAsync(token)).Describe().ShouldBe("QuoteExpired");
        (await TodayAsync()).ShouldBe((0, 0));
    }

    [Fact]
    public async Task A_token_is_only_good_for_the_driver_it_was_quoted_for()
    {
        // Quoted for a 35 year old; the price and the age checks were for that driver.
        (await host.PlaceAsync(token, driverAge: 22)).Describe().ShouldBe("QuoteInvalid");
    }

    [Fact]
    public async Task A_listing_taken_down_after_the_quote_is_not_bookable()
    {
        await Scenario.Ok(m.Client.PostAsync($"/cars/locations/{m.LocationId}/unpublish"));

        (await host.PlaceAsync(token)).Describe().ShouldBe("NotBookable");
        (await TodayAsync()).ShouldBe((0, 0));
    }

    [Fact]
    public async Task A_category_withdrawn_after_the_quote_is_not_bookable()
    {
        await Scenario.Ok(m.Client.PostAsync($"/cars/categories/{m.CategoryId}/unpublish"));

        (await host.PlaceAsync(token)).Describe().ShouldBe("NotBookable");
    }

    [Fact]
    public async Task A_minimum_age_raised_after_the_quote_is_not_bookable()
    {
        await Scenario.Ok(m.Client.PutAsync($"/cars/categories/{m.CategoryId}", Scenario.Category(minimumDriverAge: 40)));

        (await host.PlaceAsync(token)).Describe().ShouldBe("NotBookable");
    }

    [Fact]
    public async Task A_garbage_token_is_invalid_and_not_an_exception()
    {
        (await host.PlaceAsync("not-a-token")).Describe().ShouldBe("QuoteInvalid");
        (await host.PlaceAsync("")).Describe().ShouldBe("QuoteInvalid");
    }

    [Fact]
    public async Task The_scoped_services_a_handler_needs_resolve_from_one_scope()
    {
        // The integration event handlers share the DbContext the consumer saves; a second instance would lose
        // their changes. This is the wiring rule in docs/messaging.md, checked.
        await host.InScopeAsync(services =>
        {
            services.GetRequiredService<Viatica.Cars.Application.ICarsDb>()
                .ShouldBeSameAs(services.GetRequiredService<Viatica.Cars.Infrastructure.CarsDbContext>());
            return Task.FromResult(0);
        });
    }
}
