using System.Net;
using System.Text.Json;
using Viatica.Testing;

namespace Viatica.Booking.IntegrationTests;

/// <summary>The edges of the API: versioning, authentication, validation, and the draft's own rules (docs/api/conventions.md).</summary>
public sealed class ApiSurfaceTests(WorldFixture fixture) : IClassFixture<WorldFixture>, IAsyncLifetime
{
    private BookingWorld world => fixture.World;

    public async ValueTask InitializeAsync() => await world.Owners.ResumeAllAsync();

    public ValueTask DisposeAsync() => ValueTask.CompletedTask;

    [Fact]
    public async Task Every_endpoint_wants_an_api_version_and_the_right_kind_of_caller()
    {
        Guid trip = await world.NewTripAsync();

        HttpResponseMessage unversioned = await world.SendAsync(HttpMethod.Get, $"/trips/{trip}", token: world.Traveller, withVersion: false);
        unversioned.StatusCode.ShouldBe(HttpStatusCode.BadRequest);
        (await BookingWorld.ErrorCodeAsync(unversioned)).ShouldBe("MissingApiVersionParameter");

        (await world.SendAsync(HttpMethod.Put, $"/trips/{trip}/checkout", BookingWorld.Details())).StatusCode.ShouldBe(HttpStatusCode.Unauthorized);
        (await world.SendAsync(HttpMethod.Put, $"/trips/{trip}/checkout", BookingWorld.Details(), BookingWorld.PartnerToken(Guid.NewGuid()))).StatusCode.ShouldBe(HttpStatusCode.Forbidden);
        (await world.SendAsync(HttpMethod.Post, $"/trips/{trip}/items/{Guid.NewGuid()}/cancel")).StatusCode.ShouldBe(HttpStatusCode.Unauthorized);

        (await world.SendAsync(HttpMethod.Get, "/health/ready", withVersion: false)).StatusCode.ShouldBe(HttpStatusCode.OK);
        (await world.SendAsync(HttpMethod.Get, "/health/live", withVersion: false)).StatusCode.ShouldBe(HttpStatusCode.OK);
        (await world.SendAsync(HttpMethod.Get, "/openapi/v1.json", withVersion: false)).StatusCode.ShouldBe(HttpStatusCode.OK);
    }

    [Fact]
    public async Task A_trip_has_an_etag_and_names_its_items_with_what_they_cost()
    {
        (Guid trip, Guid stay, _, _) = await world.BasketAsync();

        HttpResponseMessage response = await world.SendAsync(HttpMethod.Get, $"/trips/{trip}", token: world.Traveller);
        JsonElement view = await BookingWorld.JsonAsync(response);

        response.Headers.ETag.ShouldNotBeNull();
        view.GetProperty("status").GetString().ShouldBe("Draft");
        view.GetProperty("total").GetProperty("amount").GetString().ShouldBe("540.00");
        JsonElement item = view.GetProperty("items").EnumerateArray().Single(i => i.GetProperty("id").GetGuid() == stay);
        item.GetProperty("kind").GetString().ShouldBe("Stay");
        item.GetProperty("description").GetString().ShouldStartWith("Stay ");
        item.GetProperty("total").GetProperty("amount").GetString().ShouldBe("300.00");
    }

    [Fact]
    public async Task An_item_is_checked_when_it_is_added_and_adding_the_same_one_again_changes_nothing()
    {
        Guid trip = await world.NewTripAsync();
        Guid item = Guid.CreateVersion7();
        string token = world.StayQuote(300m);
        object body = new { kind = "Stay", quoteToken = token };

        (await world.SendAsync(HttpMethod.Put, $"/trips/{trip}/items/{item}", body, world.Traveller)).StatusCode.ShouldBe(HttpStatusCode.Created);
        HttpResponseMessage again = await world.SendAsync(HttpMethod.Put, $"/trips/{trip}/items/{item}", body, world.Traveller);
        again.StatusCode.ShouldBe(HttpStatusCode.OK);
        (await BookingWorld.JsonAsync(again)).GetProperty("items").GetArrayLength().ShouldBe(1);

        HttpResponseMessage other = await world.SendAsync(HttpMethod.Put, $"/trips/{trip}/items/{item}", new { kind = "Stay", quoteToken = world.StayQuote(999m) }, world.Traveller);
        other.StatusCode.ShouldBe(HttpStatusCode.Conflict);
        (await BookingWorld.ErrorCodeAsync(other)).ShouldBe("ItemConflict");
    }

