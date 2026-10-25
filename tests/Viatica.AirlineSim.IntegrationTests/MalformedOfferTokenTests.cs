using System.Buffers.Text;
using System.Net;
using System.Text;
using System.Text.Json.Nodes;
using Shouldly;
using Viatica.AirlineSim.Api;

namespace Viatica.AirlineSim.IntegrationTests;

/// <summary>
/// Offer ids are unsigned on purpose (see <c>OfferToken</c>), so a caller can send any JSON it likes inside one. A
/// structurally valid token with nonsense in it must be answered as an offer the airline did not issue, never as a
/// 500 from a null leg, a timestamp outside <see cref="DateTimeOffset"/> or a sum that overflows.
/// </summary>
public sealed class MalformedOfferTokenTests(Postgres postgres)
{
    private static readonly (string Description, string Patch)[] Forgeries =
    [
        ("a null leg", """{"l":[null]}"""),
        ("an empty leg", """{"l":[""]}"""),
        ("a leg that is not a flight and date", """{"l":["nonsense"]}"""),
        ("an expiry beyond DateTimeOffset", """{"e":9223372036854775807}"""),
        ("an expiry before DateTimeOffset", """{"e":-9223372036854775808}"""),
        ("a negative adult count", """{"p":[-1,0,0]}"""),
        ("no adult", """{"p":[0,0,0]}"""),
        ("more seats than a booking can hold", """{"p":[10,0,0]}"""),
        ("more infants than adults", """{"p":[1,0,2]}"""),
        ("a negative child count", """{"p":[2,-1,0]}"""),
        ("a negative base price", """{"b":-1}"""),
        ("a negative tax", """{"x":-0.01}"""),
        ("a price that overflows when added to the tax", """{"b":79228162514264337593543950335,"x":1}"""),
        ("a price with more than two decimals", """{"b":10.001}"""),
        ("an unknown fare class", """{"c":"Z"}"""),
    ];

    // One host for all of them: a host brings a database and a connection pool, and fifteen would crowd the server.
    [Fact]
    public async Task A_structurally_valid_token_with_nonsense_in_it_is_an_unknown_offer()
    {
        await using SimHost sim = await SimHost.StartAsync(postgres);
        OfferView real = await sim.Client.OfferAsync("ZC", "IST", "LHR", 'K');

        foreach ((string description, string patch) in Forgeries)
        {
            string forged = Forge(real.OfferId, patch);

            // An unhandled exception reaches the caller through the test server; in production it is a 500.
            using HttpResponseMessage price = await PostAsync(sim, $"/ndc/ZC/offers/{forged}/price", description);
            using HttpResponseMessage order = await sim.Client.CreateOrderAsync("ZC", forged, "forged-" + Guid.NewGuid());

            price.StatusCode.ShouldBe(HttpStatusCode.NotFound, description);
            (await price.ErrorCodeAsync()).ShouldBe("OFFER_NOT_FOUND", description);
            order.StatusCode.ShouldBe(HttpStatusCode.NotFound, description);
            (await order.ErrorCodeAsync()).ShouldBe("OFFER_NOT_FOUND", description);
        }
    }

    private static async Task<HttpResponseMessage> PostAsync(SimHost sim, string url, string description)
    {
        try
        {
            return await sim.Client.PostAsync(url, content: null);
        }
        catch (Exception exception)
        {
            throw new ShouldAssertException($"{description}: the sim threw {exception.GetType().Name} instead of answering OFFER_NOT_FOUND.", exception);
        }
    }

    [Fact]
    public async Task The_untouched_token_still_prices_and_books()
    {
        await using SimHost sim = await SimHost.StartAsync(postgres);
        OfferView real = await sim.Client.OfferAsync("ZC", "IST", "LHR", 'K');
        string same = Forge(real.OfferId, "{}");

        using HttpResponseMessage price = await sim.Client.PostAsync($"/ndc/ZC/offers/{same}/price", content: null);
        using HttpResponseMessage order = await sim.Client.CreateOrderAsync("ZC", same, "same-" + Guid.NewGuid());

        price.StatusCode.ShouldBe(HttpStatusCode.OK);
        order.StatusCode.ShouldBe(HttpStatusCode.Created);
    }

    /// <summary>Decodes a real offer id, overwrites the named properties, and encodes it again.</summary>
    private static string Forge(string offerId, string patch)
    {
        var token = JsonNode.Parse(Base64Url.DecodeFromChars(offerId))!.AsObject();
        foreach ((string name, JsonNode? value) in JsonNode.Parse(patch)!.AsObject().ToArray())
        {
            token.Remove(name);
            token.Add(name, value?.DeepClone());
        }

        return Base64Url.EncodeToString(Encoding.UTF8.GetBytes(token.ToJsonString()));
    }
}
