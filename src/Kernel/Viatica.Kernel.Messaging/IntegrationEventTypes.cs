using System.Collections.Frozen;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.Json.Serialization.Metadata;
using Viatica.Contracts;

namespace Viatica.Kernel.Messaging;

/// <summary>
/// The integration events a service knows, and how each one is written and read. The name on the wire is the CLR
/// type name, so renaming a record is a breaking change to the contract, which is what it is.
/// </summary>
/// <remarks>
/// Metadata comes from a source-generated <see cref="JsonSerializerContext"/>, not from reflection: an event type
/// the context does not cover fails here, at startup, instead of on the first message that carries it.
/// </remarks>
public sealed class IntegrationEventTypes
{
    private readonly FrozenDictionary<string, JsonTypeInfo> _byName;
    private readonly FrozenDictionary<Type, (string Name, JsonTypeInfo Info)> _byType;

    public IntegrationEventTypes(IJsonTypeInfoResolver resolver, IEnumerable<Type> eventTypes)
    {
        ArgumentNullException.ThrowIfNull(resolver);
        ArgumentNullException.ThrowIfNull(eventTypes);

        // A context brings its own options (naming policy included); anything else gets the web defaults, which
        // are camelCase like the envelope around the data.
        JsonSerializerOptions options = resolver is JsonSerializerContext context
            ? context.Options
            : new JsonSerializerOptions(JsonSerializerDefaults.Web) { TypeInfoResolver = resolver };

        var byType = new Dictionary<Type, (string, JsonTypeInfo)>();
        var byName = new Dictionary<string, JsonTypeInfo>(StringComparer.Ordinal);

        foreach (Type type in eventTypes)
        {
            if (!typeof(IIntegrationEvent).IsAssignableFrom(type))
            {
                throw new ArgumentException($"{type.FullName} does not implement {nameof(IIntegrationEvent)}.", nameof(eventTypes));
            }

            if (!options.TryGetTypeInfo(type, out JsonTypeInfo? info))
            {
                throw new ArgumentException($"The JSON resolver has no metadata for {type.FullName}.", nameof(resolver));
            }

            if (!byName.TryAdd(type.Name, info))
            {
                throw new ArgumentException($"Two event types are named {type.Name}; the wire name must be unique.", nameof(eventTypes));
            }

            byType[type] = (type.Name, info);
        }

        _byName = byName.ToFrozenDictionary(StringComparer.Ordinal);
        _byType = byType.ToFrozenDictionary();
    }

    /// <summary>
    /// Reads one Kafka message value. For consumers that keep no relational store, and so cannot use
    /// <c>AddSubscription</c> with its inbox: the search projection reads batches itself and gets idempotency from
    /// event versions. Throws <see cref="JsonException"/> for anything malformed, including an envelope version this
    /// code does not know.
    /// </summary>
    public Incoming Read(byte[] value) => Envelope.Read(value, this);

    internal (string Type, string Data) Serialize(IIntegrationEvent @event)
    {
        if (!_byType.TryGetValue(@event.GetType(), out var entry))
        {
            throw new InvalidOperationException(
                $"{@event.GetType().FullName} is not a registered integration event. Add it to {nameof(IntegrationEventTypes)}.");
        }

        return (entry.Name, JsonSerializer.Serialize(@event, entry.Info));
    }

    /// <summary>Reads <paramref name="data"/> as the event named <paramref name="type"/>; null when the name is unknown.</summary>
    internal IIntegrationEvent? Deserialize(string type, JsonElement data) =>
        _byName.TryGetValue(type, out JsonTypeInfo? info)
            ? (IIntegrationEvent?)data.Deserialize(info) ?? throw new JsonException($"The data of {type} is null.")
            : null;
}
