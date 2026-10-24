using System.Text.Json;
using Confluent.Kafka;
using Microsoft.Extensions.Logging;
using Viatica.Contracts;
using Viatica.Kernel.Messaging;
using Viatica.Search.Application;
using Viatica.Search.Application.Projection;
using Viatica.Search.Infrastructure.Indexing;

namespace Viatica.Search.Infrastructure.Messaging;

/// <summary>A message that could not be applied, and why. The live consumer parks it; a rebuild stops on it.</summary>
internal sealed record Poisoned(ConsumeResult<string, byte[]> Message, Exception Error, int Attempts);

/// <summary>
/// Turns a batch of Kafka messages into projection writes. Shared by the live consumer and the rebuild, so both
/// read the topics the same way: unreadable messages are set aside, event types the projection does not read are
/// skipped, and the rest are applied in order.
/// </summary>
internal sealed partial class BatchApplier(
    ProjectionWriter writer, IntegrationEventTypes types, SearchMessagingOptions options, ILogger logger)
{
    /// <summary>
    /// Applies the batch and returns the messages that could not be applied. A failure that can pass (the index
    /// is restarting, overloaded) is waited out, because parking messages for something that is not their fault
    /// would lose them; a failure that cannot pass is narrowed down to the message that causes it.
    /// </summary>
    public async Task<IReadOnlyList<Poisoned>> ApplyAsync(
        IReadOnlyList<ConsumeResult<string, byte[]>> messages, CancellationToken cancellationToken)
    {
        List<Poisoned> poisoned = [];
        List<(ConsumeResult<string, byte[]> Message, ProjectionEvent Event)> events = [];

        foreach (ConsumeResult<string, byte[]> message in messages)
        {
            Incoming incoming;
            try
            {
                incoming = types.Read(message.Message.Value);
            }
            catch (JsonException ex)
            {
                // Reading it again gives the same answer, so it is not retried.
                poisoned.Add(new Poisoned(message, ex, 0));
                SearchTelemetry.Skipped("(unreadable)", "dead_lettered");
                continue;
            }

            if (incoming.Event is null || !writer.Reads(incoming.Event))
            {
                // The topic carries every event of its service; most are for Booking, not for search.
                SearchTelemetry.Skipped(incoming.Type, "ignored");
                continue;
            }

            events.Add((message, new ProjectionEvent(incoming.Id, incoming.OccurredAt, incoming.Event)));
        }

        if (events.Count == 0)
        {
            return poisoned;
        }

        try
        {
            await WithRetryAsync(() => writer.ApplyAsync([.. events.Select(static e => e.Event)], cancellationToken), cancellationToken);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            LogBatchFailed(logger, events.Count, ex);
            foreach ((ConsumeResult<string, byte[]> message, ProjectionEvent @event) in events)
            {
                try
                {
                    await WithRetryAsync(() => writer.ApplyAsync([@event], cancellationToken), cancellationToken);
                }
                catch (Exception single) when (single is not OperationCanceledException)
                {
                    poisoned.Add(new Poisoned(message, single, 1));
                    SearchTelemetry.Skipped(@event.Event.GetType().Name, "dead_lettered");
                }
            }
        }

        return poisoned;
    }

    // Transient answers (no connection, 429, 5xx) are retried without end, with a capped backoff: the consumer is
    // useless until the index is back, and giving up would park good messages. Cancellation is the way out.
    private async Task WithRetryAsync(Func<Task> work, CancellationToken cancellationToken)
    {
        for (int attempt = 1; ; attempt++)
        {
            try
            {
                await work();
                return;
            }
            catch (OpenSearchRequestException ex) when (ex.IsTransient && !cancellationToken.IsCancellationRequested)
            {
                TimeSpan wait = TimeSpan.FromTicks(Math.Min(options.MaxRetryDelay.Ticks, options.RetryDelay.Ticks << Math.Min(attempt - 1, 10)));
                LogIndexUnavailable(logger, attempt, wait, ex);
                await Task.Delay(wait, cancellationToken);
            }
        }
    }

    [LoggerMessage(Level = LogLevel.Warning, Message = "The index is unavailable (attempt {Attempt}); waiting {Wait} before the batch is tried again")]
    private static partial void LogIndexUnavailable(ILogger logger, int attempt, TimeSpan wait, Exception exception);

    [LoggerMessage(Level = LogLevel.Warning, Message = "A batch of {Count} events failed; applying them one by one to find the one that cannot be applied")]
    private static partial void LogBatchFailed(ILogger logger, int count, Exception exception);
}
