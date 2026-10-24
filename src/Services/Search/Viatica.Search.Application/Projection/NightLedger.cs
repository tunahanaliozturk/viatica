using System.Text.Json.Serialization;

namespace Viatica.Search.Application.Projection;

/// <summary>An inclusive range of day numbers (<see cref="DateOnly.DayNumber"/>), the shape of an OpenSearch <c>integer_range</c>.</summary>
public readonly record struct DayRange(
    [property: JsonPropertyName("gte")] int From,
    [property: JsonPropertyName("lte")] int To);

/// <summary>One night (or rental day) as an availability event reports it, already reduced to whole cents.</summary>
public readonly record struct NightChange(int Day, int Available, long RateCents);

/// <summary>
/// What one room type or car category looks like, night by night: units left, rate, and the version of the event
/// that last wrote that night. It is the part of a document that only grows by merging, never by replacing.
/// </summary>
/// <remarks>
/// <para>
/// Availability events are deltas (the nights that changed) with one version per event. Keeping one version per
/// night is what makes a late or repeated event harmless: an event may change a night only if its version is higher
/// than the one stored there. An event that arrives after a newer one still lands on the nights the newer one did
/// not touch, and loses on the nights it did. Applying the same events in any order, or twice, gives the same
/// ledger (<c>NightLedgerTests</c>).
/// </para>
/// <para>
/// A night nobody has reported has version 0 and counts as not for sale: the search never offers a night it has no
/// price for.
/// </para>
/// </remarks>
public sealed class NightLedger
{
    /// <summary>How far ahead a night is kept, in days from today. Bounds the document whatever a producer sends.</summary>
    public const int HorizonDays = 800;

    /// <summary>The most rooms a search asks for, so the most "at least N left" thresholds a document is indexed for.</summary>
    public const int MaxThreshold = 9;

    /// <summary>Day number of index 0 of the arrays.</summary>
    public int From { get; init; }

    public int[] Available { get; init; } = [];

    public long[] RateCents { get; init; } = [];

    public long[] Version { get; init; } = [];

    [JsonIgnore]
    public int Length => Available.Length;

    /// <summary>
    /// Merges an event's nights. Nights outside <c>[firstDay, firstDay + horizon]</c> are dropped, as are nights
    /// whose stored version is not lower than <paramref name="version"/>. <paramref name="applied"/> is how many
    /// nights changed.
    /// </summary>
    public NightLedger Apply(long version, IEnumerable<NightChange> changes, int firstDay, out int applied)
    {
        ArgumentNullException.ThrowIfNull(changes);

        int lastDay = firstDay + HorizonDays;
        var kept = new List<NightChange>();
        foreach (NightChange change in changes)
        {
            if (change.Day >= firstDay && change.Day <= lastDay)
            {
                kept.Add(change);
            }
        }

        applied = 0;
        if (kept.Count == 0)
        {
            return Trim(firstDay);
        }

        int low = kept.Min(static c => c.Day);
        int high = kept.Max(static c => c.Day);
        if (Length > 0)
        {
            low = Math.Min(low, Math.Max(From, firstDay));
            high = Math.Max(high, From + Length - 1);
        }

        int size = high - low + 1;
        int[] available = new int[size];
        long[] rate = new long[size];
        long[] versions = new long[size];

        // Copy what is still in range, then lay the event over it.
        for (int i = 0; i < Length; i++)
        {
            int target = From + i - low;
            if (target >= 0 && target < size && From + i >= firstDay)
            {
                available[target] = Available[i];
                rate[target] = RateCents[i];
                versions[target] = Version[i];
            }
        }

        foreach (NightChange change in kept)
        {
            int slot = change.Day - low;
            if (version <= versions[slot])
            {
                continue;
            }

            available[slot] = change.Available;
            rate[slot] = change.RateCents;
            versions[slot] = version;
            applied++;
        }

        return new NightLedger { From = low, Available = available, RateCents = rate, Version = versions };
    }

    /// <summary>Drops the nights before <paramref name="firstDay"/>, which nobody can book any more.</summary>
    public NightLedger Trim(int firstDay)
    {
        int drop = Math.Clamp(firstDay - From, 0, Length);
        return drop == 0
            ? this
            : new NightLedger
            {
                From = From + drop,
                Available = Available[drop..],
                RateCents = RateCents[drop..],
                Version = Version[drop..],
            };
    }

    /// <summary>
    /// The maximal runs of consecutive nights with at least <paramref name="atLeast"/> units left. A stay of nights
    /// <c>[in, out)</c> is bookable exactly when one run contains <c>[in, out - 1]</c>.
    /// </summary>
    public IReadOnlyList<DayRange> Runs(int atLeast)
    {
        var runs = new List<DayRange>();
        int start = -1;
        for (int i = 0; i < Length; i++)
        {
            bool open = Version[i] > 0 && Available[i] >= atLeast;
            if (open && start < 0)
            {
                start = From + i;
            }
            else if (!open && start >= 0)
            {
                runs.Add(new DayRange(start, From + i - 1));
                start = -1;
            }
        }

        if (start >= 0)
        {
            runs.Add(new DayRange(start, From + Length - 1));
        }

        return runs;
    }

    /// <summary>
    /// Every reported night as <c>day &lt;&lt; 32 | cents</c>, ascending. One multi-valued number per night is what
    /// lets a script find a stay's first night by binary search and add up the next <c>n</c> values.
    /// </summary>
    public long[] PackedRates()
    {
        var packed = new List<long>(Length);
        for (int i = 0; i < Length; i++)
        {
            if (Version[i] > 0)
            {
                packed.Add(((long)(From + i) << 32) | (uint)Math.Clamp(RateCents[i], 0, uint.MaxValue));
            }
        }

        return [.. packed];
    }
}
