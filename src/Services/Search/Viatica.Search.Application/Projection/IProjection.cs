using Viatica.Contracts;

namespace Viatica.Search.Application.Projection;

/// <summary>
/// How events of one family change one kind of document. Pure: it reads and edits the document it is given and
/// touches nothing else, so ordering and duplicate delivery can be tested without a broker or an index.
/// </summary>
public interface IProjection<TDocument>
    where TDocument : class
{
    /// <summary>True for the event types this projection reads; every other type on the topic is ignored.</summary>
    bool Reads(IIntegrationEvent message);

    /// <summary>The id of the document the event belongs to.</summary>
    string KeyOf(IIntegrationEvent message);

    /// <summary>
    /// Applies <paramref name="message"/> to <paramref name="current"/> (null when the document does not exist yet) and
    /// returns the document to keep. When nothing in the event is newer than what the document holds the outcome is
    /// <see cref="ProjectionOutcome.Dropped"/> and the document is returned as it was.
    /// </summary>
    /// <param name="firstDay">First day number still worth keeping; earlier nights are trimmed.</param>
    TDocument? Apply(TDocument? current, IIntegrationEvent message, int firstDay, out ProjectionOutcome outcome);
}

internal static class ProjectionSupport
{
    public static List<string> Normalize(IEnumerable<string> values) =>
    [
        .. values
            .Select(static v => v.Trim().ToLowerInvariant())
            .Where(static v => v.Length > 0)
            .Distinct(StringComparer.Ordinal)
            .Order(StringComparer.Ordinal),
    ];

    /// <summary>
    /// The nights of an event that can be indexed. Prices are EUR in v1; a night in another currency or with a
    /// negative figure cannot be compared with the rest, so it is left out instead of guessed at.
    /// </summary>
    public static IEnumerable<NightChange> Changes(IEnumerable<DateAvailability> nights)
    {
        foreach (DateAvailability night in nights)
        {
            if (night.Rate.Currency == "EUR" && night.Rate.Amount >= 0 && night.Available >= 0)
            {
                yield return new NightChange(night.Date.DayNumber, night.Available, (long)decimal.Round(night.Rate.Amount * 100m));
            }
        }
    }
}
