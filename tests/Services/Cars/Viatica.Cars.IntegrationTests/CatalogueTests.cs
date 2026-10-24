using System.Net;
using System.Text.Json.Nodes;
using Viatica.Testing;

namespace Viatica.Cars.IntegrationTests;

public sealed class CatalogueTests(PostgresFixture postgres, KafkaFixture kafka, ValkeyFixture valkey) : IAsyncLifetime
{
    private CarsHost host = null!;

    public async ValueTask InitializeAsync() => host = await CarsHost.StartAsync(postgres, kafka, valkey);

    public ValueTask DisposeAsync()
    {
        host.Dispose();
        return ValueTask.CompletedTask;
    }

    private static async Task<IReadOnlyList<ProbedMessage>> EventsAsync(CarsHost host, string type, Guid locationId, int count) =>
        await host.Probe.WaitForCountAsync(count, e => e.Type == type && e.DataString("locationId") == locationId.ToString());

    [Fact]
    public async Task A_location_is_created_as_a_draft_with_an_etag_and_a_location_header()
    {
        HttpClient partner = host.Partner(Guid.NewGuid());
        Guid id = Guid.CreateVersion7();

        HttpResponseMessage created = await partner.PutAsync($"/cars/locations/{id}", Scenario.Location());

        await created.AssertStatusAsync(HttpStatusCode.Created);
        created.Headers.ETag.ShouldNotBeNull();
        created.Headers.Location!.ToString().ShouldContain($"/cars/locations/{id}");
        JsonNode body = await created.JsonAsync();
        ((string)body["status"]!).ShouldBe("Draft");
        ((string)body["airportCode"]!).ShouldBe("LIS");
        body["openingHours"]!.AsArray().Count.ShouldBe(6);

        HttpResponseMessage read = await partner.FetchAsync($"/cars/locations/{id}");
        await read.AssertStatusAsync(HttpStatusCode.OK);
        read.ETag().ShouldBe(created.ETag());
        ((string)(await read.JsonAsync())["name"]!).ShouldBe("Lisbon Airport");
    }

    [Fact]
    public async Task A_draft_announces_nothing()
    {
        HttpClient partner = host.Partner(Guid.NewGuid());
        Guid id = Guid.CreateVersion7();
        await Scenario.Ok(partner.PutAsync($"/cars/locations/{id}", Scenario.Location()));

        // A published sibling gives the probe something to wait for; the draft must not appear before or after it.
        Guid published = Guid.CreateVersion7();
        await Scenario.Ok(partner.PutAsync($"/cars/locations/{published}", Scenario.Location("Faro")));
        await Scenario.Ok(partner.PostAsync($"/cars/locations/{published}/publish"));
        await EventsAsync(host, "CarLocationUpdated", published, 1);

        host.Probe.Received.ShouldNotContain(e => e.DataString("locationId") == id.ToString());
    }

    [Fact]
    public async Task Putting_the_same_location_again_is_a_replace_and_a_stale_etag_is_refused()
    {
        HttpClient partner = host.Partner(Guid.NewGuid());
        Guid id = Guid.CreateVersion7();
        HttpResponseMessage created = await Scenario.Ok(partner.PutAsync($"/cars/locations/{id}", Scenario.Location()));

        HttpResponseMessage replaced = await partner.PutAsync($"/cars/locations/{id}", Scenario.Location("Lisbon Airport T2"), created.ETag());
        await replaced.AssertStatusAsync(HttpStatusCode.OK);
        replaced.ETag().ShouldNotBe(created.ETag());

        HttpResponseMessage stale = await partner.PutAsync($"/cars/locations/{id}", Scenario.Location("Lisbon Airport T3"), created.ETag());
        await stale.AssertStatusAsync(HttpStatusCode.PreconditionFailed, "PreconditionFailed");

        ((string)(await (await partner.FetchAsync($"/cars/locations/{id}")).JsonAsync())["name"]!).ShouldBe("Lisbon Airport T2");
    }

