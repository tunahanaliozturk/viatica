namespace Viatica.Kernel.Web;

/// <summary>
/// Per service settings for the shared API conventions, bound from the <c>Viatica:Api</c> configuration section
/// and then from the delegate passed to <see cref="ViaticaApi.AddViaticaApi"/>.
/// </summary>
public sealed class ViaticaApiOptions
{
    public const string Section = "Viatica:Api";

    /// <summary>The <c>api-version</c> values this service answers, such as <c>2026-11-01</c>.</summary>
    public IList<string> SupportedApiVersions { get; } = [];

    /// <summary>
    /// Base64 key, at least 32 bytes, that signs skiptokens. Every instance of a service must share it, or a
    /// token issued by one instance fails on the next. Rotating it ends every paging session in flight.
    /// </summary>
    public string? SkipTokenKey { get; set; }

    /// <summary>
    /// How long a <c>Repeatability-Request-ID</c> is remembered. A request whose <c>Repeatability-First-Sent</c>
    /// is older than this is refused, because the server can no longer tell whether it already ran it.
    /// </summary>
    public TimeSpan RepeatabilityWindow { get; set; } = TimeSpan.FromHours(24);

    /// <summary>
    /// How long the claim of a running repeatable request lives without being renewed. A request that is still
    /// running renews it every third of this, so the claim outlives any handler; a process that dies stops renewing,
    /// and its id is free again after this long instead of after the whole window.
    /// </summary>
    public TimeSpan RepeatabilityLease { get; set; } = TimeSpan.FromSeconds(30);

    internal byte[] SkipTokenKeyBytes() => Convert.FromBase64String(SkipTokenKey!);

    internal IEnumerable<string> Validate()
    {
        if (SupportedApiVersions.Count == 0)
        {
            yield return "Viatica:Api:SupportedApiVersions needs at least one version.";
        }

        byte[]? key = null;
        try
        {
            key = SkipTokenKey is null ? null : Convert.FromBase64String(SkipTokenKey);
        }
        catch (FormatException)
        {
        }

        if (key is not { Length: >= 32 })
        {
            yield return "Viatica:Api:SkipTokenKey must be base64 of at least 32 random bytes.";
        }

        if (RepeatabilityWindow <= TimeSpan.Zero)
        {
            yield return "Viatica:Api:RepeatabilityWindow must be positive.";
        }

        if (RepeatabilityLease <= TimeSpan.Zero)
        {
            yield return "Viatica:Api:RepeatabilityLease must be positive.";
        }
    }
}
