using System.Net;
using System.Text.Json;
using Viatica.Contracts;
using Viatica.Testing;

namespace Viatica.Booking.IntegrationTests;

/// <summary>
/// Who may see what (requirements section 12, BKG-1, BKG-5). Someone else's trip answers 404, never 403, so an id says
/// nothing about whose it is; a partner's list holds their own inventory's bookings and nobody else's; an anonymous trip
/// belongs to whoever holds its key until a traveller claims it.
/// </summary>
public sealed class TenancyTests(WorldFixture fixture) : IClassFixture<WorldFixture>, IAsyncLifetime
{
    private static readonly Guid PartnerA = Guid.Parse("c0000000-0000-4000-8000-0000000000a1");
    private static readonly Guid PartnerB = Guid.Parse("c0000000-0000-4000-8000-0000000000b2");
    private static readonly Guid PartnerC = Guid.Parse("c0000000-0000-4000-8000-0000000000c3");

    private BookingWorld world => fixture.World;

    public async ValueTask InitializeAsync() => await world.Owners.ResumeAllAsync();

    public ValueTask DisposeAsync() => ValueTask.CompletedTask;

    [Fact]
    public async Task Another_traveller_cannot_see_change_start_or_follow_a_trip_and_is_told_it_does_not_exist()
    {
        (Guid trip, Guid stay, _, _) = await world.BasketAsync();
        JsonElement before = await world.TripAsync(trip);
        string stranger = BookingWorld.TravellerToken();

        var attempts = new (string What, Task<HttpResponseMessage> Response)[]
        {
            ("read", world.SendAsync(HttpMethod.Get, $"/trips/{trip}", token: stranger)),
            ("add an item", world.SendAsync(HttpMethod.Put, $"/trips/{trip}/items/{Guid.NewGuid()}", new { kind = "Stay", quoteToken = world.StayQuote(10m) }, stranger)),
            ("remove an item", world.SendAsync(HttpMethod.Delete, $"/trips/{trip}/items/{stay}", token: stranger)),
            ("claim", world.SendAsync(HttpMethod.Post, $"/trips/{trip}/claim", token: stranger)),
            ("check out", world.CheckoutAsync(trip, token: stranger)),
            ("cancel an item", world.SendAsync(HttpMethod.Post, $"/trips/{trip}/items/{stay}/cancel", token: stranger)),
            ("ask for the operation", world.SendAsync(HttpMethod.Get, $"/operations/{trip}", token: stranger)),
            ("ask for an item's operation", world.SendAsync(HttpMethod.Get, $"/operations/{stay}", token: stranger)),
            ("follow the stream", world.SendAsync(HttpMethod.Get, $"/trips/{trip}/events", token: stranger)),
            ("read as a partner", world.SendAsync(HttpMethod.Get, $"/trips/{trip}", token: BookingWorld.PartnerToken(PartnerA))),
            ("read with no token", world.SendAsync(HttpMethod.Get, $"/trips/{trip}")),
        };

        foreach ((string what, Task<HttpResponseMessage> pending) in attempts)
        {
            HttpResponseMessage response = await pending;
            response.StatusCode.ShouldBe(HttpStatusCode.NotFound, $"to {what}");
            (await BookingWorld.ErrorCodeAsync(response)).ShouldBeOneOf("TripNotFound", "NotFound");
        }

        // Nothing moved, and the stranger's attempts did not start a saga.
        JsonElement after = await world.TripAsync(trip);
        after.GetProperty("revision").GetInt32().ShouldBe(before.GetProperty("revision").GetInt32());
        after.GetProperty("items").GetArrayLength().ShouldBe(3);
        world.Owners.ReceivedFor(trip).OfType<StayHoldRequested>().ShouldBeEmpty();

        // An admin may read it, and only read it.
        string admin = BookingWorld.AdminToken();
        (await world.SendAsync(HttpMethod.Get, $"/trips/{trip}", token: admin)).StatusCode.ShouldBe(HttpStatusCode.OK);
        (await world.SendAsync(HttpMethod.Delete, $"/trips/{trip}/items/{stay}", token: admin)).StatusCode.ShouldBe(HttpStatusCode.NotFound);
    }

