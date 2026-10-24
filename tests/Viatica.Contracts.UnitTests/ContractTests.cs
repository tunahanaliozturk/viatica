using System.Runtime.CompilerServices;
using System.Text.Json;
using System.Text.Json.Serialization.Metadata;

namespace Viatica.Contracts.UnitTests;

/// <summary>
/// The wire format is the contract between services built and deployed separately, so it is pinned: every event
/// is catalogued, serialisable without reflection, and its JSON matches a committed snapshot. Changing a
/// snapshot is a deliberate, reviewed act (set <c>VIATICA_UPDATE_SNAPSHOTS=1</c> and commit the diff).
/// </summary>
public sealed class ContractTests
{
    private static readonly Type[] EventTypes =
    [
        .. typeof(IIntegrationEvent).Assembly.GetTypes()
            .Where(static t => t is { IsClass: true, IsAbstract: false } && typeof(IIntegrationEvent).IsAssignableFrom(t))
            .OrderBy(static t => t.Name, StringComparer.Ordinal),
    ];

    public static TheoryData<string> EventNames => new(EventTypes.Select(static t => t.Name));

    [Fact]
    public void Every_event_is_catalogued_with_a_topic() =>
        EventTypes.ShouldAllBe(type => EventCatalog.TopicByType.ContainsKey(type));

    [Fact]
    public void Every_event_has_generated_serialisation() =>
        EventTypes.ShouldAllBe(type => ContractsJsonContext.Default.GetTypeInfo(type) != null);

    [Fact]
    public void Type_names_are_unique_because_they_are_the_envelope_type() =>
        EventCatalog.TypeByName.Count.ShouldBe(EventCatalog.TopicByType.Count);

    [Theory]
    [MemberData(nameof(EventNames))]
    public void The_json_matches_the_committed_snapshot(string eventName)
    {
        Type type = EventCatalog.TypeByName[eventName];
        string json = Serialize(SampleFactory.Create(type), type);
        string path = Path.Combine(SnapshotDirectory(), eventName + ".json");

        if (Environment.GetEnvironmentVariable("VIATICA_UPDATE_SNAPSHOTS") == "1")
        {
            File.WriteAllText(path, json + "\n");
            return;
        }

        File.Exists(path).ShouldBeTrue($"No snapshot for {eventName}. Run with VIATICA_UPDATE_SNAPSHOTS=1 and review it.");
        File.ReadAllText(path).ReplaceLineEndings("\n").TrimEnd().ShouldBe(json);
    }

    [Theory]
    [MemberData(nameof(EventNames))]
    public void An_event_survives_a_round_trip(string eventName)
    {
        Type type = EventCatalog.TypeByName[eventName];
        string json = Serialize(SampleFactory.Create(type), type);

        object back = JsonSerializer.Deserialize(json, ContractsJsonContext.Default.GetTypeInfo(type)!)!;

        Serialize(back, type).ShouldBe(json);
    }

    [Fact]
    public void Money_travels_as_a_string_so_no_consumer_reads_it_as_a_float()
    {
        string json = JsonSerializer.Serialize(new Price(312.50m, "EUR"), PriceTypeInfo());

        json.ShouldBe("""{"amount":"312.50","currency":"EUR"}""");
    }

    private static JsonTypeInfo PriceTypeInfo() =>
        ContractsJsonContext.Default.GetTypeInfo(typeof(Price))!;

    private static string Serialize(object value, Type type)
    {
        JsonSerializerOptions indented = new(ContractsJsonContext.Default.Options) { WriteIndented = true, NewLine = "\n" };
        return JsonSerializer.Serialize(value, indented.GetTypeInfo(type));
    }

    private static string SnapshotDirectory([CallerFilePath] string source = "") =>
        Path.Combine(Path.GetDirectoryName(source)!, "Snapshots");
}
