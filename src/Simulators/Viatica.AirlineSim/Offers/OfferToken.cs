using System.Buffers.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using Viatica.AirlineSim.Network;

namespace Viatica.AirlineSim.Offers;

/// <summary>
/// Everything an offer promised, carried in its id. Search returns hundreds of offers a second under load, and
/// keeping them server side would make the sim's store the bottleneck of the very deadline test it exists to feed.
/// </summary>
/// <remarks>
/// ponytail: unsigned. The sim trusts its callers, which are our own services and tests; a caller that edits the
/// total in an offer id gets the price it wrote. Sign it with an HMAC if the sim ever faces anything else.
/// </remarks>
internal sealed record OfferToken(
    [property: JsonPropertyName("a")] string Airline,
    [property: JsonPropertyName("c")] char FareClass,
    [property: JsonPropertyName("p")] int[] Passengers,
    [property: JsonPropertyName("l")] string[] Legs,
    [property: JsonPropertyName("b")] decimal Base,
    [property: JsonPropertyName("x")] decimal Taxes,
    [property: JsonPropertyName("e")] long ExpiresAt)
{
    public static readonly TimeSpan Lifetime = TimeSpan.FromMinutes(15);

    public PassengerMix Mix => new(Passengers[0], Passengers[1], Passengers[2]);

    public Price Price => new(Base, Taxes);

    public DateTimeOffset Expiry => DateTimeOffset.FromUnixTimeSeconds(ExpiresAt);

    public static OfferToken Create(string airline, FareClass fareClass, PassengerMix mix, IEnumerable<Leg> legs, Price price, DateTimeOffset now) =>
        new(
            airline,
            fareClass.Code,
            [mix.Adults, mix.Children, mix.Infants],
            [.. legs.Select(static leg => leg.Key)],
            price.Base,
            price.Taxes,
            (now + Lifetime).ToUnixTimeSeconds());

    public string Encode() => Base64Url.EncodeToString(JsonSerializer.SerializeToUtf8Bytes(this, OfferTokenJson.Default.OfferToken));

    /// <summary>Reads an offer id, or returns null for anything that is not one of ours.</summary>
    public static OfferToken? Decode(string offerId)
    {
        try
        {
            OfferToken? token = JsonSerializer.Deserialize(Base64Url.DecodeFromChars(offerId), OfferTokenJson.Default.OfferToken);
            return token is { Airline: not null, Passengers.Length: 3, Legs.Length: > 0 } ? token : null;
        }
        catch (Exception exception) when (exception is FormatException or JsonException)
        {
            return null;
        }
    }

    /// <summary>Resolves the legs against the network, or null when a flight in the id does not exist.</summary>
    public Leg[]? ResolveLegs(RouteNetwork network)
    {
        var legs = new Leg[Legs.Length];
        for (int i = 0; i < Legs.Length; i++)
        {
            if (Leg.Parse(Legs[i], Airline, network) is not { } leg)
            {
                return null;
            }

            legs[i] = leg;
        }

        return legs;
    }
}

[JsonSerializable(typeof(OfferToken))]
internal sealed partial class OfferTokenJson : JsonSerializerContext;
