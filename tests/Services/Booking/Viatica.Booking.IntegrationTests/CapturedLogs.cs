using Microsoft.Extensions.Logging;

namespace Viatica.Booking.IntegrationTests;

/// <summary>
/// Keeps every log entry the host writes, from every category at every level, with its formatted message, its
/// structured values and the full text of any exception. That is everything a person reading the logs of a real
/// deployment could ever see, so a value missing from here is a value missing from there.
/// </summary>
internal sealed class CapturedLogs : ILoggerProvider
{
    private readonly List<string> entries = [];

    public IReadOnlyList<string> Entries
    {
        get
        {
            lock (entries)
            {
                return [.. entries];
            }
        }
    }

    public ILogger CreateLogger(string categoryName) => new Capture(this, categoryName);

    public void Dispose()
    {
    }

    /// <summary>The entries that contain <paramref name="text"/>, compared without regard to case.</summary>
    public IReadOnlyList<string> Containing(string text) =>
        [.. Entries.Where(entry => entry.Contains(text, StringComparison.OrdinalIgnoreCase))];

    private sealed class Capture(CapturedLogs owner, string category) : ILogger
    {
        public IDisposable? BeginScope<TState>(TState state)
            where TState : notnull => null;

        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
        {
            // The message as a person sees it, then every structured value (a template can hide a value from the
            // message but not from the state), then the exception with its inner exceptions and stack.
            string structured = state is IEnumerable<KeyValuePair<string, object?>> pairs
                ? string.Join(" ", pairs.Select(static p => $"{p.Key}={p.Value}"))
                : "";
            string entry = $"{category} [{logLevel}] {formatter(state, exception)} | {structured} | {exception}";

            lock (owner.entries)
            {
                owner.entries.Add(entry);
            }
        }
    }
}
