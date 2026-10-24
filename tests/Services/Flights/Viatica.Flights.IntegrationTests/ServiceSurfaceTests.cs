using System.Net;
using System.Net.Http.Headers;
using System.Text.Json;
using Viatica.Testing;

namespace Viatica.Flights.IntegrationTests;

/// <summary>Health, authorization and the one read endpoint a person can use on orders.</summary>
[Collection(DefaultFlightsGroup.Name)]
public sealed class ServiceSurfaceTests(DefaultFlights flights) : IAsyncLifetime
{
    private readonly Scenario scenario = new(flights);

    public async ValueTask InitializeAsync() => await flights.Sim.QuietAsync();

    public ValueTask DisposeAsync() => ValueTask.CompletedTask;

    private async Task<HttpResponseMessage> GetAsync(string url, string? token = null)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, url);
        if (token is not null)
        {
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        }

        return await flights.Client.SendAsync(request);
    }

    [Theory]
    [InlineData("/health/live")]
    [InlineData("/health/ready")]
    public async Task The_health_endpoints_answer_without_an_api_version(string path)
    {
        using HttpResponseMessage response = await GetAsync(path);

        response.StatusCode.ShouldBe(HttpStatusCode.OK);
    }

    [Fact]
    public async Task An_admin_reads_an_order_with_its_etag_and_no_passenger_data()
    {
        PlacedOrder order = await scenario.TicketedOrderAsync("ZC");

        using HttpResponseMessage response = await GetAsync(Travel.V($"/flights/orders/{order.OrderId}"), TestTokens.Admin("flights-api"));
        string text = await response.Content.ReadAsStringAsync();
        JsonElement body = JsonElement.Parse(text);

        response.StatusCode.ShouldBe(HttpStatusCode.OK);
        response.Headers.ETag.ShouldNotBeNull();
        body.GetProperty("status").GetString().ShouldBe("Ticketed");
        body.GetProperty("tripId").GetGuid().ShouldBe(order.TripId);
        body.GetProperty("itemId").GetGuid().ShouldBe(order.ItemId);
        body.GetProperty("airline").GetString().ShouldBe("ZC");
        body.GetProperty("bookingReference").GetString().ShouldBe(order.BookingReference);
        body.GetProperty("ticketNumbers").GetArrayLength().ShouldBe(1);
        body.GetProperty("total").GetProperty("currency").GetString().ShouldBe("EUR");
        text.ShouldNotContain("Zeynebnur");
        text.ShouldNotContain("sealed", Case.Insensitive);
        text.ShouldNotContain("passenger", Case.Insensitive);
    }

    [Fact]
    public async Task Reading_an_order_needs_an_admin()
    {
        PlacedOrder order = await scenario.OrderAsync("ZC");
        string url = Travel.V($"/flights/orders/{order.OrderId}");

        (await GetAsync(url)).StatusCode.ShouldBe(HttpStatusCode.Unauthorized);
        (await GetAsync(url, TestTokens.Traveller("flights-api"))).StatusCode.ShouldBe(HttpStatusCode.Forbidden);
        (await GetAsync(url, TestTokens.Partner("flights-api", Guid.NewGuid()))).StatusCode.ShouldBe(HttpStatusCode.Forbidden);
        (await GetAsync(url, TestTokens.Admin("stays-api"))).StatusCode.ShouldBe(HttpStatusCode.Unauthorized);
    }

    [Fact]
    public async Task An_order_that_does_not_exist_is_a_404_in_the_error_envelope()
    {
        using HttpResponseMessage response = await GetAsync(Travel.V($"/flights/orders/{Guid.NewGuid()}"), TestTokens.Admin("flights-api"));
        JsonElement body = JsonElement.Parse(await response.Content.ReadAsStringAsync());

        response.StatusCode.ShouldBe(HttpStatusCode.NotFound);
        body.GetProperty("error").GetProperty("code").GetString().ShouldBe("NotFound");
    }

    [Fact]
    public async Task Search_is_open_to_anyone_without_a_token()
    {
        using HttpResponseMessage response = await GetAsync(Travel.V($"/flights/searches/{Guid.NewGuid()}"));

        response.StatusCode.ShouldBe(HttpStatusCode.NotFound, "a 401 here would mean anonymous visitors cannot search");
    }
}
