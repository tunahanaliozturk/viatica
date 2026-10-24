using System.Globalization;
using System.Text.Json.Serialization;

namespace Viatica.Contracts;

// Published by Booking on booking.v1, all keyed by trip so one trip's saga is ordered end to end.
//
// The *Requested events are commands in event clothing: Booking orchestrates the checkout saga and these are
// its instructions to the service that owns each item. Each has exactly one consumer. They stay on Booking's
// own topic so the saga's whole history reads from one place.

public sealed record StayHoldRequested(Guid TripId, Guid ItemId, Guid HoldId, string QuoteToken, int Quantity) : IIntegrationEvent
{
    [JsonIgnore]
    public string PartitionKey => TripId.ToString("N", CultureInfo.InvariantCulture);
}

public sealed record StayHoldCommitRequested(Guid TripId, Guid ItemId, Guid HoldId) : IIntegrationEvent
{
    [JsonIgnore]
    public string PartitionKey => TripId.ToString("N", CultureInfo.InvariantCulture);
}

public sealed record StayHoldReleaseRequested(Guid TripId, Guid ItemId, Guid HoldId) : IIntegrationEvent
{
    [JsonIgnore]
    public string PartitionKey => TripId.ToString("N", CultureInfo.InvariantCulture);
}

public sealed record StayBookingCancelRequested(Guid TripId, Guid ItemId, Guid HoldId) : IIntegrationEvent
{
    [JsonIgnore]
    public string PartitionKey => TripId.ToString("N", CultureInfo.InvariantCulture);
}

public sealed record CarHoldRequested(Guid TripId, Guid ItemId, Guid HoldId, string QuoteToken, int DriverAge) : IIntegrationEvent
{
    [JsonIgnore]
    public string PartitionKey => TripId.ToString("N", CultureInfo.InvariantCulture);
}

public sealed record CarHoldCommitRequested(Guid TripId, Guid ItemId, Guid HoldId) : IIntegrationEvent
{
    [JsonIgnore]
    public string PartitionKey => TripId.ToString("N", CultureInfo.InvariantCulture);
}

public sealed record CarHoldReleaseRequested(Guid TripId, Guid ItemId, Guid HoldId) : IIntegrationEvent
{
    [JsonIgnore]
    public string PartitionKey => TripId.ToString("N", CultureInfo.InvariantCulture);
}

public sealed record CarBookingCancelRequested(Guid TripId, Guid ItemId, Guid HoldId) : IIntegrationEvent
{
    [JsonIgnore]
    public string PartitionKey => TripId.ToString("N", CultureInfo.InvariantCulture);
}

/// <summary>
/// Ask Flights to create an airline order for a re-priced offer. Passenger names, dates of birth and documents
/// are personal data, so they never cross the broker in plain text: <see cref="EncryptedPassengers"/> is
/// AES-256-GCM ciphertext under a key only Booking and Flights hold, with the trip and item ids as associated
/// data (docs/design.md, "Personal data on the broker").
/// </summary>
public sealed record FlightOrderRequested(
    Guid TripId,
    Guid ItemId,
    string OfferId,
    Price ExpectedTotal,
    string EncryptedPassengers) : IIntegrationEvent
{
    [JsonIgnore]
    public string PartitionKey => TripId.ToString("N", CultureInfo.InvariantCulture);
}

public sealed record FlightTicketingRequested(Guid TripId, Guid ItemId, Guid OrderId) : IIntegrationEvent
{
    [JsonIgnore]
    public string PartitionKey => TripId.ToString("N", CultureInfo.InvariantCulture);
}

public sealed record FlightOrderVoidRequested(Guid TripId, Guid ItemId, Guid OrderId) : IIntegrationEvent
{
    [JsonIgnore]
    public string PartitionKey => TripId.ToString("N", CultureInfo.InvariantCulture);
}

public sealed record FlightOrderCancelRequested(Guid TripId, Guid ItemId, Guid OrderId) : IIntegrationEvent
{
    [JsonIgnore]
    public string PartitionKey => TripId.ToString("N", CultureInfo.InvariantCulture);
}

public sealed record PaymentAuthorizationRequested(Guid TripId, Guid PaymentId, Price Amount, string PaymentMethodToken) : IIntegrationEvent
{
    [JsonIgnore]
    public string PartitionKey => TripId.ToString("N", CultureInfo.InvariantCulture);
}

public sealed record PaymentCaptureRequested(Guid TripId, Guid PaymentId, Price Amount) : IIntegrationEvent
{
    [JsonIgnore]
    public string PartitionKey => TripId.ToString("N", CultureInfo.InvariantCulture);
}

public sealed record PaymentVoidRequested(Guid TripId, Guid PaymentId) : IIntegrationEvent
{
    [JsonIgnore]
    public string PartitionKey => TripId.ToString("N", CultureInfo.InvariantCulture);
}

public sealed record PaymentRefundRequested(Guid TripId, Guid PaymentId, Guid RefundId, Price Amount) : IIntegrationEvent
{
    [JsonIgnore]
    public string PartitionKey => TripId.ToString("N", CultureInfo.InvariantCulture);
}

public enum TripFailureReason
{
    ItemRejected,
    PaymentDeclined,
    TicketingRefused,
    DeadlineReached,
}

public sealed record TripItemSummary(Guid ItemId, TripItemKind Kind, Price Total);

public sealed record TripConfirmed(Guid TripId, Guid? TravellerId, Price Total, IReadOnlyList<TripItemSummary> Items) : IIntegrationEvent
{
    [JsonIgnore]
    public string PartitionKey => TripId.ToString("N", CultureInfo.InvariantCulture);
}

public sealed record TripFailed(Guid TripId, TripFailureReason Reason) : IIntegrationEvent
{
    [JsonIgnore]
    public string PartitionKey => TripId.ToString("N", CultureInfo.InvariantCulture);
}

public sealed record TripItemCancelled(Guid TripId, Guid ItemId, Price Refund) : IIntegrationEvent
{
    [JsonIgnore]
    public string PartitionKey => TripId.ToString("N", CultureInfo.InvariantCulture);
}
