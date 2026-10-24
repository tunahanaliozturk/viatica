using System.Buffers;
using System.Text.Json;
using Viatica.Contracts;

namespace Viatica.Kernel.Messaging;

/// <summary>
/// The JSON every Kafka message carries: <c>id</c>, <c>type</c>, <c>version</c>, <c>occurredAt</c>,
/// <c>traceparent</c> and <c>data</c>. Written by hand with <see cref="Utf8JsonWriter"/> so the event data, already
/// serialised when it went into the outbox, is copied in as raw JSON instead of being parsed and written again.
/// </summary>
internal static class Envelope
{
    public const int Version = 1;

    public static byte[] Write(Guid id, string type, DateTimeOffset occurredAt, string? traceParent, string data)
    {
        var buffer = new ArrayBufferWriter<byte>(256 + data.Length);
        using (var writer = new Utf8JsonWriter(buffer))
        {
            writer.WriteStartObject();
            writer.WriteString("id", id);
            writer.WriteString("type", type);
            writer.WriteNumber("version", Version);
            writer.WriteString("occurredAt", occurredAt);
            if (traceParent is null)
            {
                writer.WriteNull("traceparent");
            }
            else
            {
                writer.WriteString("traceparent", traceParent);
            }

            writer.WritePropertyName("data");
            writer.WriteRawValue(data, skipInputValidation: true);
            writer.WriteEndObject();
        }

        return buffer.WrittenSpan.ToArray();
    }

    /// <summary>
    /// Reads an envelope. Throws <see cref="JsonException"/> for anything malformed, including a version this code
    /// does not know: a message that cannot be read now will not become readable on a retry.
    /// </summary>
    public static Incoming Read(byte[] value, IntegrationEventTypes types)
    {
        using JsonDocument document = JsonDocument.Parse(value);
        JsonElement root = document.RootElement;

        try
        {
            int version = root.GetProperty("version").GetInt32();
            if (version != Version)
            {
                throw new JsonException($"Envelope version {version} is not supported; this consumer reads version {Version}.");
            }

            string type = root.GetProperty("type").GetString() ?? throw new JsonException("The envelope type is null.");

            return new Incoming(
                root.GetProperty("id").GetGuid(),
                type,
                root.GetProperty("occurredAt").GetDateTimeOffset(),
                root.TryGetProperty("traceparent", out JsonElement trace) ? trace.GetString() : null,
                types.Deserialize(type, root.GetProperty("data")));
        }
        catch (Exception ex) when (ex is KeyNotFoundException or FormatException or InvalidOperationException)
        {
            throw new JsonException($"The envelope is malformed: {ex.Message}", ex);
        }
    }
}

/// <summary>An envelope read off Kafka. <see cref="Event"/> is null when this service does not know the type.</summary>
public sealed record Incoming(Guid Id, string Type, DateTimeOffset OccurredAt, string? TraceParent, IIntegrationEvent? Event);
