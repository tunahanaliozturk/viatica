using System.Collections.Frozen;

namespace Viatica.Contracts;

public static class Topics
{
    public const string Stays = "stays.v1";
    public const string Cars = "cars.v1";
    public const string Flights = "flights.v1";
    public const string Booking = "booking.v1";
    public const string Payments = "payments.v1";

    /// <summary>The dead letter twin every consumer writes poison messages to.</summary>
    public static string DeadLetter(string topic) => topic + ".dlq";
}

/// <summary>
/// Every integration event and the one topic it is published on. The type name is the <c>type</c> field of the
/// envelope, so renaming a record is a breaking change; contract tests pin both the names and the JSON.
/// </summary>
public static class EventCatalog
{
    public static FrozenDictionary<Type, string> TopicByType { get; } = new Dictionary<Type, string>
    {
        [typeof(StayListingUpdated)] = Topics.Stays,
        [typeof(StayUnpublished)] = Topics.Stays,
        [typeof(StayAvailabilityChanged)] = Topics.Stays,
        [typeof(StayHoldPlaced)] = Topics.Stays,
        [typeof(StayHoldRejected)] = Topics.Stays,
        [typeof(StayHoldCommitted)] = Topics.Stays,
        [typeof(StayHoldReleased)] = Topics.Stays,
        [typeof(StayHoldExpired)] = Topics.Stays,
        [typeof(StayBookingCancelled)] = Topics.Stays,

        [typeof(CarLocationUpdated)] = Topics.Cars,
        [typeof(CarLocationUnpublished)] = Topics.Cars,
        [typeof(CarAvailabilityChanged)] = Topics.Cars,
        [typeof(CarHoldPlaced)] = Topics.Cars,
        [typeof(CarHoldRejected)] = Topics.Cars,
        [typeof(CarHoldCommitted)] = Topics.Cars,
        [typeof(CarHoldReleased)] = Topics.Cars,
        [typeof(CarHoldExpired)] = Topics.Cars,
        [typeof(CarBookingCancelled)] = Topics.Cars,

        [typeof(FlightOrderCreated)] = Topics.Flights,
        [typeof(FlightOrderRejected)] = Topics.Flights,
        [typeof(FlightTicketed)] = Topics.Flights,
        [typeof(FlightTicketingRefused)] = Topics.Flights,
        [typeof(FlightOrderVoided)] = Topics.Flights,
        [typeof(FlightOrderCancelled)] = Topics.Flights,

        [typeof(StayHoldRequested)] = Topics.Booking,
        [typeof(StayHoldCommitRequested)] = Topics.Booking,
        [typeof(StayHoldReleaseRequested)] = Topics.Booking,
        [typeof(StayBookingCancelRequested)] = Topics.Booking,
        [typeof(CarHoldRequested)] = Topics.Booking,
        [typeof(CarHoldCommitRequested)] = Topics.Booking,
        [typeof(CarHoldReleaseRequested)] = Topics.Booking,
        [typeof(CarBookingCancelRequested)] = Topics.Booking,
        [typeof(FlightOrderRequested)] = Topics.Booking,
        [typeof(FlightTicketingRequested)] = Topics.Booking,
        [typeof(FlightOrderVoidRequested)] = Topics.Booking,
        [typeof(FlightOrderCancelRequested)] = Topics.Booking,
        [typeof(PaymentAuthorizationRequested)] = Topics.Booking,
        [typeof(PaymentCaptureRequested)] = Topics.Booking,
        [typeof(PaymentVoidRequested)] = Topics.Booking,
        [typeof(PaymentRefundRequested)] = Topics.Booking,
        [typeof(TripConfirmed)] = Topics.Booking,
        [typeof(TripFailed)] = Topics.Booking,
        [typeof(TripItemCancelled)] = Topics.Booking,
        [typeof(TripStateChanged)] = Topics.Booking,
        [typeof(TripItemStateChanged)] = Topics.Booking,
        [typeof(TripClaimed)] = Topics.Booking,

        [typeof(PaymentAuthorized)] = Topics.Payments,
        [typeof(PaymentDeclined)] = Topics.Payments,
        [typeof(PaymentCaptured)] = Topics.Payments,
        [typeof(PaymentVoided)] = Topics.Payments,
        [typeof(PaymentRefunded)] = Topics.Payments,
        [typeof(PaymentOperationRefused)] = Topics.Payments,
    }.ToFrozenDictionary();

    public static FrozenDictionary<string, Type> TypeByName { get; } =
        TopicByType.Keys.ToFrozenDictionary(static type => type.Name, StringComparer.Ordinal);
}
