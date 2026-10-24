using System.Globalization;
using System.Text.Json.Serialization;

namespace Viatica.Contracts;

// Published by Flights on flights.v1, all keyed by trip. Flight offers and searches never go on Kafka: they live
// for minutes, belong to the airlines, and only matter to the traveller looking at them.

public enum FlightRejectionReason
{
    OfferExpired,
    PriceChanged,
    SoldOut,
    SupplierUnavailable,
}

public sealed record FlightOrderCreated(
    Guid OrderId,
    Guid TripId,
    Guid ItemId,
    string AirlineCode,
    string BookingReference,
    DateTimeOffset TicketingTimeLimit,
    Price Total) : IIntegrationEvent
{
    [JsonIgnore]
    public string PartitionKey => TripId.ToString("N", CultureInfo.InvariantCulture);
}

public sealed record FlightOrderRejected(Guid TripId, Guid ItemId, FlightRejectionReason Reason) : IIntegrationEvent
{
    [JsonIgnore]
    public string PartitionKey => TripId.ToString("N", CultureInfo.InvariantCulture);
}

public sealed record FlightTicketed(Guid OrderId, Guid TripId, Guid ItemId, IReadOnlyList<string> TicketNumbers) : IIntegrationEvent
{
    [JsonIgnore]
    public string PartitionKey => TripId.ToString("N", CultureInfo.InvariantCulture);
}

/// <summary>The airline refused to issue tickets. <see cref="Reason"/> is the airline's text, for operators.</summary>
public sealed record FlightTicketingRefused(Guid OrderId, Guid TripId, Guid ItemId, string Reason) : IIntegrationEvent
{
    [JsonIgnore]
    public string PartitionKey => TripId.ToString("N", CultureInfo.InvariantCulture);
}

/// <summary>An unticketed order was released, or tickets were voided inside the airline's void window.</summary>
public sealed record FlightOrderVoided(Guid OrderId, Guid TripId, Guid ItemId) : IIntegrationEvent
{
    [JsonIgnore]
    public string PartitionKey => TripId.ToString("N", CultureInfo.InvariantCulture);
}

/// <summary>A ticketed order was cancelled under the fare rules; <see cref="Refund"/> may be zero.</summary>
public sealed record FlightOrderCancelled(Guid OrderId, Guid TripId, Guid ItemId, Price Refund) : IIntegrationEvent
{
    [JsonIgnore]
    public string PartitionKey => TripId.ToString("N", CultureInfo.InvariantCulture);
}
