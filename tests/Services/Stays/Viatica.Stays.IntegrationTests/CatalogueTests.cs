using System.Net;
using System.Text.Json;
using Viatica.Stays.Application.Features.Properties;
using Viatica.Stays.Domain;
using Viatica.Testing;

namespace Viatica.Stays.IntegrationTests;

/// <summary>Properties and room types over HTTP: ownership, the ETag rules, publication and the events it raises.</summary>
public sealed class CatalogueTests(StaysFixture stays) : IClassFixture<StaysFixture>
{
    private StaysHost Host => stays.Host;

    private static async Task<(Guid PropertyId, Guid Partner, string Token)> CreateDraftAsync(StaysHost host)
    {
        Guid partner = Guid.CreateVersion7();
        Guid id = Guid.CreateVersion7();
        string token = StaysHost.PartnerToken(partner);
        using HttpResponseMessage created = await host.SendAsync(HttpMethod.Put, $"/properties/{id}", Scenario.PropertyBody(), token);
        created.StatusCode.ShouldBe(HttpStatusCode.Created);
        return (id, partner, token);
    }

    [Fact]
    public async Task A_created_property_is_a_draft_with_an_etag_and_a_location()
    {
        Guid id = Guid.CreateVersion7();
        string token = StaysHost.PartnerToken(Guid.CreateVersion7());

        using HttpResponseMessage response = await Host.SendAsync(HttpMethod.Put, $"/properties/{id}", Scenario.PropertyBody(), token);

        response.StatusCode.ShouldBe(HttpStatusCode.Created);
        response.Headers.ETag.ShouldNotBeNull();
        response.Headers.Location!.ToString().ShouldContain($"/properties/{id}");
        response.Headers.Location.Query.ShouldContain("api-version=" + StaysHost.Version);
        PropertyView view = await StaysHost.ReadAsync<PropertyView>(response);
        view.Id.ShouldBe(id);
        view.Status.ShouldBe(PropertyStatus.Draft);
        view.ListingVersion.ShouldBe(1);
        view.Name.ShouldBe("Casa do Mar");
        view.RoomTypes.ShouldBeEmpty();
    }

    [Fact]
    public async Task Putting_the_same_property_again_returns_it_and_putting_a_different_one_is_a_conflict()
    {
        (Guid id, _, string token) = await CreateDraftAsync(Host);

        using HttpResponseMessage retry = await Host.SendAsync(HttpMethod.Put, $"/properties/{id}", Scenario.PropertyBody(), token);
        retry.StatusCode.ShouldBe(HttpStatusCode.OK);

        using HttpResponseMessage different = await Host.SendAsync(HttpMethod.Put, $"/properties/{id}", Scenario.PropertyBody("Another name"), token);
        different.StatusCode.ShouldBe(HttpStatusCode.Conflict);
        (await StaysHost.ErrorCodeAsync(different)).ShouldBe(StayErrorCode.AlreadyExists);
    }

    [Fact]
    public async Task Creations_racing_for_one_id_make_one_property_and_never_a_500()
    {
        Guid id = Guid.CreateVersion7();
        string token = StaysHost.PartnerToken(Guid.CreateVersion7());

        HttpResponseMessage[] responses = await Task.WhenAll(Enumerable.Range(0, 8).Select(_ =>
            Host.SendAsync(HttpMethod.Put, $"/properties/{id}", Scenario.PropertyBody(), token)));

        // The one that wins is 201. The rest either arrive after it and are told it exists (200), or collide with it
        // in the database and are told to read and retry (409, from the unique violation on the primary key).
        responses.Count(static r => r.StatusCode == HttpStatusCode.Created).ShouldBe(1);
        responses.ShouldAllBe(static r => r.StatusCode == HttpStatusCode.Created || r.StatusCode == HttpStatusCode.OK || r.StatusCode == HttpStatusCode.Conflict);
        (await Host.GetAsync($"/properties/{id}", token)).StatusCode.ShouldBe(HttpStatusCode.OK);
    }

