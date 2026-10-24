using Viatica.Search.Application;
using Viatica.Search.Application.Places;
using Viatica.Search.Infrastructure.Places;

namespace Viatica.Search.UnitTests;

public sealed class PlacesTests
{
    // The airports the airline simulator flies between (src/Simulators/Viatica.AirlineSim/Network/Airports.cs).
    // Search must not reference the simulator, so the list is repeated here and the test fails if the JSON drifts.
    private static readonly string[] SimulatorAirports =
    [
        "IST", "ESB", "ADB", "AYT", "LHR", "MAN", "EDI", "DUB", "LIS", "MAD", "BCN", "CDG", "NCE", "BRU", "AMS", "FRA",
        "MUC", "BER", "HAM", "ZRH", "GVA", "VIE", "FCO", "MXP", "CPH", "OSL", "ARN", "HEL", "WAW", "PRG", "BUD", "OTP",
        "SOF", "ATH", "JFK", "YYZ", "GRU", "DXB", "SIN", "NRT",
    ];

    private readonly AirportDirectory _airports = new();

    [Fact]
    public void Every_airport_the_simulator_uses_is_in_the_list()
    {
        SimulatorAirports.Length.ShouldBe(40);

        foreach (string code in SimulatorAirports)
        {
            _airports.Find(code, 5).ShouldContain(place => place.Code == code, code);
        }
    }

    [Fact]
    public void A_code_typed_in_full_comes_first()
    {
        IReadOnlyList<Place> found = _airports.Find("lis", 5);

        found[0].Code.ShouldBe("LIS");
        found[0].Kind.ShouldBe(PlaceKind.Airport);
        found[0].Detail.ShouldBe("Lisbon, PT");
    }

    [Theory]
    [InlineData("zurich", "ZRH")]
    [InlineData("ZÜRICH", "ZRH")]
    [InlineData("Istanbul", "IST")]
    [InlineData("heathrow", "LHR")]
    [InlineData("new york", "JFK")]
    [InlineData("sao pa", "GRU")]
    public void Cities_and_airport_names_match_in_any_case_with_or_without_accents(string typed, string expected)
    {
        _airports.Find(typed, 5).Select(static p => p.Code).ShouldContain(expected);
    }

    [Fact]
    public void Nothing_matches_gibberish_and_the_limit_is_respected()
    {
        _airports.Find("qqqq", 5).ShouldBeEmpty();
        _airports.Find("a", 3).Count.ShouldBe(3);
    }

    [Theory]
    [InlineData("Zürich", "zurich")]
    [InlineData("İstanbul", "istanbul")]
    [InlineData("Łódź", "lodz")]
    [InlineData("Straße", "strasse")]
    [InlineData("Ålesund", "alesund")]
    public void Folding_lower_cases_and_strips_accents(string text, string expected)
    {
        TextFolding.Fold(text).ShouldBe(expected);
    }
}