    [Theory]
    [InlineData("garbage", "Stay", "QuoteInvalid")]
    [InlineData("stay-for-a-car", "Car", "QuoteInvalid")]
    public async Task A_quote_token_that_is_not_one_for_that_kind_of_item_is_refused(string which, string kind, string code)
    {
        Guid trip = await world.NewTripAsync();
        string token = which == "garbage" ? "not-a-token" : world.StayQuote(10m);

        HttpResponseMessage response = await world.SendAsync(HttpMethod.Put, $"/trips/{trip}/items/{Guid.NewGuid()}", new { kind, quoteToken = token }, world.Traveller);

        response.StatusCode.ShouldBe(HttpStatusCode.BadRequest);
        (await BookingWorld.ErrorCodeAsync(response)).ShouldBe(code);
    }

    [Fact]
    public async Task A_quote_that_has_expired_is_a_conflict_and_a_flight_without_an_offer_names_its_missing_fields()
    {
        Guid trip = await world.NewTripAsync();

        HttpResponseMessage expired = await world.SendAsync(
            HttpMethod.Put, $"/trips/{trip}/items/{Guid.NewGuid()}",
            new { kind = "Stay", quoteToken = world.StayQuote(10m, world.Clock.GetUtcNow().AddMinutes(-1)) }, world.Traveller);
        expired.StatusCode.ShouldBe(HttpStatusCode.Conflict);
        (await BookingWorld.ErrorCodeAsync(expired)).ShouldBe("QuoteExpired");

        HttpResponseMessage flight = await world.SendAsync(HttpMethod.Put, $"/trips/{trip}/items/{Guid.NewGuid()}", new { kind = "Flight" }, world.Traveller);
        flight.StatusCode.ShouldBe(HttpStatusCode.BadRequest);
        (await BookingWorld.JsonAsync(flight)).GetProperty("error").GetProperty("details").EnumerateArray()
            .Select(static d => d.GetProperty("code").GetString()).ShouldBe(["OfferIdRequired", "ExpectedTotalRequired"], ignoreOrder: true);
    }

    [Fact]
    public async Task A_draft_can_lose_an_item_and_a_trip_that_is_checked_out_can_no_longer_be_edited()
    {
        (Guid trip, Guid stay, _, _) = await world.BasketAsync();

        (await world.SendAsync(HttpMethod.Delete, $"/trips/{trip}/items/{stay}", token: world.Traveller)).StatusCode.ShouldBe(HttpStatusCode.NoContent);
        (await world.SendAsync(HttpMethod.Delete, $"/trips/{trip}/items/{stay}", token: world.Traveller)).StatusCode.ShouldBe(HttpStatusCode.NoContent);
        (await world.TripAsync(trip)).GetProperty("items").GetArrayLength().ShouldBe(2);
        await world.NextAsync("TripItemStateChanged", trip, m => m.DataString("itemId") == stay.ToString() && m.DataString("state") == "Removed");

        await world.CheckoutAsync(trip);
        HttpResponseMessage late = await world.SendAsync(HttpMethod.Put, $"/trips/{trip}/items/{Guid.NewGuid()}", new { kind = "Stay", quoteToken = world.StayQuote(10m) }, world.Traveller);
        late.StatusCode.ShouldBe(HttpStatusCode.Conflict);
        (await BookingWorld.ErrorCodeAsync(late)).ShouldBe("TripNotEditable");
        await world.WaitForTripAsync(trip, "Confirmed");
    }

    [Fact]
    public async Task Many_creates_of_one_trip_at_once_make_one_trip_and_answer_the_rest_with_it()
    {
        Guid trip = Guid.CreateVersion7();

        HttpResponseMessage[] answers = await Task.WhenAll(
            Enumerable.Range(0, 16).Select(_ => world.SendAsync(HttpMethod.Put, $"/trips/{trip}", token: world.Traveller)));

        answers.Count(static a => a.StatusCode == HttpStatusCode.Created).ShouldBe(1);
        answers.ShouldAllBe(static a => a.StatusCode == HttpStatusCode.Created || a.StatusCode == HttpStatusCode.OK, "the others are repeats of the first, and answer with its trip");
        (await world.SendAsync(HttpMethod.Get, $"/trips/{trip}", token: world.Traveller)).StatusCode.ShouldBe(HttpStatusCode.OK);
        (await world.AllAsync("TripStateChanged", trip)).Count.ShouldBe(1, "one trip was made, so one Draft was announced");
    }

    [Fact]
    public async Task A_skiptoken_from_another_list_is_refused()
    {
        HttpResponseMessage bad = await world.SendAsync(HttpMethod.Get, "/trips?$skiptoken=AQIDBA", token: world.Traveller);

        bad.StatusCode.ShouldBe(HttpStatusCode.BadRequest);
        (await BookingWorld.ErrorCodeAsync(bad)).ShouldBe("InvalidSkipToken");
    }
}