    [Fact]
    public async Task A_change_of_opening_hours_moves_the_etag_even_when_nothing_else_changed()
    {
        HttpClient partner = host.Partner(Guid.NewGuid());
        Guid id = Guid.CreateVersion7();
        HttpResponseMessage created = await Scenario.Ok(partner.PutAsync($"/cars/locations/{id}", Scenario.Location()));

        object[] shorter = [new { day = "Monday", opens = "08:00:00", closes = "12:00:00" }];
        HttpResponseMessage changed = await partner.PutAsync($"/cars/locations/{id}", Scenario.Location(hours: shorter), created.ETag());

        await changed.AssertStatusAsync(HttpStatusCode.OK);
        changed.ETag().ShouldNotBe(created.ETag());
        ((JsonArray)(await changed.JsonAsync())["openingHours"]!).Count.ShouldBe(1);
    }

    [Fact]
    public async Task A_put_without_if_match_is_428_and_an_etag_for_a_resource_that_does_not_exist_is_412()
    {
        HttpClient partner = host.Partner(Guid.NewGuid());
        Guid id = Guid.CreateVersion7();

        await (await partner.PutAsync($"/cars/locations/{id}", Scenario.Location(), ifMatch: null))
            .AssertStatusAsync(HttpStatusCode.PreconditionRequired, "PreconditionRequired");
        await (await partner.PutAsync($"/cars/locations/{id}", Scenario.Location(), ifMatch: "\"12345\""))
            .AssertStatusAsync(HttpStatusCode.PreconditionFailed, "PreconditionFailed");
    }

    [Fact]
    public async Task Publishing_announces_the_whole_listing_with_a_version_and_every_change_raises_it()
    {
        Guid partnerId = Guid.NewGuid();
        HttpClient partner = host.Partner(partnerId);
        Guid category = Guid.CreateVersion7();
        Guid id = Guid.CreateVersion7();
        await Scenario.Ok(partner.PutAsync($"/cars/categories/{category}", Scenario.Category()));
        await Scenario.Ok(partner.PostAsync($"/cars/categories/{category}/publish"));
        await Scenario.Ok(partner.PutAsync($"/cars/locations/{id}", Scenario.Location()));

        await Scenario.Ok(partner.PostAsync($"/cars/locations/{id}/publish"));
        string etag = (await partner.FetchAsync($"/cars/locations/{id}")).ETag();
        await Scenario.Ok(partner.PutAsync($"/cars/locations/{id}", Scenario.Location("Lisbon Airport T2"), etag));
        await Scenario.Ok(partner.PostAsync($"/cars/locations/{id}/unpublish"));

        IReadOnlyList<ProbedMessage> updates = await EventsAsync(host, "CarLocationUpdated", id, 2);
        ProbedMessage withdrawn = (await EventsAsync(host, "CarLocationUnpublished", id, 1))[0];

        updates[0].DataString("listingVersion").ShouldBe("1");
        updates[1].DataString("listingVersion").ShouldBe("2");
        withdrawn.DataString("listingVersion").ShouldBe("3");
        updates[0].DataString("name").ShouldBe("Lisbon Airport");
        updates[1].DataString("name").ShouldBe("Lisbon Airport T2");
        updates[0].DataString("partnerId").ShouldBe(partnerId.ToString());
        updates[0].Key.ShouldBe(id.ToString("N"));
        updates[0].Data.GetProperty("categories")[0].GetProperty("acriss").GetString().ShouldBe("ECMN");
        updates[0].Data.GetProperty("categories")[0].GetProperty("transmission").GetString().ShouldBe("Manual");
        updates[0].Data.GetProperty("openingHours").GetArrayLength().ShouldBe(6);
    }

