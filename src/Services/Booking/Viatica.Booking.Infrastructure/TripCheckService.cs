using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Viatica.Booking.Application;

namespace Viatica.Booking.Infrastructure;

/// <summary>
/// Drives what the clock causes: it wakes every <c>interval</c>, finds the trips with something due, oldest first, and
/// runs each in a transaction and a scope of its own. A trip whose deadline has passed unwinds here whether or not any
/// event ever arrives for it.
/// </summary>
/// <remarks>
/// A scope per trip keeps a failure local. Every instance runs one; two reaching the same trip are ordered by the row
/// lock, and the second finds nothing left to do.
/// </remarks>
internal sealed partial class TripCheckService(IServiceScopeFactory scopes, ILogger<TripCheckService> logger, TimeSpan interval, int batchSize)
    : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                // A full batch means there may be more due; go again at once instead of waiting a whole interval.
                while (await CheckOnceAsync(stoppingToken) == batchSize)
                {
                }
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                // The database is down. The next tick tries again; the check keeps no state of its own.
                LogCheckFailed(logger, ex);
            }

            try
            {
                await Task.Delay(interval, stoppingToken);
            }
            catch (OperationCanceledException)
            {
                return;
            }
        }
    }

    private async Task<int> CheckOnceAsync(CancellationToken cancellationToken)
    {
        IReadOnlyList<Guid> due;
        await using (AsyncServiceScope finder = scopes.CreateAsyncScope())
        {
            due = await finder.ServiceProvider.GetRequiredService<ScheduledChecks>().FindDueAsync(batchSize, cancellationToken);
        }

        foreach (Guid tripId in due)
        {
            try
            {
                await using AsyncServiceScope scope = scopes.CreateAsyncScope();
                await scope.ServiceProvider.GetRequiredService<ScheduledChecks>().RunAsync(tripId, cancellationToken);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                LogTripFailed(logger, tripId, ex);
            }
        }

        return due.Count;
    }

    [LoggerMessage(1, LogLevel.Error, "Looking for trips with something due failed; trying again at the next tick")]
    private static partial void LogCheckFailed(ILogger logger, Exception exception);

    [LoggerMessage(2, LogLevel.Error, "The scheduled check of trip {TripId} failed; it stays due and is tried again")]
    private static partial void LogTripFailed(ILogger logger, Guid tripId, Exception exception);
}
