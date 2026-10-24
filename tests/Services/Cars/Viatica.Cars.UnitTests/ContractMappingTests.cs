using Viatica.Cars.Domain;
using Contracts = Viatica.Contracts;

namespace Viatica.Cars.UnitTests;

/// <summary>
/// The service keeps its own enums so the Domain knows nothing of the wire. Where the integration events take them
/// by numeric cast, these tests are what keeps the two lists in step.
/// </summary>
public sealed class ContractMappingTests
{
    [Fact]
    public void Transmission_matches_the_contract_member_for_member() =>
        Pairs<Transmission, Contracts.Transmission>().ShouldAllBe(static pair => pair.Domain == pair.Wire);

    [Fact]
    public void Fuel_matches_the_contract_member_for_member() =>
        Pairs<FuelType, Contracts.FuelType>().ShouldAllBe(static pair => pair.Domain == pair.Wire);

    private static (string Domain, string Wire)[] Pairs<TDomain, TWire>()
        where TDomain : struct, Enum
        where TWire : struct, Enum
    {
        string[] domain = [.. Enum.GetValues<TDomain>().OrderBy(static v => Convert.ToInt32(v, System.Globalization.CultureInfo.InvariantCulture)).Select(static v => v.ToString())];
        string[] wire = [.. Enum.GetValues<TWire>().OrderBy(static v => Convert.ToInt32(v, System.Globalization.CultureInfo.InvariantCulture)).Select(static v => v.ToString())];

        domain.Length.ShouldBe(wire.Length);
        return [.. domain.Zip(wire)];
    }
}
