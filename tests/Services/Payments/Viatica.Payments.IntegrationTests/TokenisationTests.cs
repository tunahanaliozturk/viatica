using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Viatica.Payments.Domain;
using Viatica.Testing;

namespace Viatica.Payments.IntegrationTests;

/// <summary>PAY-1: a test card becomes a token over HTTP, and everything that can go wrong with a card is an answer, not a leak.</summary>
public sealed class TokenisationTests(DefaultPayments payments) : IClassFixture<DefaultPayments>, IAsyncLifetime
{
    private readonly Scenario scenario = new(payments);

    public async ValueTask InitializeAsync() => await payments.Sim.QuietAsync();

    public ValueTask DisposeAsync() => ValueTask.CompletedTask;

    private static async Task<JsonElement> JsonOf(HttpResponseMessage response)
    {
        string text = await response.Content.ReadAsStringAsync();
        return text.Length == 0 ? default : JsonElement.Parse(text);
    }

    [Theory]
    [InlineData(Cards.Visa, "visa", "4242")]
    [InlineData(Cards.Mastercard, "mastercard", "4444")]
    [InlineData("4242 4242 4242 4242", "visa", "4242")]
    public async Task A_test_card_becomes_a_token_with_its_brand_and_last_four_and_nothing_else(string number, string brand, string last4)
    {
        using HttpResponseMessage response = await scenario.TokeniseAsync(new { number, expMonth = 12, expYear = 2030, cvc = "123" });
        JsonElement body = await JsonOf(response);

        response.StatusCode.ShouldBe(HttpStatusCode.Created);
        body.GetProperty("token").GetString()!.ShouldStartWith("pm_");
        body.GetProperty("brand").GetString().ShouldBe(brand);
        body.GetProperty("last4").GetString().ShouldBe(last4);
        body.GetProperty("expMonth").GetInt32().ShouldBe(12);
        body.GetProperty("expYear").GetInt32().ShouldBe(2030);
        body.EnumerateObject().Select(static p => p.Name).Order().ShouldBe(["brand", "expMonth", "expYear", "last4", "token"]);
        body.ToString().ShouldNotContain("4242424242424242");
    }

    [Fact]
    public async Task The_token_is_stored_with_the_provider_token_and_what_a_receipt_shows()
    {
        string token = await scenario.TokenAsync(Cards.Mastercard);

        PaymentMethod stored = await payments.Host.InDbAsync(db => db.Methods.AsNoTracking().SingleAsync(m => m.Token == token));

        stored.Brand.ShouldBe("mastercard");
        stored.Last4.ShouldBe("4444");
        stored.ProviderToken.ShouldStartWith("tok_");
        stored.ProviderToken.ShouldNotBe(token, "the provider token never leaves the service");
    }

    [Fact]
    public async Task Two_tokens_for_one_card_are_different_and_unguessable()
    {
        string first = await scenario.TokenAsync();
        string second = await scenario.TokenAsync();

        first.ShouldNotBe(second);
        first.Length.ShouldBeGreaterThan(30);
    }

    [Theory]
    [InlineData(Cards.Declined)]
    [InlineData(Cards.InsufficientFunds)]
    [InlineData(Cards.TimesOutButHappens)]
    [InlineData(Cards.TimesOutAndDoesNotHappen)]
    public async Task Cards_that_fail_later_still_tokenise_because_the_test_card_decides_the_outcome_when_it_is_charged(string number)
    {
        using HttpResponseMessage response = await scenario.TokeniseAsync(new { number, expMonth = 12, expYear = 2030, cvc = "123" });

        response.StatusCode.ShouldBe(HttpStatusCode.Created);
    }

    [Fact]
    public async Task A_bad_card_is_a_400_with_every_broken_field_and_without_the_values_in_the_answer()
    {
        using HttpResponseMessage response = await scenario.TokeniseAsync(new { number = "4242424242424241", expMonth = 13, expYear = 2030, cvc = "12" });
        JsonElement error = (await JsonOf(response)).GetProperty("error");

        response.StatusCode.ShouldBe(HttpStatusCode.BadRequest);
        error.GetProperty("code").GetString().ShouldBe("InvalidRequest");
        error.GetProperty("details").EnumerateArray().Select(static d => (d.GetProperty("code").GetString(), d.GetProperty("target").GetString()))
            .ShouldBe([("InvalidCardNumber", "number"), ("InvalidExpiry", "expMonth"), ("InvalidCvc", "cvc")]);
        error.ToString().ShouldNotContain("4242424242424241");
    }

    [Fact]
    public async Task An_expired_card_is_refused_with_its_own_code()
    {
        using HttpResponseMessage response = await scenario.TokeniseAsync(new { number = Cards.Visa, expMonth = 1, expYear = 2020 });
        JsonElement error = (await JsonOf(response)).GetProperty("error");

        response.StatusCode.ShouldBe(HttpStatusCode.BadRequest);
        error.GetProperty("details")[0].GetProperty("code").GetString().ShouldBe("CardExpired");
    }

