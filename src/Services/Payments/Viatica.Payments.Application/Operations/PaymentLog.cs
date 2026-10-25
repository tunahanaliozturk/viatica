using Microsoft.Extensions.Logging;

namespace Viatica.Payments.Application.Operations;

// Everything the payment handlers log. The rule for this file: ids, operation names, outcome names and counts. Never a
// card number, a card token, a payment method token or an exception message from a path that has touched a card.
// tests/Viatica.Payments.IntegrationTests/NoCardDataStoredOrLoggedTests holds the line by capturing every log of a
// real run.
internal static partial class PaymentLog
{
    [LoggerMessage(Level = LogLevel.Information, Message = "{Operation} {OperationId} of trip {TripId} concluded as {Result}{Reason}")]
    public static partial void Concluded(ILogger logger, string operation, Guid tripId, Guid operationId, string result, string reason);

    [LoggerMessage(Level = LogLevel.Debug, Message = "{Operation} {OperationId} of trip {TripId} was already carried out; this repeat changes nothing")]
    public static partial void AlreadyDone(ILogger logger, string operation, Guid tripId, Guid operationId);

    [LoggerMessage(Level = LogLevel.Information,
        Message = "{Operation} of trip {TripId} got no answer from the provider; looking the operation up by its key before deciding")]
    public static partial void LookingUp(ILogger logger, string operation, Guid tripId);

    [LoggerMessage(Level = LogLevel.Information, Message = "The lookup found the provider answer to {Operation} of trip {TripId}: the call had been carried out")]
    public static partial void FoundByLookup(ILogger logger, string operation, Guid tripId);

    [LoggerMessage(Level = LogLevel.Information,
        Message = "The provider has no record of {Operation} of trip {TripId}; sending it again under the same key")]
    public static partial void Resending(ILogger logger, string operation, Guid tripId);

    [LoggerMessage(Level = LogLevel.Warning,
        Message = "{Operation} of trip {TripId} could not be resolved ({Why}); nothing is decided and the request will be tried again")]
    public static partial void Unresolved(ILogger logger, string operation, Guid tripId, string why);

    [LoggerMessage(Level = LogLevel.Information, Message = "Trip {TripId}: the provider was certainly not reached for {Operation}")]
    public static partial void NothingSent(ILogger logger, string operation, Guid tripId);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Trip {TripId}: {Operation} {OperationId} names payment {PaymentId}, which this service has no record of")]
    public static partial void UnknownPayment(ILogger logger, string operation, Guid tripId, Guid operationId, Guid paymentId);
}
