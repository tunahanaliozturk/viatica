using System.Net;
using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.EntityFrameworkCore;
using Viatica.Cars.Application.Features.Holds;
using Viatica.Cars.Domain;
using Viatica.Testing;

namespace Viatica.Cars.IntegrationTests;

public sealed class InventoryTests(PostgresFixture postgres, KafkaFixture kafka, ValkeyFixture valkey) : IAsyncLifetime
{
    private CarsHost host = null!;

    public async ValueTask InitializeAsync() => host = await CarsHost.StartAsync(postgres, kafka, valkey);

    public ValueTask DisposeAsync()
    {
        host.Dispose();
        return ValueTask.CompletedTask;
    }

    private static string Path(Marketplace m) => $"/cars/locations/{m.LocationId}/categories/{m.CategoryId}/inventory";

    private async Task<IReadOnlyList<ProbedMessage>> AvailabilityAsync(Marketplace m, int count) =>
        await host.Probe.WaitForCountAsync(
            count, e => e.Type == "CarAvailabilityChanged" && e.DataString("locationId") == m.LocationId.ToString() && e.DataString("categoryId") == m.CategoryId.ToString());

    [Fact]
    public async Task A_range_is_set_in_one_request_and_announced_as_one_event()
    {
        Marketplace m = await Scenario.ArrangeAsync(host, allotment: 5, rate: 40m);

        IReadOnlyList<ProbedMessage> events = await AvailabilityAsync(m, 1);

        JsonElement days = events[0].Data.GetProperty("days");
        days.GetArrayLength().ShouldBe(31);
        days[0].GetProperty("date").GetString().ShouldBe("2026-12-01");
        days[0].GetProperty("available").GetInt32().ShouldBe(5);
        days[0].GetProperty("rate").GetProperty("amount").GetString().ShouldBe("40.00");
        events[0].Key.ShouldBe(m.LocationId.ToString("N"));

        JsonNode calendar = await (await m.Client.FetchAsync(Path(m), "from=2026-12-01&to=2026-12-03")).JsonAsync();
        calendar.AsArray().Select(static d => (int)d!["allotment"]!).ShouldBe([5, 5, 5]);
    }

    [Fact]
    public async Task Each_change_to_a_category_at_a_location_announces_a_higher_version()
    {
        Marketplace m = await Scenario.ArrangeAsync(host, allotment: 5);
        await Scenario.Ok(m.Client.PostAsync(Path(m) + "/bulk", Scenario.Inventory("2026-12-07", "2026-12-08", 3, 55m)));
        await Scenario.Ok(m.Client.PostAsync(Path(m) + "/bulk", Scenario.Inventory("2026-12-08", "2026-12-09", 4, 60m)));

        IReadOnlyList<ProbedMessage> events = await AvailabilityAsync(m, 3);

        events.Select(static e => long.Parse(e.DataString("availabilityVersion")!, System.Globalization.CultureInfo.InvariantCulture)).ShouldBe([1, 2, 3]);
        events[1].Data.GetProperty("days").GetArrayLength().ShouldBe(2);
        events[1].Data.GetProperty("days")[0].GetProperty("available").GetInt32().ShouldBe(3);
        events[1].Data.GetProperty("days")[0].GetProperty("rate").GetProperty("amount").GetString().ShouldBe("55.00");
    }

    [Fact]
    public async Task Versions_are_per_category_and_location_so_another_stream_starts_at_one()
    {
        Marketplace m = await Scenario.ArrangeAsync(host);
        Guid second = Guid.CreateVersion7();
        await Scenario.Ok(m.Client.PutAsync($"/cars/categories/{second}", Scenario.Category("CDMR", name: "Compact")));
        await Scenario.Ok(m.Client.PostAsync($"/cars/locations/{m.LocationId}/categories/{second}/inventory/bulk", Scenario.Inventory("2026-12-01", "2026-12-02", 2, 50m)));

        IReadOnlyList<ProbedMessage> events = await host.Probe.WaitForCountAsync(
            1, e => e.Type == "CarAvailabilityChanged" && e.DataString("categoryId") == second.ToString());

        events[0].DataString("availabilityVersion").ShouldBe("1");
    }

