using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Viatica.Stays.Application.Features.Holds;

namespace Viatica.Stays.Infrastructure;

/// <summary>
/// Gives back the rooms of holds nobody committed in time. It wakes every <c>interval</c>, finds the holds that are
/// due, oldest first, and expires each in a transaction and a scope of its own.
/// </summary>
/// <remarks>
/// A scope per hold keeps a failure local: a hold whose expiry throws leaves nothing half-written in a shared
/// DbContext for the next one to save. Expiry is not instant, by design: a hold lives up to one interval beyond its
/// time. Commit does not rely on the sweeper for that, it refuses an expired hold itself.
/// </remarks>
internal sealed partial class HoldSweeper(IServiceScopeFactory scopes, ILogger<HoldSweeper> logger, TimeSpan interval, int batchSize)
    : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                // A full batch means there may be more due; go again at once instead of waiting a whole interval.
                while (await SweepOnceAsync(stoppingToken) == batchSize)
                {
                }
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                // The database or the network is down. The next tick tries again; the sweeper has no state to lose.
                LogSweepFailed(logger, ex);
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

    private async Task<int> SweepOnceAsync(CancellationToken cancellationToken)
    {
        IReadOnlyList<Guid> due;
        await using (AsyncServiceScope finder = scopes.CreateAsyncScope())
        {
            due = await finder.ServiceProvider.GetRequiredService<ExpireDueHolds>().FindDueAsync(batchSize, cancellationToken);
        }

        foreach (Guid holdId in due)
        {
            try
            {
                await using AsyncServiceScope scope = scopes.CreateAsyncScope();
                await scope.ServiceProvider.GetRequiredService<ExpireDueHolds>().ExpireAsync(holdId, cancellationToken);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                LogExpireFailed(logger, holdId, ex);
            }
        }

        return due.Count;
    }

    [LoggerMessage(1, LogLevel.Error, "Looking for expired holds failed; trying again at the next tick")]
    private static partial void LogSweepFailed(ILogger logger, Exception exception);

    [LoggerMessage(2, LogLevel.Error, "Expiring hold {HoldId} failed; it stays due and is tried again")]
    private static partial void LogExpireFailed(ILogger logger, Guid holdId, Exception exception);
}
