using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Relevox;
using Viatica.Cars.Application;
using Viatica.Cars.Application.Features.Holds;

namespace Viatica.Cars.Infrastructure;

public sealed class HoldSweeperOptions
{
    public const string Section = "Cars:HoldSweeper";

    /// <summary>How often to look for holds past their 15 minutes. Expiry is therefore late by up to this much.</summary>
    public TimeSpan Interval { get; set; } = TimeSpan.FromSeconds(10);

    /// <summary>Holds expired per round; a full round starts the next at once.</summary>
    public int BatchSize { get; set; } = 100;
}

/// <summary>
/// Gives back the cars of holds nobody confirmed and tells Booking with <c>CarHoldExpired</c>. Each hold expires in
/// a transaction of its own, so one that fails (a commit won the race for it) does not hold up the rest, and any
/// number of instances can run: the hold row's version lets one of them win each hold.
/// </summary>
public sealed partial class HoldExpirySweeper(
    IServiceScopeFactory scopes, IOptions<HoldSweeperOptions> options, TimeProvider clock, ILogger<HoldExpirySweeper> logger)
    : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        using var timer = new PeriodicTimer(options.Value.Interval, clock);
        try
        {
            do
            {
                try
                {
                    await SweepAsync(stoppingToken);
                }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    // The next tick tries again; a sweeper that dies would leave holds unexpired until a restart.
                    LogSweepFailed(logger, ex);
                }
            }
            while (await timer.WaitForNextTickAsync(stoppingToken));
        }
        catch (OperationCanceledException)
        {
            // Stopping.
        }
    }

    /// <summary>Expires every hold that is due and returns how many it expired. Public so tests can sweep on demand.</summary>
    public async Task<int> SweepAsync(CancellationToken cancellationToken)
    {
        int expired = 0;
        int expiredInRound;
        IReadOnlyList<Guid> due;
        do
        {
            await using (AsyncServiceScope scope = scopes.CreateAsyncScope())
            {
                due = await scope.ServiceProvider.GetRequiredService<ISender>()
                    .Send(new FindExpiredHolds(options.Value.BatchSize), cancellationToken);
            }

            expiredInRound = 0;
            foreach (Guid holdId in due)
            {
                if (await ExpireAsync(holdId, cancellationToken))
                {
                    expiredInRound++;
                }
            }

            expired += expiredInRound;

            // Stop when a round expires nothing, so holds that keep failing cannot spin the loop.
        }
        while (due.Count == options.Value.BatchSize && expiredInRound > 0);

        return expired;
    }

    private async Task<bool> ExpireAsync(Guid holdId, CancellationToken cancellationToken)
    {
        await using AsyncServiceScope scope = scopes.CreateAsyncScope();
        try
        {
            Transition result = await scope.ServiceProvider.GetRequiredService<ISender>()
                .Send(new ExpireHold(holdId), cancellationToken);
            if (result == Transition.Applied)
            {
                CarsTelemetry.HoldsExpired.Add(1);
                LogExpired(logger, holdId);
                return true;
            }
        }
        catch (DbUpdateConcurrencyException)
        {
            // A commit or release got to the hold first. Its state is now not "held", which is the answer.
            LogLostRace(logger, holdId);
        }

        return false;
    }

    [LoggerMessage(1, LogLevel.Information, "Hold {HoldId} expired and its car is free again")]
    private static partial void LogExpired(ILogger logger, Guid holdId);

    [LoggerMessage(2, LogLevel.Debug, "Hold {HoldId} changed while it was being expired; left to the other writer")]
    private static partial void LogLostRace(ILogger logger, Guid holdId);

    [LoggerMessage(3, LogLevel.Error, "A hold expiry sweep failed")]
    private static partial void LogSweepFailed(ILogger logger, Exception exception);
}