    [Fact]
    public async Task An_invalid_body_is_one_400_that_names_every_field()
    {
        Guid id = Guid.CreateVersion7();
        string token = StaysHost.PartnerToken(Guid.CreateVersion7());
        object body = new
        {
            name = "",
            description = "x",
            address = new { line1 = "Rua 1", city = "Lisbon", postalCode = "1200", countryCode = "Portugal" },
            location = new { latitude = 91.0, longitude = 0.0 },
            starRating = 7,
            amenities = Array.Empty<string>(),
            checkInFrom = "15:00:00",
            checkOutUntil = "11:00:00",
            cancellationPolicy = new { freeUntilHoursBeforeStart = 24, penaltyAfter = "FullAmount" },
        };

        using HttpResponseMessage response = await Host.SendAsync(HttpMethod.Put, $"/properties/{id}", body, token);

        response.StatusCode.ShouldBe(HttpStatusCode.BadRequest);
        JsonElement error = (await StaysHost.JsonAsync(response)).GetProperty("error");
        error.GetProperty("code").GetString().ShouldBe("InvalidRequest");
        error.GetProperty("details").EnumerateArray().Select(static d => d.GetProperty("target").GetString())
            .ShouldBe(["name", "starRating", "address.countryCode", "location.latitude"], ignoreOrder: true);
    }

    [Fact]
    public async Task A_body_missing_a_required_member_does_not_bind()
    {
        string token = StaysHost.PartnerToken(Guid.CreateVersion7());

        using HttpResponseMessage response = await Host.SendAsync(HttpMethod.Put, $"/properties/{Guid.CreateVersion7()}", new { name = "Only a name" }, token);

        response.StatusCode.ShouldBe(HttpStatusCode.BadRequest);
        (await StaysHost.ErrorCodeAsync(response)).ShouldBe("InvalidRequest");
    }

    [Fact]
    public async Task Creating_needs_a_partner_token()
    {
        string path = $"/properties/{Guid.CreateVersion7()}";

        (await Host.SendAsync(HttpMethod.Put, path, Scenario.PropertyBody())).StatusCode.ShouldBe(HttpStatusCode.Unauthorized);
        (await Host.SendAsync(HttpMethod.Put, path, Scenario.PropertyBody(), StaysHost.TravellerToken())).StatusCode.ShouldBe(HttpStatusCode.Forbidden);
    }

    [Fact]
    public async Task A_draft_is_visible_to_its_partner_and_an_admin_and_to_nobody_else()
    {
        (Guid id, _, string owner) = await CreateDraftAsync(Host);
        string path = $"/properties/{id}";

        (await Host.GetAsync(path, owner)).StatusCode.ShouldBe(HttpStatusCode.OK);
        (await Host.GetAsync(path, StaysHost.AdminToken())).StatusCode.ShouldBe(HttpStatusCode.OK);

        foreach (string? token in new[] { null, StaysHost.TravellerToken(), StaysHost.PartnerToken(Guid.CreateVersion7()) })
        {
            using HttpResponseMessage hidden = await Host.GetAsync(path, token);
            hidden.StatusCode.ShouldBe(HttpStatusCode.NotFound);
            (await StaysHost.ErrorCodeAsync(hidden)).ShouldBe(StayErrorCode.PropertyNotFound);
        }
    }