    [Fact]
    public async Task A_traveller_lists_only_their_own_trips_newest_first_and_pages_through_them()
    {
        var mine = BookingWorld.TravellerToken(Guid.CreateVersion7());
        var theirs = BookingWorld.TravellerToken(Guid.CreateVersion7());
        List<Guid> ours = [];
        for (int i = 0; i < 5; i++)
        {
            ours.Add(await world.NewTripAsync(mine));
            world.Clock.Advance(TimeSpan.FromSeconds(1)); // trips made in the same instant have no "newest"
        }

        Guid other = await world.NewTripAsync(theirs);

        List<Guid> seen = [];
        string? next = $"/trips?$top=2&api-version={BookingHost.Version}";
        while (next is not null)
        {
            // The link is followed exactly as returned: it already carries the api-version and the signed skiptoken.
            HttpResponseMessage page = await world.SendAsync(HttpMethod.Get, next, token: mine, withVersion: false);
            page.StatusCode.ShouldBe(HttpStatusCode.OK);
            JsonElement body = await BookingWorld.JsonAsync(page);
            seen.AddRange(body.GetProperty("value").EnumerateArray().Select(static t => t.GetProperty("id").GetGuid()));
            next = body.TryGetProperty("nextLink", out JsonElement link) ? new Uri(link.GetString()!).PathAndQuery : null;
        }

        seen.ShouldBe(((IEnumerable<Guid>)ours).Reverse(), "every trip of mine exactly once, the newest first");
        seen.ShouldNotContain(other);

        (await world.SendAsync(HttpMethod.Get, "/trips", token: BookingWorld.PartnerToken(PartnerA))).StatusCode.ShouldBe(HttpStatusCode.Forbidden);
        (await world.SendAsync(HttpMethod.Get, "/trips")).StatusCode.ShouldBe(HttpStatusCode.Unauthorized);
    }

    [Fact]
    public async Task A_partner_lists_the_bookings_against_their_own_inventory_and_nobody_elses()
    {
        ItemPlan a(decimal total) => new() { Total = total, PartnerId = PartnerA };
        ItemPlan b(decimal total) => new() { Total = total, PartnerId = PartnerB };

        // Trip 1: a stay and a car of partner A. Trip 2: a stay of partner B. Both confirmed.
        (Guid t1, Guid stayA, Guid carA, _) = await world.BasketAsync(a(300m), a(90m), new ItemPlan { Total = 150m });
        (Guid t2, Guid stayB, _, _) = await world.BasketAsync(b(200m), new ItemPlan { Total = 80m }, new ItemPlan { Total = 100m });
        foreach (Guid trip in new[] { t1, t2 })
        {
            await world.CheckoutAsync(trip);
            await world.WaitForTripAsync(trip, "Confirmed");
        }

        // Trip 3 failed: its held stay was never a booking. Trip 4 is still capturing: not yet one either.
        (Guid t3, Guid stayFailed, _, _) = await world.BasketAsync(a(111m), new ItemPlan { Total = 90m, Fate = Fate.Refuse }, new ItemPlan { Total = 150m });
        await world.CheckoutAsync(t3);
        await world.WaitForTripAsync(t3, "Failed");
        world.Owners.Pause<PaymentCaptureRequested>();
        (Guid t4, Guid stayCapturing, _, _) = await world.BasketAsync(a(222m), new ItemPlan { Total = 90m }, new ItemPlan { Total = 150m });
        await world.CheckoutAsync(t4);
        await world.Owners.WaitForAsync<PaymentCaptureRequested>(e => e.TripId == t4);

        List<JsonElement> mine = await ListAsync(PartnerA);
        mine.Select(static b => b.GetProperty("itemId").GetGuid()).ShouldBe([carA, stayA], ignoreOrder: true);
        mine.ShouldAllBe(static b => b.GetProperty("status").GetString() == "Committed");
        mine.Select(static b => b.GetProperty("tripId").GetGuid()).ShouldAllBe(id => id == t1);

        (await ListAsync(PartnerB)).Select(static b => b.GetProperty("itemId").GetGuid()).ShouldBe([stayB]);
        (await ListAsync(PartnerC)).ShouldBeEmpty("a partner with no inventory here sees an empty list, not an error");

        // It pages, filters and sorts like every other list, and says nothing about the traveller.
        HttpResponseMessage firstPage = await world.SendAsync(HttpMethod.Get, "/partner/bookings?$top=1", token: BookingWorld.PartnerToken(PartnerA));
        JsonElement first = await BookingWorld.JsonAsync(firstPage);
        first.GetProperty("value").GetArrayLength().ShouldBe(1);
        first.TryGetProperty("nextLink", out _).ShouldBeTrue();
        string raw = first.GetRawText();
        raw.ShouldNotContain(world.TravellerId.ToString());
        raw.ShouldNotContain("Karabulutoglu");

        HttpResponseMessage cars = await world.SendAsync(HttpMethod.Get, "/partner/bookings?$filter=kind eq 'Car'", token: BookingWorld.PartnerToken(PartnerA));
        (await BookingWorld.JsonAsync(cars)).GetProperty("value").EnumerateArray().Single().GetProperty("itemId").GetGuid().ShouldBe(carA);

        // A cancelled booking stays in the list, with what was refunded.
        (await world.SendAsync(HttpMethod.Post, $"/trips/{t1}/items/{stayA}/cancel", token: world.Traveller)).StatusCode.ShouldBe(HttpStatusCode.Accepted);
        List<JsonElement> afterCancel = await Eventually.MatchesAsync(
            () => ListAsync(PartnerA), list => list.Any(static b => b.GetProperty("status").GetString() == "Cancelled"));
        afterCancel.Single(static b => b.GetProperty("status").GetString() == "Cancelled").GetProperty("refunded").GetProperty("amount").GetString().ShouldBe("300.00");

        // Not a booking yet, or never one.
        mine.Select(static b => b.GetProperty("itemId").GetGuid()).ShouldNotContain(stayFailed);
        mine.Select(static b => b.GetProperty("itemId").GetGuid()).ShouldNotContain(stayCapturing);

        // The wrong kind of caller is refused before the query; a missing token is 401.
        (await world.SendAsync(HttpMethod.Get, "/partner/bookings", token: world.Traveller)).StatusCode.ShouldBe(HttpStatusCode.Forbidden);
        (await world.SendAsync(HttpMethod.Get, "/partner/bookings")).StatusCode.ShouldBe(HttpStatusCode.Unauthorized);

        await world.Owners.ResumeAsync<PaymentCaptureRequested>();
        await world.WaitForTripAsync(t4, "Confirmed");
    }

