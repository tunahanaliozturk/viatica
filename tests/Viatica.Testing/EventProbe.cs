using System.Text;
using System.Text.Json;
using Confluent.Kafka;

namespace Viatica.Testing;

/// <summary>
/// Reads a topic the way an outside consumer would and waits for a message matching a condition. It parses the
/// envelope itself rather than through the messaging library, so a test that passes also pins the wire shape.
/// </summary>
/// <example>
/// <code>
/// using EventProbe probe = kafka.Probe("stays.v1");
/// ProbedMessage placed = await probe.WaitForAsync(m => m.Type == "StayHoldPlaced" &amp;&amp; m.Key == holdId);
/// </code>
/// </example>
public sealed class EventProbe : IDisposable
{
    private static readonly TimeSpan DefaultTimeout = TimeSpan.FromSeconds(30);

    private readonly IConsumer<string, byte[]> _consumer;
    private readonly List<ProbedMessage> _received = [];

    internal EventProbe(string bootstrapServers, string topic)
    {
        // A group of its own that starts at the earliest offset: the probe sees what was published before it
        // started, so a test never races its own setup.
        _consumer = new ConsumerBuilder<string, byte[]>(new ConsumerConfig
        {
            BootstrapServers = bootstrapServers,
            GroupId = $"probe-{Guid.NewGuid():N}",
            AutoOffsetReset = AutoOffsetReset.Earliest,
            EnableAutoCommit = false,
        }).Build();
        _consumer.Subscribe(topic);
    }

    /// <summary>Every message read so far, in the order the probe read them.</summary>
    public IReadOnlyList<ProbedMessage> Received => _received;

    /// <summary>Waits for a message matching <paramref name="predicate"/> and returns it.</summary>
    public Task<ProbedMessage> WaitForAsync(Func<ProbedMessage, bool> predicate, TimeSpan? timeout = null)
    {
        ArgumentNullException.ThrowIfNull(predicate);

        return Task.Run(() =>
        {
            Pump(() => _received.Any(predicate), timeout, () => "No matching message arrived");
            return _received.First(predicate);
        });
    }

    /// <summary>Waits until <paramref name="count"/> messages matching <paramref name="predicate"/> have been read.</summary>
    public Task<IReadOnlyList<ProbedMessage>> WaitForCountAsync(
        int count,
        Func<ProbedMessage, bool> predicate,
        TimeSpan? timeout = null)
    {
        ArgumentNullException.ThrowIfNull(predicate);

        return Task.Run(() =>
        {
            Pump(() => _received.Count(predicate) >= count, timeout, () => $"Read {_received.Count(predicate)} of {count} matching messages");
            return (IReadOnlyList<ProbedMessage>)[.. _received.Where(predicate)];
        });
    }

    // Consume blocks the calling thread, which is why both waits run this on the pool, not on the test's thread.
    private void Pump(Func<bool> done, TimeSpan? timeout, Func<string> failure)
    {
        TimeSpan limit = timeout ?? DefaultTimeout;
        DateTime deadline = DateTime.UtcNow + limit;

        while (!done())
        {
            if (DateTime.UtcNow > deadline)
            {
                throw new TimeoutException($"{failure()} within {limit}; {_received.Count} message(s) read in all.");
            }

            if (_consumer.Consume(TimeSpan.FromMilliseconds(100)) is { IsPartitionEOF: false } result)
            {
                _received.Add(ProbedMessage.From(result));
            }
        }
    }

    public void Dispose()
    {
        _consumer.Close();
        _consumer.Dispose();
    }
}

/// <summary>One message as it sat on the topic: Kafka key and headers, and the envelope fields parsed out.</summary>
public sealed record ProbedMessage(
    string Key,
    int Partition,
    long Offset,
    IReadOnlyDictionary<string, string> Headers,
    Guid Id,
    string Type,
    int Version,
    DateTimeOffset OccurredAt,
    string? TraceParent,
    JsonElement Data)
{
    internal static ProbedMessage From(ConsumeResult<string, byte[]> result)
    {
        using JsonDocument document = JsonDocument.Parse(result.Message.Value);
        JsonElement root = document.RootElement;

        return new ProbedMessage(
            result.Message.Key,
            result.Partition.Value,
            result.Offset.Value,
            result.Message.Headers?.ToDictionary(static h => h.Key, static h => Encoding.UTF8.GetString(h.GetValueBytes()))
                ?? [],
            root.GetProperty("id").GetGuid(),
            root.GetProperty("type").GetString()!,
            root.GetProperty("version").GetInt32(),
            root.GetProperty("occurredAt").GetDateTimeOffset(),
            root.TryGetProperty("traceparent", out JsonElement trace) ? trace.GetString() : null,
            root.GetProperty("data").Clone());
    }

    /// <summary>Reads <see cref="Data"/> as a string property, for predicates that match on a field.</summary>
    public string? DataString(string property) =>
        Data.TryGetProperty(property, out JsonElement value) ? value.ToString() : null;
}