    [Fact]
    public async Task Publishing_twice_announces_once()
    {
        HttpClient partner = host.Partner(Guid.NewGuid());
        Guid id = Guid.CreateVersion7();
        await Scenario.Ok(partner.PutAsync($"/cars/locations/{id}", Scenario.Location()));

        await Scenario.Ok(partner.PostAsync($"/cars/locations/{id}/publish"));
        await Scenario.Ok(partner.PostAsync($"/cars/locations/{id}/publish"));
        await Scenario.Ok(partner.PostAsync($"/cars/locations/{id}/unpublish"));
        await EventsAsync(host, "CarLocationUnpublished", id, 1);

        host.Probe.Received.Count(e => e.Type == "CarLocationUpdated" && e.DataString("locationId") == id.ToString()).ShouldBe(1);
    }

    [Fact]
    public async Task A_location_without_opening_hours_cannot_be_published()
    {
        HttpClient partner = host.Partner(Guid.NewGuid());
        Guid id = Guid.CreateVersion7();
        await Scenario.Ok(partner.PutAsync($"/cars/locations/{id}", Scenario.Location(hours: [])));

        HttpResponseMessage response = await partner.PostAsync($"/cars/locations/{id}/publish");

        await response.AssertStatusAsync(HttpStatusCode.Conflict, "NotPublishable");
        ((string)(await (await partner.FetchAsync($"/cars/locations/{id}")).JsonAsync())["status"]!).ShouldBe("Draft");
    }

    [Fact]
    public async Task Publishing_a_category_adds_it_to_every_published_location_of_the_partner()
    {
        HttpClient partner = host.Partner(Guid.NewGuid());
        Guid first = Guid.CreateVersion7();
        Guid second = Guid.CreateVersion7();
        Guid category = Guid.CreateVersion7();
        foreach (Guid location in new[] { first, second })
        {
            await Scenario.Ok(partner.PutAsync($"/cars/locations/{location}", Scenario.Location()));
            await Scenario.Ok(partner.PostAsync($"/cars/locations/{location}/publish"));
        }

        await Scenario.Ok(partner.PutAsync($"/cars/categories/{category}", Scenario.Category()));
        await Scenario.Ok(partner.PostAsync($"/cars/categories/{category}/publish"));

        foreach (Guid location in new[] { first, second })
        {
            IReadOnlyList<ProbedMessage> updates = await EventsAsync(host, "CarLocationUpdated", location, 2);
            updates[0].Data.GetProperty("categories").GetArrayLength().ShouldBe(0);
            updates[1].Data.GetProperty("categories").GetArrayLength().ShouldBe(1);
            updates[1].DataString("listingVersion").ShouldBe("2");
        }
    }

    [Fact]
    public async Task An_edit_to_a_published_category_reaches_the_locations_that_list_it()
    {
        Marketplace m = await Scenario.ArrangeAsync(host);
        HttpResponseMessage category = await m.Client.FetchAsync($"/cars/categories/{m.CategoryId}");

        await Scenario.Ok(m.Client.PutAsync($"/cars/categories/{m.CategoryId}", Scenario.Category(name: "Economy plus"), category.ETag()));

        IReadOnlyList<ProbedMessage> updates = await host.Probe.WaitForCountAsync(
            1, e => e.Type == "CarLocationUpdated" && e.DataString("locationId") == m.LocationId.ToString()
                && e.Data.GetProperty("categories")[0].GetProperty("name").GetString() == "Economy plus");
        updates.ShouldNotBeEmpty();
    }

    [Fact]
    public async Task Another_partner_sees_404_and_changes_nothing_on_a_location_that_is_not_theirs()
    {
        Marketplace owner = await Scenario.ArrangeAsync(host);
        HttpClient intruder = host.Partner(Guid.NewGuid());
        string path = $"/cars/locations/{owner.LocationId}";

        await (await intruder.FetchAsync(path)).AssertStatusAsync(HttpStatusCode.NotFound, "LocationNotFound");
        await (await intruder.PutAsync(path, Scenario.Location("Hijacked"))).AssertStatusAsync(HttpStatusCode.NotFound, "LocationNotFound");
        await (await intruder.PostAsync(path + "/unpublish")).AssertStatusAsync(HttpStatusCode.NotFound, "LocationNotFound");
        await (await intruder.PostAsync(path + "/publish")).AssertStatusAsync(HttpStatusCode.NotFound, "LocationNotFound");

        JsonNode mine = await (await owner.Client.FetchAsync(path)).JsonAsync();
        ((string)mine["name"]!).ShouldBe("Lisbon Airport");
        ((string)mine["status"]!).ShouldBe("Published");
    }

