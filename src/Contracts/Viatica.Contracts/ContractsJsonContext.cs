using System.Text.Json.Serialization;

namespace Viatica.Contracts;

/// <summary>
/// Source-generated serialisation for every integration event: no reflection on the hot path, and a type left
/// out of this list fails the contract tests instead of failing in production.
/// </summary>
[JsonSourceGenerationOptions(
    PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase,
    DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    UseStringEnumConverter = true)]
[JsonSerializable(typeof(StayListingUpdated))]
[JsonSerializable(typeof(StayUnpublished))]
[JsonSerializable(typeof(StayAvailabilityChanged))]
[JsonSerializable(typeof(StayHoldPlaced))]
[JsonSerializable(typeof(StayHoldRejected))]
[JsonSerializable(typeof(StayHoldCommitted))]
[JsonSerializable(typeof(StayHoldReleased))]
[JsonSerializable(typeof(StayHoldExpired))]
[JsonSerializable(typeof(StayBookingCancelled))]
[JsonSerializable(typeof(CarLocationUpdated))]
[JsonSerializable(typeof(CarLocationUnpublished))]
[JsonSerializable(typeof(CarAvailabilityChanged))]
[JsonSerializable(typeof(CarHoldPlaced))]
[JsonSerializable(typeof(CarHoldRejected))]
[JsonSerializable(typeof(CarHoldCommitted))]
[JsonSerializable(typeof(CarHoldReleased))]
[JsonSerializable(typeof(CarHoldExpired))]
[JsonSerializable(typeof(CarBookingCancelled))]
[JsonSerializable(typeof(FlightOrderCreated))]
[JsonSerializable(typeof(FlightOrderRejected))]
[JsonSerializable(typeof(FlightTicketed))]
[JsonSerializable(typeof(FlightTicketingRefused))]
[JsonSerializable(typeof(FlightOrderVoided))]
[JsonSerializable(typeof(FlightOrderCancelled))]
[JsonSerializable(typeof(StayHoldRequested))]
[JsonSerializable(typeof(StayHoldCommitRequested))]
[JsonSerializable(typeof(StayHoldReleaseRequested))]
[JsonSerializable(typeof(StayBookingCancelRequested))]
[JsonSerializable(typeof(CarHoldRequested))]
[JsonSerializable(typeof(CarHoldCommitRequested))]
[JsonSerializable(typeof(CarHoldReleaseRequested))]
[JsonSerializable(typeof(CarBookingCancelRequested))]
[JsonSerializable(typeof(FlightOrderRequested))]
[JsonSerializable(typeof(FlightTicketingRequested))]
[JsonSerializable(typeof(FlightOrderVoidRequested))]
[JsonSerializable(typeof(FlightOrderCancelRequested))]
[JsonSerializable(typeof(PaymentAuthorizationRequested))]
[JsonSerializable(typeof(PaymentCaptureRequested))]
[JsonSerializable(typeof(PaymentVoidRequested))]
[JsonSerializable(typeof(PaymentRefundRequested))]
[JsonSerializable(typeof(TripConfirmed))]
[JsonSerializable(typeof(TripFailed))]
[JsonSerializable(typeof(TripItemCancelled))]
[JsonSerializable(typeof(PaymentAuthorized))]
[JsonSerializable(typeof(PaymentDeclined))]
[JsonSerializable(typeof(PaymentCaptured))]
[JsonSerializable(typeof(PaymentVoided))]
[JsonSerializable(typeof(PaymentRefunded))]
[JsonSerializable(typeof(PaymentOperationRefused))]
public sealed partial class ContractsJsonContext : JsonSerializerContext;