    [Fact]
    public async Task A_card_that_passes_our_checks_but_is_not_a_test_card_is_refused_by_the_provider_as_PaymentMethodInvalid()
    {
        // 4111 1111 1111 1111 is a valid number; the simulator only accepts its own test cards, as Stripe test mode does.
        using HttpResponseMessage response = await scenario.TokeniseAsync(new { number = "4111111111111111", expMonth = 12, expYear = 2030, cvc = "123" });
        JsonElement error = (await JsonOf(response)).GetProperty("error");

        response.StatusCode.ShouldBe(HttpStatusCode.BadRequest);
        error.GetProperty("code").GetString().ShouldBe("PaymentMethodInvalid");
        error.GetProperty("innererror").GetProperty("code").GetString().ShouldBe("incorrect_number");
        error.ToString().ShouldNotContain("4111");
        (await payments.Host.InDbAsync(db => db.Methods.CountAsync(m => m.Last4 == "1111"))).ShouldBe(0, "a refused card leaves no token behind");
    }

    [Fact]
    public async Task A_body_that_is_not_a_card_is_a_400_in_the_envelope()
    {
        using var request = new HttpRequestMessage(HttpMethod.Post, Scenario.Version("/payments/methods"))
        {
            Content = new StringContent("""{ "number": 4242424242424242 }""", System.Text.Encoding.UTF8, "application/json"),
        };
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", TestTokens.Traveller(PaymentsHost.Audience));

        using HttpResponseMessage response = await payments.Client.SendAsync(request);
        string body = await response.Content.ReadAsStringAsync();

        response.StatusCode.ShouldBe(HttpStatusCode.BadRequest);
        body.ShouldNotContain("4242424242424242");
    }

    [Fact]
    public async Task Tokenising_needs_a_traveller_token()
    {
        object card = new { number = Cards.Visa, expMonth = 12, expYear = 2030 };

        using HttpResponseMessage anonymous = await payments.Client.PostAsJsonAsyncVersioned("/payments/methods", card);
        using HttpResponseMessage partner = await scenario.TokeniseAsync(card, TestTokens.Partner(PaymentsHost.Audience, Guid.CreateVersion7()));
        using HttpResponseMessage wrongAudience = await scenario.TokeniseAsync(card, TestTokens.Traveller("stays-api"));

        anonymous.StatusCode.ShouldBe(HttpStatusCode.Unauthorized);
        partner.StatusCode.ShouldBe(HttpStatusCode.Forbidden);
        wrongAudience.StatusCode.ShouldBe(HttpStatusCode.Unauthorized);
    }

    [Fact]
    public async Task A_missing_api_version_is_refused_before_anything_happens()
    {
        using var request = new HttpRequestMessage(HttpMethod.Post, "/payments/methods")
        {
            Content = JsonContent.Create(new { number = Cards.Visa, expMonth = 12, expYear = 2030 }),
        };
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", TestTokens.Traveller(PaymentsHost.Audience));

        using HttpResponseMessage response = await payments.Client.SendAsync(request);

        response.StatusCode.ShouldBe(HttpStatusCode.BadRequest);
        (await JsonOf(response)).GetProperty("error").GetProperty("code").GetString().ShouldBe("MissingApiVersionParameter");
    }

    [Fact]
    public async Task When_the_provider_is_down_the_answer_is_a_503_that_says_to_try_again_and_no_token_is_made()
    {
        int before = await payments.Host.InDbAsync(db => db.Methods.CountAsync());
        await payments.Sim.SetKnobsAsync(new { Outage = true });

        using HttpResponseMessage response = await scenario.TokeniseAsync(new { number = Cards.Visa, expMonth = 12, expYear = 2030, cvc = "123" });
        JsonElement error = (await JsonOf(response)).GetProperty("error");

        response.StatusCode.ShouldBe(HttpStatusCode.ServiceUnavailable);
        error.GetProperty("code").GetString().ShouldBe("ProviderUnavailable");
        response.Headers.RetryAfter.ShouldNotBeNull();
        (await payments.Host.InDbAsync(db => db.Methods.CountAsync())).ShouldBe(before);
    }

    [Fact]
    public async Task The_card_number_goes_to_the_provider_in_one_request_and_to_nobody_else()
    {
        int before = payments.Host.Wire.Entries.Count(static e => e.Body.Contains(Cards.Mastercard, StringComparison.Ordinal));

        await scenario.TokenAsync(Cards.Mastercard);

        WireEntry[] withNumber = [.. payments.Host.Wire.Entries.Where(static e => e.Body.Contains(Cards.Mastercard, StringComparison.Ordinal))];
        withNumber.Length.ShouldBe(before + 1);
        withNumber.ShouldAllBe(static e => e.Method == HttpMethod.Post && e.Path == "/v1/tokens");
    }
}

internal static class HttpExtensions
{
    public static Task<HttpResponseMessage> PostAsJsonAsyncVersioned(this HttpClient client, string path, object body) =>
        client.PostAsJsonAsync(Scenario.Version(path), body);
}
