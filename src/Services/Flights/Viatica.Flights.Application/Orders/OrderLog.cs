using Microsoft.Extensions.Logging;
using Viatica.Contracts;

namespace Viatica.Flights.Application.Orders;

// Everything the order handlers log. The rule for this file: ids, airline codes, counts and outcome names. Never a
// passenger's name, date of birth or document, never an offer or booking reference, and never an exception message
// from a path that has touched a passenger list. tests/Viatica.Flights.IntegrationTests/NoPassengerDataInLogsTests
// holds the line by capturing every log of a real run.
internal static partial class OrderLog
{
    [LoggerMessage(Level = LogLevel.Information, Message = "Order for item {ItemId} of trip {TripId} created as {OrderId} at {Airline}")]
    public static partial void Created(ILogger logger, Guid tripId, Guid itemId, Guid orderId, string airline);

    [LoggerMessage(Level = LogLevel.Information, Message = "Order for item {ItemId} of trip {TripId} rejected: {Reason}")]
    public static partial void Rejected(ILogger logger, Guid tripId, Guid itemId, FlightRejectionReason reason);

    [LoggerMessage(Level = LogLevel.Warning,
        Message = "The passengers for item {ItemId} of trip {TripId} were refused: {Problems} (positions and fields only)")]
    public static partial void PassengersRefused(ILogger logger, Guid tripId, Guid itemId, string problems);

    [LoggerMessage(Level = LogLevel.Warning,
        Message = "The sealed passenger list for item {ItemId} of trip {TripId} did not authenticate or was not a passenger list")]
    public static partial void PassengersUnreadable(ILogger logger, Guid tripId, Guid itemId);

    [LoggerMessage(Level = LogLevel.Information,
        Message = "Creating the order for item {ItemId} at {Airline} timed out or failed in flight; looking it up by key before deciding")]
    public static partial void LookingUpAfterTimeout(ILogger logger, Guid itemId, string airline);

    [LoggerMessage(Level = LogLevel.Information, Message = "The lookup found the airline's order for item {ItemId}: the creation had succeeded")]
    public static partial void FoundAfterTimeout(ILogger logger, Guid itemId);

    [LoggerMessage(Level = LogLevel.Debug, Message = "Item {ItemId} already has order {OrderId} in state {Status}; answering again without changing anything")]
    public static partial void AlreadyOrdered(ILogger logger, Guid itemId, Guid orderId, Domain.FlightOrderStatus status);

    [LoggerMessage(Level = LogLevel.Information, Message = "Order {OrderId} ticketed")]
    public static partial void Ticketed(ILogger logger, Guid orderId);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Ticketing order {OrderId} was refused or could not be done: {Outcome}")]
    public static partial void TicketingRefused(ILogger logger, Guid orderId, string outcome);

    [LoggerMessage(Level = LogLevel.Information, Message = "Order {OrderId} voided")]
    public static partial void Voided(ILogger logger, Guid orderId);

    [LoggerMessage(Level = LogLevel.Information, Message = "Order {OrderId} cancelled")]
    public static partial void Cancelled(ILogger logger, Guid orderId);

    [LoggerMessage(Level = LogLevel.Information,
        Message = "The void window of order {OrderId} is closed at the airline; cancelling it under the fare rules instead")]
    public static partial void VoidWindowClosed(ILogger logger, Guid orderId);

    [LoggerMessage(Level = LogLevel.Warning, Message = "The airline no longer holds order {OrderId}; treating the request as done")]
    public static partial void AirlineHasNoOrder(ILogger logger, Guid orderId);

    [LoggerMessage(Level = LogLevel.Debug, Message = "{Request} for order {OrderId} is a no-op: the order is {Status}")]
    public static partial void NothingToDo(ILogger logger, string request, Guid orderId, Domain.FlightOrderStatus status);

    [LoggerMessage(Level = LogLevel.Error, Message = "{Request} named order {OrderId} for item {ItemId}, which this service has no record of")]
    public static partial void UnknownOrder(ILogger logger, string request, Guid orderId, Guid itemId);
}
