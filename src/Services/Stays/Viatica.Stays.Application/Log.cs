using Microsoft.Extensions.Logging;
using Viatica.Contracts;
using Viatica.Stays.Domain;

namespace Viatica.Stays.Application;

/// <summary>
/// Log lines of the hold handlers. Ids of holds and a rejection reason only: no quote tokens, which hold a price a
/// traveller was shown, and nothing about who is travelling.
/// </summary>
internal static partial class Log
{
    [LoggerMessage(1, LogLevel.Debug, "Hold {HoldId} was already placed; the request changes nothing")]
    public static partial void HoldAlreadyPlaced(this ILogger logger, Guid holdId);

    [LoggerMessage(2, LogLevel.Information, "Hold {HoldId} was rejected: {Reason}")]
    public static partial void HoldRejected(this ILogger logger, Guid holdId, HoldRejectionReason reason);

    [LoggerMessage(3, LogLevel.Debug, "Commit of hold {HoldId} changes nothing; the hold is {Status}")]
    public static partial void CommitSkipped(this ILogger logger, Guid holdId, HoldStatus? status);

    [LoggerMessage(4, LogLevel.Debug, "Release of hold {HoldId} changes nothing; the hold is {Status}")]
    public static partial void ReleaseSkipped(this ILogger logger, Guid holdId, HoldStatus status);

    [LoggerMessage(5, LogLevel.Warning, "Release of hold {HoldId}, which this service never placed; answering that nothing is held")]
    public static partial void ReleaseOfUnknownHold(this ILogger logger, Guid holdId);

    [LoggerMessage(6, LogLevel.Debug, "Cancel of hold {HoldId} changes nothing; the hold is {Status}")]
    public static partial void CancelSkipped(this ILogger logger, Guid holdId, HoldStatus? status);
}
