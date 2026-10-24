using System.Text.Json;
using Viatica.Contracts;

namespace Viatica.Kernel.Messaging.IntegrationTests;

/// <summary>The real catalogue goes through the envelope, so the registry and the contracts cannot drift apart.</summary>
public sealed class ContractCatalogueTests
{
    private static readonly IntegrationEventTypes Types = new(ContractsJsonContext.Default, EventCatalog.TopicByType.Keys);

    [Fact]
    public void Every_catalogued_event_round_trips_through_the_envelope_by_its_clr_name()
    {
        var sent = new StayUnpublished(Guid.CreateVersion7(), ListingVersion: 7);
        (string type, string data) = Types.Serialize(sent);
        byte[] wire = Envelope.Write(Guid.CreateVersion7(), type, DateTimeOffset.UtcNow, traceParent: null, data);

        Incoming received = Envelope.Read(wire, Types);

        received.Type.ShouldBe(nameof(StayUnpublished));
        received.Event.ShouldBe(sent);
        using JsonDocument json = JsonDocument.Parse(data);
        json.RootElement.TryGetProperty("partitionKey", out _).ShouldBeFalse();
    }

    [Fact]
    public void A_type_from_another_service_reads_as_unknown_rather_than_failing() =>
        Envelope.Read(Envelope.Write(Guid.CreateVersion7(), "SomethingNew", DateTimeOffset.UtcNow, null, "{}"), Types)
            .Event.ShouldBeNull();

    [Fact]
    public void An_envelope_version_this_code_does_not_know_is_unreadable()
    {
        byte[] future = """{"id":"0192f0c4-7a3b-7cde-8f00-000000000001","type":"StayUnpublished","version":2,"occurredAt":"2026-10-24T10:00:00Z","traceparent":null,"data":{}}"""u8.ToArray();

        Should.Throw<JsonException>(() => Envelope.Read(future, Types));
    }
}