    [Fact]
    public async Task Another_partner_cannot_touch_a_property_and_is_told_it_does_not_exist()
    {
        var scenario = await Scenario.CreateAsync(Host, publish: false);
        string rival = StaysHost.PartnerToken(Guid.CreateVersion7());
        PropertyView before = await scenario.GetPropertyAsync();
        string etag = StaysHost.ETagOf(await Host.GetAsync(scenario.PropertyPath, scenario.PartnerToken));

        var attempts = new (HttpMethod Method, string Path, object? Body, string? IfMatch)[]
        {
            (HttpMethod.Patch, scenario.PropertyPath, new { name = "Hijacked" }, "*"),
            (HttpMethod.Post, $"{scenario.PropertyPath}/publish", null, "*"),
            (HttpMethod.Post, $"{scenario.PropertyPath}/unpublish", null, "*"),
            (HttpMethod.Put, $"{scenario.PropertyPath}/room-types/{Guid.CreateVersion7()}", Scenario.RoomBody(), null),
            (HttpMethod.Patch, scenario.RoomPath, new { name = "Hijacked" }, "*"),
            (HttpMethod.Post, $"{scenario.RoomPath}/inventory:set", new { from = StaysHost.CheckIn, to = StaysHost.CheckOut, allotment = 0 }, null),
            (HttpMethod.Get, $"{scenario.RoomPath}/inventory?from={StaysHost.CheckIn:yyyy-MM-dd}&to={StaysHost.CheckOut:yyyy-MM-dd}", null, null),
            (HttpMethod.Get, scenario.PropertyPath, null, null),
            (HttpMethod.Get, scenario.RoomPath, null, null),
        };

        foreach ((HttpMethod method, string path, object? body, string? ifMatch) in attempts)
        {
            using HttpResponseMessage response = await Host.SendAsync(method, path, body, rival, ifMatch);
            response.StatusCode.ShouldBe(HttpStatusCode.NotFound, $"{method} {path}");
            (await StaysHost.ErrorCodeAsync(response)).ShouldBeOneOf(StayErrorCode.PropertyNotFound, StayErrorCode.RoomTypeNotFound);
        }

        // Nothing changed, and the owner's ETag still works.
        PropertyView after = await scenario.GetPropertyAsync();
        StaysHost.Json(after).ShouldBe(StaysHost.Json(before));
        StaysHost.ETagOf(await Host.GetAsync(scenario.PropertyPath, scenario.PartnerToken)).ShouldBe(etag);
    }

    [Fact]
    public async Task Putting_a_property_under_an_id_another_partner_owns_answers_not_found()
    {
        (Guid id, _, _) = await CreateDraftAsync(Host);

        using HttpResponseMessage response = await Host.SendAsync(
            HttpMethod.Put, $"/properties/{id}", Scenario.PropertyBody(), StaysHost.PartnerToken(Guid.CreateVersion7()));

        response.StatusCode.ShouldBe(HttpStatusCode.NotFound);
    }

    [Fact]
    public async Task An_update_without_if_match_is_428()
    {
        (Guid id, _, string token) = await CreateDraftAsync(Host);

        using HttpResponseMessage response = await Host.SendAsync(HttpMethod.Patch, $"/properties/{id}", new { name = "New" }, token);

        response.StatusCode.ShouldBe((HttpStatusCode)428);
        (await StaysHost.ErrorCodeAsync(response)).ShouldBe("PreconditionRequired");
    }

    [Fact]
    public async Task A_stale_weak_or_foreign_etag_is_412_and_a_current_one_updates_and_moves_the_etag()
    {
        (Guid id, _, string token) = await CreateDraftAsync(Host);
        string path = $"/properties/{id}";
        string first = StaysHost.ETagOf(await Host.GetAsync(path, token));

        using HttpResponseMessage updated = await Host.SendAsync(HttpMethod.Patch, path, new { name = "Second name" }, token, first);
        updated.StatusCode.ShouldBe(HttpStatusCode.OK);
        string second = StaysHost.ETagOf(updated);
        second.ShouldNotBe(first);
        (await StaysHost.ReadAsync<PropertyView>(updated)).Name.ShouldBe("Second name");

        foreach (string stale in new[] { first, "W/" + second, "\"1\"", "\"not-a-number\"" })
        {
            using HttpResponseMessage response = await Host.SendAsync(HttpMethod.Patch, path, new { name = "Lost" }, token, stale);
            response.StatusCode.ShouldBe(HttpStatusCode.PreconditionFailed, stale);
            (await StaysHost.ErrorCodeAsync(response)).ShouldBe("PreconditionFailed");
        }

        (await StaysHost.ReadAsync<PropertyView>(await Host.GetAsync(path, token))).Name.ShouldBe("Second name");
        (await Host.SendAsync(HttpMethod.Patch, path, new { name = "Any version" }, token, "*")).StatusCode.ShouldBe(HttpStatusCode.OK);
    }