    [Fact]
    public async Task Another_partner_sees_404_on_a_category_and_cannot_touch_its_inventory()
    {
        Marketplace owner = await Scenario.ArrangeAsync(host);
        HttpClient intruder = host.Partner(Guid.NewGuid());
        Marketplace other = await Scenario.ArrangeAsync(host);

        await (await intruder.FetchAsync($"/cars/categories/{owner.CategoryId}")).AssertStatusAsync(HttpStatusCode.NotFound, "CategoryNotFound");
        await (await intruder.PutAsync($"/cars/categories/{owner.CategoryId}", Scenario.Category())).AssertStatusAsync(HttpStatusCode.NotFound, "CategoryNotFound");
        await (await intruder.PostAsync($"/cars/categories/{owner.CategoryId}/unpublish")).AssertStatusAsync(HttpStatusCode.NotFound, "CategoryNotFound");

        string inventory = $"/cars/locations/{owner.LocationId}/categories/{owner.CategoryId}/inventory";
        await (await intruder.PostAsync(inventory + "/bulk", Scenario.Inventory("2026-12-01", "2026-12-02", 99, 1m)))
            .AssertStatusAsync(HttpStatusCode.NotFound, "LocationNotFound");
        await (await intruder.FetchAsync(inventory, "from=2026-12-01&to=2026-12-02")).AssertStatusAsync(HttpStatusCode.NotFound, "LocationNotFound");

        // Their own location with someone else's category is the category that does not exist for them.
        await (await other.Client.PostAsync($"/cars/locations/{other.LocationId}/categories/{owner.CategoryId}/inventory/bulk", Scenario.Inventory("2026-12-01", "2026-12-02", 99, 1m)))
            .AssertStatusAsync(HttpStatusCode.NotFound, "CategoryNotFound");
    }

    [Fact]
    public async Task Lists_show_only_the_callers_own_and_page_with_a_next_link()
    {
        Guid partnerId = Guid.NewGuid();
        HttpClient partner = host.Partner(partnerId);
        foreach (string name in new[] { "Alpha", "Bravo", "Charlie" })
        {
            await Scenario.Ok(partner.PutAsync($"/cars/locations/{Guid.CreateVersion7()}", Scenario.Location(name)));
        }

        await Scenario.ArrangeAsync(host);   // another partner's locations must not appear

        JsonNode first = await (await partner.FetchAsync("/cars/locations", "$top=2")).JsonAsync();
        first["value"]!.AsArray().Select(static l => (string)l!["name"]!).ShouldBe(["Alpha", "Bravo"]);
        string next = (string)first["nextLink"]!;

        // nextLink is followed as is: it already carries the api-version and the signed skiptoken.
        JsonNode rest = await (await partner.GetAsync(new Uri(next))).JsonAsync();
        rest["value"]!.AsArray().Select(static l => (string)l!["name"]!).ShouldBe(["Charlie"]);
        rest["nextLink"].ShouldBeNull();
    }

