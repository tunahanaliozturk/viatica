namespace Viatica.Contracts;

/// <summary>An event that crosses a service boundary over Kafka.</summary>
public interface IIntegrationEvent
{
    /// <summary>The Kafka message key. Events with the same key are delivered in order.</summary>
    string PartitionKey { get; }
}