    [Fact]
    public async Task Two_updates_with_the_same_etag_let_exactly_one_through()
    {
        (Guid id, _, string token) = await CreateDraftAsync(Host);
        string path = $"/properties/{id}";
        string etag = StaysHost.ETagOf(await Host.GetAsync(path, token));

        HttpResponseMessage[] responses = await Task.WhenAll(Enumerable.Range(0, 8).Select(i =>
            Host.SendAsync(HttpMethod.Patch, path, new { name = $"Writer {i}" }, token, etag)));

        responses.Count(static r => r.StatusCode == HttpStatusCode.OK).ShouldBe(1);
        responses.Count(static r => r.StatusCode == HttpStatusCode.PreconditionFailed).ShouldBe(7);
    }

    [Fact]
    public async Task A_property_with_no_room_type_cannot_be_published()
    {
        (Guid id, _, string token) = await CreateDraftAsync(Host);
        string path = $"/properties/{id}";

        using HttpResponseMessage response = await Host.SendAsync(
            HttpMethod.Post, $"{path}/publish", token: token, ifMatch: StaysHost.ETagOf(await Host.GetAsync(path, token)));

        response.StatusCode.ShouldBe(HttpStatusCode.Conflict);
        (await StaysHost.ErrorCodeAsync(response)).ShouldBe(StayErrorCode.NoRoomTypes);
    }

    [Fact]
    public async Task A_room_type_has_its_own_etag_and_the_same_rules()
    {
        var scenario = await Scenario.CreateAsync(Host, publish: false);
        using HttpResponseMessage read = await Host.GetAsync(scenario.RoomPath, scenario.PartnerToken);
        string etag = StaysHost.ETagOf(read);

        (await Host.SendAsync(HttpMethod.Patch, scenario.RoomPath, new { maxOccupancy = 3 }, scenario.PartnerToken)).StatusCode.ShouldBe((HttpStatusCode)428);

        using HttpResponseMessage updated = await Host.SendAsync(HttpMethod.Patch, scenario.RoomPath, new { maxOccupancy = 3 }, scenario.PartnerToken, etag);
        updated.StatusCode.ShouldBe(HttpStatusCode.OK);

        (await Host.SendAsync(HttpMethod.Patch, scenario.RoomPath, new { maxOccupancy = 4 }, scenario.PartnerToken, etag))
            .StatusCode.ShouldBe(HttpStatusCode.PreconditionFailed);
        (await scenario.GetRoomAsync()).MaxOccupancy.ShouldBe(3);
    }

    [Fact]
    public async Task Selling_nights_does_not_disturb_a_room_types_etag()
    {
        // A traveller booking must not make a partner's pending edit fail: the version counter lives elsewhere.
        var scenario = await Scenario.CreateAsync(Host, publish: false);
        string etag = StaysHost.ETagOf(await Host.GetAsync(scenario.RoomPath, scenario.PartnerToken));

        (await scenario.SetNightsAsync(StaysHost.CheckIn, StaysHost.CheckOut, 7)).StatusCode.ShouldBe(HttpStatusCode.OK);

        StaysHost.ETagOf(await Host.GetAsync(scenario.RoomPath, scenario.PartnerToken)).ShouldBe(etag);
    }

    [Fact]
    public async Task Publishing_announces_a_snapshot_and_every_later_change_a_newer_one()
    {
        using EventProbe probe = stays.Probe();
        var scenario = await Scenario.CreateAsync(Host, publish: false);

        // Drafts say nothing: the edits above produced no listing event.
        await scenario.PublishAsync();
        IReadOnlyList<ProbedMessage> first = await scenario.EventsAsync(probe, "StayListingUpdated", 1);
        first[0].Key.ShouldBe(scenario.PropertyId.ToString("N"));
        first[0].Data.GetProperty("name").GetString().ShouldBe("Casa do Mar");
        first[0].Data.GetProperty("roomTypes").GetArrayLength().ShouldBe(1);
        first[0].Data.GetProperty("roomTypes")[0].GetProperty("roomTypeId").GetGuid().ShouldBe(scenario.RoomTypeId);
        first[0].Data.GetProperty("address").GetProperty("city").GetString().ShouldBe("Lisbon");
        first[0].Data.GetProperty("checkInFrom").GetString().ShouldBe("15:00:00");

        string etag = StaysHost.ETagOf(await Host.GetAsync(scenario.PropertyPath, scenario.PartnerToken));
        (await Host.SendAsync(HttpMethod.Patch, scenario.PropertyPath, new { name = "Renamed" }, scenario.PartnerToken, etag)).StatusCode.ShouldBe(HttpStatusCode.OK);
        (await Host.SendAsync(HttpMethod.Patch, scenario.RoomPath, new { name = "Renamed room" }, scenario.PartnerToken, "*")).StatusCode.ShouldBe(HttpStatusCode.OK);
        (await Host.SendAsync(HttpMethod.Put, $"{scenario.PropertyPath}/room-types/{Guid.CreateVersion7()}", Scenario.RoomBody(name: "Suite"), scenario.PartnerToken))
            .StatusCode.ShouldBe(HttpStatusCode.Created);

        IReadOnlyList<ProbedMessage> all = await scenario.EventsAsync(probe, "StayListingUpdated", 4);
        long[] versions = [.. all.Select(static m => Scenario.Long(m, "listingVersion"))];
        versions.ShouldBe([.. versions.Order()]);
        versions.Distinct().Count().ShouldBe(4);
        all[1].Data.GetProperty("name").GetString().ShouldBe("Renamed");
        all[3].Data.GetProperty("roomTypes").GetArrayLength().ShouldBe(2);
    }