    private async Task<List<JsonElement>> ListAsync(Guid partner)
    {
        HttpResponseMessage response = await world.SendAsync(HttpMethod.Get, "/partner/bookings?$top=100", token: BookingWorld.PartnerToken(partner));
        response.StatusCode.ShouldBe(HttpStatusCode.OK);
        return [.. (await BookingWorld.JsonAsync(response)).GetProperty("value").EnumerateArray()];
    }

    [Fact]
    public async Task An_anonymous_trip_belongs_to_whoever_holds_its_key_until_a_traveller_claims_it()
    {
        string key = Convert.ToBase64String(System.Security.Cryptography.RandomNumberGenerator.GetBytes(24));
        var withKey = new Dictionary<string, string> { ["Trip-Access-Key"] = key };
        Guid trip = Guid.CreateVersion7();

        // Made up front, with a key of the builder's own making.
        HttpResponseMessage created = await world.SendAsync(HttpMethod.Put, $"/trips/{trip}", headers: withKey);
        created.StatusCode.ShouldBe(HttpStatusCode.Created);
        (await world.SendAsync(HttpMethod.Put, $"/trips/{trip}", headers: withKey)).StatusCode.ShouldBe(HttpStatusCode.OK);
        (await BookingWorld.JsonAsync(created)).GetProperty("claimed").GetBoolean().ShouldBeFalse();

        Guid item = await world.AddStayAsync(trip, headers: withKey);

        // Without the key, or with another, it does not exist.
        (await world.SendAsync(HttpMethod.Get, $"/trips/{trip}")).StatusCode.ShouldBe(HttpStatusCode.NotFound);
        (await world.SendAsync(HttpMethod.Get, $"/trips/{trip}", headers: new Dictionary<string, string> { ["Trip-Access-Key"] = key + "x" })).StatusCode.ShouldBe(HttpStatusCode.NotFound);
        (await world.SendAsync(HttpMethod.Put, $"/trips/{trip}", headers: new Dictionary<string, string> { ["Trip-Access-Key"] = key + "x" })).StatusCode.ShouldBe(HttpStatusCode.Conflict);
        (await world.SendAsync(HttpMethod.Get, $"/trips/{trip}", headers: withKey)).StatusCode.ShouldBe(HttpStatusCode.OK);

        // Signing in is not enough; the key is the proof.
        (await world.SendAsync(HttpMethod.Post, $"/trips/{trip}/claim", token: world.Traveller)).StatusCode.ShouldBe(HttpStatusCode.NotFound);

        HttpResponseMessage claimed = await world.SendAsync(HttpMethod.Post, $"/trips/{trip}/claim", token: world.Traveller, headers: withKey);
        claimed.StatusCode.ShouldBe(HttpStatusCode.OK);
        (await BookingWorld.JsonAsync(claimed)).GetProperty("claimed").GetBoolean().ShouldBeTrue();
        await world.NextAsync(nameof(TripClaimed), trip);

        // Now the token is the way in, the key is worth nothing, and the claim is repeatable by its owner only.
        (await world.SendAsync(HttpMethod.Get, $"/trips/{trip}", token: world.Traveller)).StatusCode.ShouldBe(HttpStatusCode.OK);
        (await world.SendAsync(HttpMethod.Get, $"/trips/{trip}", headers: withKey)).StatusCode.ShouldBe(HttpStatusCode.NotFound);
        (await world.SendAsync(HttpMethod.Post, $"/trips/{trip}/claim", token: world.Traveller, headers: withKey)).StatusCode.ShouldBe(HttpStatusCode.OK);
        (await world.SendAsync(HttpMethod.Post, $"/trips/{trip}/claim", token: BookingWorld.TravellerToken(), headers: withKey)).StatusCode.ShouldBe(HttpStatusCode.NotFound);
        (await world.TripAsync(trip)).GetProperty("items").EnumerateArray().Single().GetProperty("id").GetGuid().ShouldBe(item);
    }