    [Fact]
    public async Task A_filter_outside_the_grammar_is_a_400_and_a_known_one_filters()
    {
        HttpClient partner = host.Partner(Guid.NewGuid());
        await Scenario.Ok(partner.PutAsync($"/cars/locations/{Guid.CreateVersion7()}", Scenario.Location("Alpha")));

        await (await partner.FetchAsync("/cars/locations", "$filter=name eq 'Alpha' or 1 eq 1")).AssertStatusAsync(HttpStatusCode.BadRequest, "InvalidFilter");
        await (await partner.FetchAsync("/cars/locations", "$filter=partnerId eq 'x'")).AssertStatusAsync(HttpStatusCode.BadRequest, "InvalidFilter");

        JsonNode filtered = await (await partner.FetchAsync("/cars/locations", "$filter=status eq 'Published'")).JsonAsync();
        filtered["value"]!.AsArray().Count.ShouldBe(0);
    }

    [Fact]
    public async Task Bad_input_is_a_400_with_one_detail_per_field()
    {
        HttpClient partner = host.Partner(Guid.NewGuid());

        HttpResponseMessage response = await partner.PutAsync(
            $"/cars/categories/{Guid.CreateVersion7()}",
            new { acriss = "EC1N", name = " ", seats = 0, transmission = "Manual", fuel = "Petrol", bags = 2, minimumDriverAge = 17 });

        await response.AssertStatusAsync(HttpStatusCode.BadRequest, "InvalidRequest");
        JsonNode details = (await response.JsonAsync())["error"]!["details"]!;
        details.AsArray().Select(static d => (string)d!["code"]!).ShouldBe(
            ["InvalidAcriss", "InvalidName", "InvalidSeats", "InvalidMinimumDriverAge"], ignoreOrder: true);
        details.AsArray().Select(static d => (string)d!["target"]!).ShouldContain("acriss");
    }

    [Fact]
    public async Task Partner_endpoints_need_a_partner_token()
    {
        string path = $"/cars/locations/{Guid.CreateVersion7()}";

        await (await host.Anonymous().PutAsync(path, Scenario.Location())).AssertStatusAsync(HttpStatusCode.Unauthorized, "Unauthorized");
        await (await host.Traveller().PutAsync(path, Scenario.Location())).AssertStatusAsync(HttpStatusCode.Forbidden, "Forbidden");
        await (await host.Traveller().FetchAsync("/cars/locations")).AssertStatusAsync(HttpStatusCode.Forbidden, "Forbidden");
    }

    [Fact]
    public async Task A_request_without_an_api_version_is_a_400()
    {
        using HttpClient partner = host.Partner(Guid.NewGuid());

        using HttpResponseMessage bare = await partner.GetAsync(new Uri("/cars/locations", UriKind.Relative));

        await bare.AssertStatusAsync(HttpStatusCode.BadRequest, "MissingApiVersionParameter");
    }

    [Fact]
    public async Task Partner_settings_start_at_zero_and_are_changed_with_if_match()
    {
        HttpClient partner = host.Partner(Guid.NewGuid());

        HttpResponseMessage initial = await partner.FetchAsync("/cars/partner/settings");
        await initial.AssertStatusAsync(HttpStatusCode.OK);
        initial.Headers.ETag.ShouldBeNull();
        ((string)(await initial.JsonAsync())["oneWayFee"]!["amount"]!).ShouldBe("0.00");

        var settings = new { oneWayFee = Scenario.Money(25m), youngDriverSurchargePerDay = Scenario.Money(12m) };
        HttpResponseMessage saved = await partner.PutAsync("/cars/partner/settings", settings, "*");
        await saved.AssertStatusAsync(HttpStatusCode.Created);
        saved.Headers.ETag.ShouldNotBeNull();

        var raised = new { oneWayFee = Scenario.Money(30m), youngDriverSurchargePerDay = Scenario.Money(12m) };
        await (await partner.PutAsync("/cars/partner/settings", raised, "\"1\"")).AssertStatusAsync(HttpStatusCode.PreconditionFailed);
        await (await partner.PutAsync("/cars/partner/settings", raised, saved.ETag())).AssertStatusAsync(HttpStatusCode.OK);
        ((string)(await (await partner.FetchAsync("/cars/partner/settings")).JsonAsync())["oneWayFee"]!["amount"]!).ShouldBe("30.00");
    }
}