    [Fact]
    public async Task Holds_commits_releases_and_cancellations_announce_the_new_numbers_with_rising_versions()
    {
        Marketplace m = await Scenario.ArrangeAsync(host, allotment: 2);
        string token = await Scenario.TokenAsync(host.Anonymous(), m, Scenario.Monday, Scenario.Monday.AddHours(24));
        CarHold held = (await host.PlaceAsync(token)).HoldOf();
        CarHold released = (await host.PlaceAsync(token)).HoldOf();
        await host.SendAsync(new CommitHold(held.TripId, held.ItemId, held.Id));
        await host.SendAsync(new ReleaseHold(released.TripId, released.ItemId, released.Id));
        await host.SendAsync(new CancelBooking(held.TripId, held.ItemId, held.Id));

        // Inventory set (1), two holds (2, 3), the release (4) and the cancellation (5). Committing changes held to
        // sold and so leaves availability alone: it announces nothing.
        IReadOnlyList<ProbedMessage> events = await AvailabilityAsync(m, 5);

        events.Select(static e => long.Parse(e.DataString("availabilityVersion")!, System.Globalization.CultureInfo.InvariantCulture)).ShouldBe([1, 2, 3, 4, 5]);
        int[] availableOnTheSeventh = [.. events.Select(static e => e.Data.GetProperty("days").EnumerateArray()
            .Single(static d => d.GetProperty("date").GetString() == "2026-12-07").GetProperty("available").GetInt32())];
        availableOnTheSeventh.ShouldBe([2, 1, 0, 1, 2]);
    }

    [Fact]
    public async Task Repeating_a_bulk_request_with_the_same_id_returns_the_first_answer_and_changes_nothing()
    {
        Marketplace m = await Scenario.ArrangeAsync(host);
        string id = Guid.NewGuid().ToString();
        object body = Scenario.Inventory("2026-12-10", "2026-12-12", 9, 70m);

        HttpResponseMessage first = await m.Client.PostAsync(Path(m) + "/bulk", body, id, host.Clock.GetUtcNow());
        HttpResponseMessage again = await m.Client.PostAsync(Path(m) + "/bulk", body, id, host.Clock.GetUtcNow());

        await first.AssertStatusAsync(HttpStatusCode.OK);
        await again.AssertStatusAsync(HttpStatusCode.OK);
        (await again.Content.ReadAsStringAsync()).ShouldBe(await first.Content.ReadAsStringAsync());
        ((int)(await first.JsonAsync())["availabilityVersion"]!).ShouldBe(2);

        // One event for the arrangement, one for the first request, none for the repeat.
        (await AvailabilityAsync(m, 2)).Count.ShouldBe(2);
        await Task.Delay(500);
        host.Probe.Received.Count(e => e.Type == "CarAvailabilityChanged").ShouldBe(2);
    }

    [Fact]
    public async Task The_same_id_with_a_different_body_is_refused()
    {
        Marketplace m = await Scenario.ArrangeAsync(host);
        string id = Guid.NewGuid().ToString();
        await Scenario.Ok(m.Client.PostAsync(Path(m) + "/bulk", Scenario.Inventory("2026-12-10", "2026-12-12", 9, 70m), id, host.Clock.GetUtcNow()));

        HttpResponseMessage reuse = await m.Client.PostAsync(Path(m) + "/bulk", Scenario.Inventory("2026-12-10", "2026-12-12", 1, 70m), id, host.Clock.GetUtcNow());

        await reuse.AssertStatusAsync(HttpStatusCode.BadRequest, "InvalidRequest");
    }

    [Fact]
    public async Task An_allotment_below_what_is_sold_or_held_is_refused_and_no_day_of_the_range_changes()
    {
        Marketplace m = await Scenario.ArrangeAsync(host, allotment: 5);
        string token = await Scenario.TokenAsync(host.Anonymous(), m, Scenario.Monday, Scenario.Monday.AddHours(24));
        for (int i = 0; i < 3; i++)
        {
            (await host.PlaceAsync(token)).Describe().ShouldBe("placed");
        }

        // The 7th has three cars held, so two is too few; the 8th and the 9th would take it. All or nothing.
        HttpResponseMessage refused = await m.Client.PostAsync(Path(m) + "/bulk", Scenario.Inventory("2026-12-07", "2026-12-09", 2, 99m));

        await refused.AssertStatusAsync(HttpStatusCode.Conflict, "AllotmentBelowCommitments");
        List<InventoryDay> days = await host.DaysAsync(m, "2026-12-07", "2026-12-09");
        days.Select(static d => d.Allotment).ShouldBe([5, 5, 5]);
        days.Select(static d => d.Rate).ShouldBe([40m, 40m, 40m]);

        // Three is exactly enough.
        await Scenario.Ok(m.Client.PostAsync(Path(m) + "/bulk", Scenario.Inventory("2026-12-07", "2026-12-09", 3, 99m)));
        (await host.DaysAsync(m, "2026-12-07", "2026-12-07")).Single().Available.ShouldBe(0);
    }