    [Fact]
    public async Task A_traveller_who_checks_out_an_anonymous_trip_with_its_key_takes_it_in_the_same_request()
    {
        string key = Convert.ToBase64String(System.Security.Cryptography.RandomNumberGenerator.GetBytes(24));
        var withKey = new Dictionary<string, string> { ["Trip-Access-Key"] = key };
        Guid trip = Guid.CreateVersion7();
        await world.SendAsync(HttpMethod.Put, $"/trips/{trip}", headers: withKey);
        await world.AddStayAsync(trip, headers: withKey);

        (await world.CheckoutAsync(trip)).StatusCode.ShouldBe(HttpStatusCode.NotFound);

        HttpResponseMessage accepted = await world.SendAsync(HttpMethod.Put, $"/trips/{trip}/checkout", BookingWorld.Details(), world.Traveller, withKey);
        accepted.StatusCode.ShouldBe(HttpStatusCode.Accepted);
        await world.WaitForTripAsync(trip, "Confirmed");
        (await world.NextAsync(nameof(TripClaimed), trip)).DataString("travellerId").ShouldBe(world.TravellerId.ToString());
    }

    [Fact]
    public async Task An_anonymous_trip_needs_a_key_of_its_own_and_a_taken_id_is_a_conflict()
    {
        HttpResponseMessage noKey = await world.SendAsync(HttpMethod.Put, $"/trips/{Guid.CreateVersion7()}");
        noKey.StatusCode.ShouldBe(HttpStatusCode.BadRequest);
        (await BookingWorld.JsonAsync(noKey)).GetProperty("error").GetProperty("details")[0].GetProperty("code").GetString().ShouldBe("AccessKeyRequired");

        HttpResponseMessage shortKey = await world.SendAsync(
            HttpMethod.Put, $"/trips/{Guid.CreateVersion7()}", headers: new Dictionary<string, string> { ["Trip-Access-Key"] = "too-short" });
        shortKey.StatusCode.ShouldBe(HttpStatusCode.BadRequest);

        Guid mine = await world.NewTripAsync();
        HttpResponseMessage taken = await world.SendAsync(HttpMethod.Put, $"/trips/{mine}", token: BookingWorld.TravellerToken());
        taken.StatusCode.ShouldBe(HttpStatusCode.Conflict);
        (await BookingWorld.ErrorCodeAsync(taken)).ShouldBe("TripAlreadyExists");
        (await world.SendAsync(HttpMethod.Put, $"/trips/{mine}", token: world.Traveller)).StatusCode.ShouldBe(HttpStatusCode.OK);
    }
}
