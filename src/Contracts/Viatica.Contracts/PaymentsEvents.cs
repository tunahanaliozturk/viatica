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
