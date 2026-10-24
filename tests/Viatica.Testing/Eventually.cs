namespace Viatica.Testing;

/// <summary>
/// Waits for something that happens asynchronously, a consumer's write or a relay's publish, instead of sleeping
/// for a guess. Fails with the last observed value, so a timeout says what was seen rather than only that it was
/// wrong.
/// </summary>
public static class Eventually
{
    private static readonly TimeSpan DefaultTimeout = TimeSpan.FromSeconds(30);

    public static async Task<T> MatchesAsync<T>(Func<Task<T>> observe, Func<T, bool> condition, TimeSpan? timeout = null)
    {
        ArgumentNullException.ThrowIfNull(observe);
        ArgumentNullException.ThrowIfNull(condition);

        DateTime deadline = DateTime.UtcNow + (timeout ?? DefaultTimeout);
        T last = await observe();

        while (!condition(last))
        {
            if (DateTime.UtcNow > deadline)
            {
                throw new TimeoutException($"The condition was not met within {timeout ?? DefaultTimeout}. Last observed: {last}");
            }

            await Task.Delay(50);
            last = await observe();
        }

        return last;
    }
}