    [Fact]
    public async Task Unpublishing_announces_it_with_a_version_above_the_last_snapshot_and_an_admin_may_do_it()
    {
        using EventProbe probe = stays.Probe();
        var scenario = await Scenario.CreateAsync(Host);
        IReadOnlyList<ProbedMessage> snapshot = await scenario.EventsAsync(probe, "StayListingUpdated", 1);

        string etag = StaysHost.ETagOf(await Host.GetAsync(scenario.PropertyPath, scenario.PartnerToken));
        using HttpResponseMessage unpublished = await Host.SendAsync(HttpMethod.Post, $"{scenario.PropertyPath}/unpublish", token: StaysHost.AdminToken(), ifMatch: etag);
        unpublished.StatusCode.ShouldBe(HttpStatusCode.OK);
        (await StaysHost.ReadAsync<PropertyView>(unpublished)).Status.ShouldBe(PropertyStatus.Unpublished);

        IReadOnlyList<ProbedMessage> events = await scenario.EventsAsync(probe, "StayUnpublished", 1);
        Scenario.Long(events[0], "listingVersion").ShouldBeGreaterThan(Scenario.Long(snapshot[0], "listingVersion"));

        // Off sale: the public no longer sees it, its partner still does, and publishing again announces it anew.
        (await Host.GetAsync(scenario.PropertyPath)).StatusCode.ShouldBe(HttpStatusCode.NotFound);
        (await Host.GetAsync(scenario.PropertyPath, scenario.PartnerToken)).StatusCode.ShouldBe(HttpStatusCode.OK);
        await scenario.PublishAsync();
        IReadOnlyList<ProbedMessage> again = await scenario.EventsAsync(probe, "StayListingUpdated", 2);
        Scenario.Long(again[1], "listingVersion").ShouldBeGreaterThan(Scenario.Long(events[0], "listingVersion"));
    }

    [Fact]
    public async Task Only_an_admin_or_the_owner_may_unpublish()
    {
        var scenario = await Scenario.CreateAsync(Host);

        (await Host.SendAsync(HttpMethod.Post, $"{scenario.PropertyPath}/unpublish", token: StaysHost.TravellerToken(), ifMatch: "*"))
            .StatusCode.ShouldBe(HttpStatusCode.Forbidden);
        (await Host.SendAsync(HttpMethod.Post, $"{scenario.PropertyPath}/unpublish", token: StaysHost.PartnerToken(Guid.CreateVersion7()), ifMatch: "*"))
            .StatusCode.ShouldBe(HttpStatusCode.NotFound);
        (await Host.SendAsync(HttpMethod.Post, $"{scenario.PropertyPath}/unpublish", token: scenario.PartnerToken, ifMatch: "*"))
            .StatusCode.ShouldBe(HttpStatusCode.OK);
    }

