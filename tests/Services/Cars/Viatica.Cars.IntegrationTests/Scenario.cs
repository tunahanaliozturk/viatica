using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace Viatica.Cars.IntegrationTests;

/// <summary>Calls the API the way a client does: the version on every request, JSON both ways.</summary>
internal static class Api
{
    public static string Url(string path) =>
        $"{path}{(path.Contains('?', StringComparison.Ordinal) ? '&' : '?')}api-version={CarsHost.ApiVersion}";

    public static Task<HttpResponseMessage> FetchAsync(this HttpClient client, string path, string? query = null) =>
        client.GetAsync(new Uri(Url(query is null ? path : path + "?" + query), UriKind.Relative));

    /// <summary><c>If-Match: *</c> creates or replaces; pass the ETag to replace that version only.</summary>
    public static Task<HttpResponseMessage> PutAsync(this HttpClient client, string path, object body, string? ifMatch = "*")
    {
        var request = new HttpRequestMessage(HttpMethod.Put, Url(path)) { Content = JsonContent.Create(body, options: JsonSerializerOptions.Web) };
        if (ifMatch is not null)
        {
            request.Headers.TryAddWithoutValidation("If-Match", ifMatch);
        }

        return client.SendAsync(request);
    }

    /// <param name="client">The caller.</param>
    /// <param name="path">The path, without the api-version.</param>
    /// <param name="body">The JSON body, if any.</param>
    /// <param name="repeatabilityId">A <c>Repeatability-Request-ID</c> to send.</param>
    /// <param name="firstSent">When the request was first sent. The service judges it against its own clock, which the tests move.</param>
    public static Task<HttpResponseMessage> PostAsync(
        this HttpClient client, string path, object? body = null, string? repeatabilityId = null, DateTimeOffset? firstSent = null)
    {
        var request = new HttpRequestMessage(HttpMethod.Post, Url(path));
        if (body is not null)
        {
            request.Content = JsonContent.Create(body, options: JsonSerializerOptions.Web);
        }

        if (repeatabilityId is not null)
        {
            request.Headers.Add("Repeatability-Request-ID", repeatabilityId);
            request.Headers.Add("Repeatability-First-Sent", (firstSent ?? DateTimeOffset.UtcNow).ToString("R", System.Globalization.CultureInfo.InvariantCulture));
        }

        return client.SendAsync(request);
    }

    public static async Task<JsonNode> JsonAsync(this HttpResponseMessage response) =>
        JsonNode.Parse(await response.Content.ReadAsStringAsync()) ?? throw new InvalidOperationException("The response had no JSON body.");

    public static async Task<string> ErrorCodeAsync(this HttpResponseMessage response) =>
        (string)(await response.JsonAsync())["error"]!["code"]!;

    public static string ETag(this HttpResponseMessage response) =>
        response.Headers.ETag?.ToString() ?? throw new InvalidOperationException("The response had no ETag.");
}

/// <summary>Request bodies and the standard set-up most tests start from.</summary>
internal static class Scenario
{
    // Mondays to Fridays 08:00 to 18:00, Saturdays 09:00 to 13:00, closed on Sundays. 2026-12-07 is a Monday.
    public static readonly DateTimeOffset Monday = new(2026, 12, 7, 10, 0, 0, TimeSpan.Zero);

    public static object[] BusinessHours { get; } =
    [
        .. new[] { "Monday", "Tuesday", "Wednesday", "Thursday", "Friday" }
            .Select(static day => new { day, opens = "08:00:00", closes = "18:00:00" }),
        new { day = "Saturday", opens = "09:00:00", closes = "13:00:00" },
    ];

    public static object Location(string name = "Lisbon Airport", object[]? hours = null, string? airport = "LIS") => new
    {
        name,
        address = new { line1 = "Rua A 1", city = "Lisbon", postalCode = "1700-008", countryCode = "PT" },
        position = new { latitude = 38.7742, longitude = -9.1342 },
        airportCode = airport,
        openingHours = hours ?? BusinessHours,
        cancellationPolicy = new { freeUntilHoursBeforeStart = 48, penaltyAfter = "FirstDay" },
    };