    [Fact]
    public async Task A_calendar_shows_sold_and_held_next_to_the_allotment()
    {
        Marketplace m = await Scenario.ArrangeAsync(host, allotment: 4);
        string token = await Scenario.TokenAsync(host.Anonymous(), m, Scenario.Monday, Scenario.Monday.AddHours(24));
        CarHold sold = (await host.PlaceAsync(token)).HoldOf();
        await host.PlaceAsync(token);
        await host.SendAsync(new CommitHold(sold.TripId, sold.ItemId, sold.Id));

        JsonNode day = (await (await m.Client.FetchAsync(Path(m), "from=2026-12-07&to=2026-12-07")).JsonAsync())[0]!;

        ((int)day["allotment"]!).ShouldBe(4);
        ((int)day["sold"]!).ShouldBe(1);
        ((int)day["held"]!).ShouldBe(1);
        ((int)day["available"]!).ShouldBe(2);
    }

    [Theory]
    [InlineData("2026-12-05", "2026-12-01", 5, "InvalidDateRange")]
    [InlineData("2026-12-01", "2027-12-31", 5, "InvalidDateRange")]
    [InlineData("2026-10-01", "2026-10-05", 5, "DateInThePast")]
    [InlineData("2028-01-01", "2028-01-05", 5, "DateTooFarAhead")]
    [InlineData("2026-12-01", "2026-12-05", -1, "InvalidAllotment")]
    public async Task A_bad_range_or_allotment_is_a_400_and_changes_nothing(string from, string to, int allotment, string code)
    {
        Marketplace m = await Scenario.ArrangeAsync(host);

        HttpResponseMessage response = await m.Client.PostAsync(Path(m) + "/bulk", Scenario.Inventory(from, to, allotment, 40m));

        await response.AssertStatusAsync(HttpStatusCode.BadRequest, "InvalidRequest");
        (await response.JsonAsync())["error"]!["details"]!.AsArray().Select(static d => (string)d!["code"]!).ShouldContain(code);
    }

    [Fact]
    public async Task The_rate_is_money_in_euros()
    {
        Marketplace m = await Scenario.ArrangeAsync(host);

        HttpResponseMessage dollars = await m.Client.PostAsync(
            Path(m) + "/bulk", new { from = "2026-12-01", to = "2026-12-02", allotment = 1, rate = new { amount = "40.00", currency = "USD" } });
        HttpResponseMessage number = await m.Client.PostAsync(
            Path(m) + "/bulk", new { from = "2026-12-01", to = "2026-12-02", allotment = 1, rate = 40 });

        await dollars.AssertStatusAsync(HttpStatusCode.BadRequest, "InvalidRequest");
        await number.AssertStatusAsync(HttpStatusCode.BadRequest, "InvalidRequest");
    }

    [Fact]
    public async Task Two_partners_bulk_updating_the_same_days_at_once_never_corrupt_the_counters()
    {
        Marketplace m = await Scenario.ArrangeAsync(host, allotment: 10);
        string token = await Scenario.TokenAsync(host.Anonymous(), m, Scenario.Monday, Scenario.Monday.AddHours(24));
        using var start = new SemaphoreSlim(0);

        Task<string>[] writers =
        [
            .. Enumerable.Range(0, 8).Select(i => Task.Run(async () =>
            {
                await start.WaitAsync();
                return (await host.PlaceAsync(token)).Describe();
            })),
            .. Enumerable.Range(0, 8).Select(i => Task.Run(async () =>
            {
                await start.WaitAsync();
                HttpResponseMessage r = await m.Client.PostAsync(Path(m) + "/bulk", Scenario.Inventory("2026-12-07", "2026-12-07", 8 + (i % 3), 40m + i));
                return ((int)r.StatusCode).ToString(System.Globalization.CultureInfo.InvariantCulture);
            })),
        ];
        start.Release(writers.Length);
        string[] results = await Task.WhenAll(writers);

        results.Count(static r => r == "placed").ShouldBe(8);
        // 200s and 409s are both fine for the writers; what matters is that the counters still add up.
        results.Skip(8).ShouldAllBe(static r => r == "200" || r == "409");
        await host.AssertInventoryIsConsistentAsync(m);
        (await host.QueryAsync(db => db.InventoryDays.AsNoTracking().CountAsync(d => d.Held + d.Sold > d.Allotment))).ShouldBe(0);
    }
}