    [Fact]
    public async Task The_public_list_holds_published_properties_and_the_partners_holds_all_of_theirs_in_pages()
    {
        Guid partner = Guid.CreateVersion7();
        string token = StaysHost.PartnerToken(partner);

        // Three of one partner's own: two drafts and one published.
        var ids = new List<Guid>();
        for (int i = 0; i < 3; i++)
        {
            Guid id = Guid.CreateVersion7();
            ids.Add(id);
            (await Host.SendAsync(HttpMethod.Put, $"/properties/{id}", Scenario.PropertyBody($"Own {i}"), token)).StatusCode.ShouldBe(HttpStatusCode.Created);
        }

        Guid room = Guid.CreateVersion7();
        (await Host.SendAsync(HttpMethod.Put, $"/properties/{ids[0]}/room-types/{room}", Scenario.RoomBody(), token)).StatusCode.ShouldBe(HttpStatusCode.Created);
        (await Host.SendAsync(HttpMethod.Post, $"/properties/{ids[0]}/publish", token: token, ifMatch: "*")).StatusCode.ShouldBe(HttpStatusCode.OK);

        // The partner's list, two at a time, follows nextLink to the end.
        var seen = new List<Guid>();
        string? path = "/partner/properties?$top=2&$orderby=name";
        while (path is not null)
        {
            using HttpResponseMessage page = await Host.SendAsync(HttpMethod.Get, path, token: token, withVersion: !path.Contains("api-version", StringComparison.Ordinal));
            page.StatusCode.ShouldBe(HttpStatusCode.OK);
            JsonElement body = await StaysHost.JsonAsync(page);
            seen.AddRange(body.GetProperty("value").EnumerateArray().Select(static p => p.GetProperty("id").GetGuid()));
            path = body.TryGetProperty("nextLink", out JsonElement next) ? new Uri(next.GetString()!).PathAndQuery : null;
        }

        seen.Order().ShouldBe(ids.Order());

        // Draft rows are filtered by a field the list declares; an undeclared one is refused, not ignored.
        using HttpResponseMessage drafts = await Host.SendAsync(HttpMethod.Get, "/partner/properties?$filter=status eq 'Draft'", token: token);
        (await StaysHost.JsonAsync(drafts)).GetProperty("value").GetArrayLength().ShouldBe(2);
        (await Host.SendAsync(HttpMethod.Get, "/properties?$filter=partnerId eq '" + partner + "'")).StatusCode.ShouldBe(HttpStatusCode.BadRequest);

        // The public list shows the published ones only, and never a draft.
        using HttpResponseMessage everyone = await Host.SendAsync(HttpMethod.Get, "/properties?$top=100");
        Guid[] publicIds = [.. (await StaysHost.JsonAsync(everyone)).GetProperty("value").EnumerateArray().Select(static p => p.GetProperty("id").GetGuid())];
        publicIds.ShouldContain(ids[0]);
        publicIds.ShouldNotContain(ids[1]);
        publicIds.ShouldNotContain(ids[2]);
    }

    [Fact]
    public async Task Every_request_needs_an_api_version()
    {
        (await Host.SendAsync(HttpMethod.Get, "/properties", withVersion: false)).StatusCode.ShouldBe(HttpStatusCode.BadRequest);
    }

    [Fact]
    public async Task Health_answers_without_a_version_or_a_token()
    {
        (await Host.SendAsync(HttpMethod.Get, "/health/live", withVersion: false)).StatusCode.ShouldBe(HttpStatusCode.OK);
        (await Host.SendAsync(HttpMethod.Get, "/health/ready", withVersion: false)).StatusCode.ShouldBe(HttpStatusCode.OK);
    }

    [Fact]
    public async Task The_openapi_document_answers_without_a_version_or_a_token_and_describes_the_routes()
    {
        using HttpResponseMessage response = await Host.SendAsync(HttpMethod.Get, "/openapi/v1.json", withVersion: false);

        response.StatusCode.ShouldBe(HttpStatusCode.OK);
        JsonElement paths = (await StaysHost.JsonAsync(response)).GetProperty("paths");
        paths.TryGetProperty("/properties/{propertyId}", out _).ShouldBeTrue();
        paths.TryGetProperty("/properties/{propertyId}/room-types/{roomTypeId}/quotes", out _).ShouldBeTrue();
    }
}
