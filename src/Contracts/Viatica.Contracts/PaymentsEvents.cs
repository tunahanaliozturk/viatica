using System.Globalization;
using System.Text.Json.Serialization;

namespace Viatica.Contracts;

// Published by Payments on payments.v1, keyed by trip. Card data never appears here: Booking only ever holds a
// payment method token issued by Payments.

public sealed record PaymentAuthorized(Guid PaymentId, Guid TripId, Price Amount) : IIntegrationEvent
{
    [JsonIgnore]
    public string PartitionKey => TripId.ToString("N", CultureInfo.InvariantCulture);
}

/// <summary>The provider declined the authorisation. <see cref="Reason"/> is a provider code, never card data.</summary>
public sealed record PaymentDeclined(Guid PaymentId, Guid TripId, string Reason) : IIntegrationEvent
{
    [JsonIgnore]
    public string PartitionKey => TripId.ToString("N", CultureInfo.InvariantCulture);
}

public sealed record PaymentCaptured(Guid PaymentId, Guid TripId, Price Amount) : IIntegrationEvent
{
    [JsonIgnore]
    public string PartitionKey => TripId.ToString("N", CultureInfo.InvariantCulture);
}

public sealed record PaymentVoided(Guid PaymentId, Guid TripId) : IIntegrationEvent
{
    [JsonIgnore]
    public string PartitionKey => TripId.ToString("N", CultureInfo.InvariantCulture);
}

public sealed record PaymentRefunded(Guid PaymentId, Guid TripId, Guid RefundId, Price Amount) : IIntegrationEvent
{
    [JsonIgnore]
    public string PartitionKey => TripId.ToString("N", CultureInfo.InvariantCulture);
}

/// <summary>The payment operations that can be refused after the payment exists. An authorisation is declined instead.</summary>
public enum PaymentOperation
{
    Capture,
    Void,
    Refund,
}

/// <summary>
/// The answer to a capture, void or refund request that was not carried out, so Booking is never left waiting. Nothing
/// moved: either a money rule refused it before the provider was asked, or the provider refused it.
/// <see cref="OperationId"/> is the payment id for a capture and a void, and the refund id for a refund.
/// <see cref="Reason"/> is a code such as <c>ExceedsAuthorization</c> or a provider code, never card data.
/// </summary>
public sealed record PaymentOperationRefused(Guid PaymentId, Guid TripId, PaymentOperation Operation, Guid OperationId, string Reason) : IIntegrationEvent
{
    [JsonIgnore]
    public string PartitionKey => TripId.ToString("N", CultureInfo.InvariantCulture);
}
