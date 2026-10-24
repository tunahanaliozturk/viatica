using System.Reflection;
using System.Text.Json;
using System.Text.Json.Serialization;
using Viatica.Search.Application;
using Viatica.Search.Application.Places;
using Viatica.Search.Application.Projection;

namespace Viatica.Search.Infrastructure.Places;

internal sealed record AirportRow(string Code, string Name, string City, string Country, double Lat, double Lon);

[JsonSourceGenerationOptions(JsonSerializerDefaults.Web)]
[JsonSerializable(typeof(AirportRow[]))]
internal sealed partial class AirportJsonContext : JsonSerializerContext;

/// <summary>
/// The airports the platform knows, from <c>airports.json</c> compiled into the assembly. The list is a copy of the
/// forty airports the airline simulator flies between: Search must not reference the simulator, and a copy of the
/// reference data is cheaper than a service call on every keystroke. A traveller can type a code, a city or part
/// of the airport's name, in any case and with or without accents.
/// </summary>
public sealed class AirportDirectory : IAirportDirectory
{
    private readonly Entry[] _entries;

    public AirportDirectory()
    {
        using Stream stream = typeof(AirportDirectory).Assembly.GetManifestResourceStream("airports.json")
            ?? throw new InvalidOperationException("airports.json is not embedded in the assembly.");
        AirportRow[] rows = JsonSerializer.Deserialize(stream, AirportJsonContext.Default.AirportRowArray)
            ?? throw new InvalidOperationException("airports.json is empty.");

        _entries =
        [
            .. rows.Select(static row => new Entry(
                new Place(PlaceKind.Airport, row.Name, $"{row.City}, {row.Country}", row.Code, null, new GeoLocation(row.Lat, row.Lon)),
                TextFolding.Fold(row.Code),
                TextFolding.Fold(row.City),
                TextFolding.Fold($"{row.Code} {row.City} {row.Name}").Split(' ', StringSplitOptions.RemoveEmptyEntries))),
        ];
    }

    public int Count => _entries.Length;

    public IReadOnlyList<Place> Find(string text, int limit)
    {
        ArgumentNullException.ThrowIfNull(text);

        string folded = TextFolding.Fold(text.Trim());
        string[] tokens = folded.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        if (tokens.Length == 0)
        {
            return [];
        }

        // Every word typed must begin some word of the airport's code, city or name; the best matches are the code
        // typed in full, then a code that starts with it, then a city that does, then anything else.
        return
        [
            .. _entries
                .Where(entry => tokens.All(token => entry.Words.Any(word => word.StartsWith(token, StringComparison.Ordinal))))
                .Select(entry => (Entry: entry, Rank: Rank(entry, folded)))
                .OrderBy(static pair => pair.Rank)
                .ThenBy(static pair => pair.Entry.Place.Name, StringComparer.Ordinal)
                .Take(limit)
                .Select(static pair => pair.Entry.Place),
        ];
    }

    private static int Rank(Entry entry, string folded) =>
        entry.Code == folded ? 0
        : entry.Code.StartsWith(folded, StringComparison.Ordinal) ? 1
        : entry.City.StartsWith(folded, StringComparison.Ordinal) ? 2
        : 3;

    private sealed record Entry(Place Place, string Code, string City, string[] Words);
}