    public static object Category(string acriss = "ECMN", int minimumDriverAge = 21, string name = "Economy") => new
    {
        acriss,
        name,
        seats = 5,
        transmission = "Manual",
        fuel = "Petrol",
        bags = 2,
        minimumDriverAge,
    };

    public static object Money(decimal amount) => new { amount = amount.ToString("F2", System.Globalization.CultureInfo.InvariantCulture), currency = "EUR" };

    public static object Inventory(string from, string to, int allotment, decimal rate) =>
        new { from, to, allotment, rate = Money(rate) };

    public static object Quote(Guid location, Guid category, DateTimeOffset pickUp, DateTimeOffset dropOff, int driverAge = 35, Guid? dropOffLocation = null) =>
        new { locationId = location, categoryId = category, dropOffLocationId = dropOffLocation, pickUpAt = pickUp, dropOffAt = dropOff, driverAge };

    /// <summary>A partner with two published locations (Lisbon and Porto) and a published category.</summary>
    public static async Task<Marketplace> ArrangeAsync(
        CarsHost host, Guid? partnerId = null, int allotment = 5, decimal rate = 40m, int minimumDriverAge = 21)
    {
        Guid partner = partnerId ?? Guid.NewGuid();
        HttpClient client = host.Partner(partner);
        var marketplace = new Marketplace(partner, Guid.CreateVersion7(), Guid.CreateVersion7(), Guid.CreateVersion7(), client);

        await Ok(client.PutAsync($"/cars/categories/{marketplace.CategoryId}", Category(minimumDriverAge: minimumDriverAge)));
        await Ok(client.PutAsync($"/cars/locations/{marketplace.LocationId}", Location()));
        await Ok(client.PutAsync($"/cars/locations/{marketplace.OtherLocationId}", Location("Porto Airport", airport: "OPO")));
        await Ok(client.PostAsync($"/cars/categories/{marketplace.CategoryId}/publish"));
        await Ok(client.PostAsync($"/cars/locations/{marketplace.LocationId}/publish"));
        await Ok(client.PostAsync($"/cars/locations/{marketplace.OtherLocationId}/publish"));
        await Ok(client.PostAsync(
            $"/cars/locations/{marketplace.LocationId}/categories/{marketplace.CategoryId}/inventory/bulk",
            Inventory("2026-12-01", "2026-12-31", allotment, rate)));
        return marketplace;
    }

    public static async Task<HttpResponseMessage> Ok(Task<HttpResponseMessage> call)
    {
        HttpResponseMessage response = await call;
        if (!response.IsSuccessStatusCode)
        {
            throw new InvalidOperationException($"Arranging failed: {(int)response.StatusCode} {await response.Content.ReadAsStringAsync()}");
        }

        return response;
    }

    public static async Task<string> TokenAsync(HttpClient anonymous, Marketplace m, DateTimeOffset pickUp, DateTimeOffset dropOff, int age = 35)
    {
        HttpResponseMessage response = await Ok(anonymous.PostAsync("/cars/quotes", Quote(m.LocationId, m.CategoryId, pickUp, dropOff, age)));
        return (string)(await response.JsonAsync())["quoteToken"]!;
    }

    public static async Task AssertStatusAsync(this HttpResponseMessage response, HttpStatusCode status, string? code = null)
    {
        string body = await response.Content.ReadAsStringAsync();
        response.StatusCode.ShouldBe(status, body);
        if (code is not null)
        {
            ((string)JsonNode.Parse(body)!["error"]!["code"]!).ShouldBe(code);
        }
    }
}

internal sealed record Marketplace(Guid PartnerId, Guid LocationId, Guid OtherLocationId, Guid CategoryId, HttpClient Client);
