using System.Collections.Frozen;
using System.Text;

namespace Viatica.Search.Application;

/// <summary>
/// Lower-cases text and strips accents, so "Zürich", "ZURICH" and "zurich" are one word. OpenSearch does the same
/// to indexed text with its own analyzer; this is for the lists the service holds itself (airports), where nobody
/// else will do it. A table rather than Unicode normalisation: the service runs with invariant globalization, where
/// normalisation of anything beyond ASCII is not something to rely on.
/// </summary>
public static class TextFolding
{
    private static readonly FrozenDictionary<char, string> Map = BuildMap();

    public static string Fold(string text)
    {
        ArgumentNullException.ThrowIfNull(text);

        var builder = new StringBuilder(text.Length);
        foreach (char c in text)
        {
            char lower = char.ToLowerInvariant(c);
            if (Map.TryGetValue(lower, out string? folded))
            {
                builder.Append(folded);
            }
            else if (lower is >= '̀' and <= 'ͯ')
            {
                // A combining accent that arrived as its own character: drop it, keep the letter before it.
            }
            else
            {
                builder.Append(lower);
            }
        }

        return builder.ToString();
    }

    private static FrozenDictionary<char, string> BuildMap()
    {
        Dictionary<char, string> map = [];
        void Add(string letters, string plain)
        {
            foreach (char c in letters)
            {
                map[c] = plain;
            }
        }

        Add("àáâãäåāăą", "a");
        Add("çćĉċč", "c");
        Add("ďđ", "d");
        Add("èéêëēĕėęě", "e");
        Add("ĝğġģ", "g");
        Add("ĥħ", "h");
        Add("ìíîïĩīĭįıİ", "i");
        Add("ĵ", "j");
        Add("ķ", "k");
        Add("ĺļľŀł", "l");
        Add("ñńņňŉ", "n");
        Add("òóôõöøōŏő", "o");
        Add("ŕŗř", "r");
        Add("śŝşšș", "s");
        Add("ţťŧț", "t");
        Add("ùúûüũūŭůűų", "u");
        Add("ŵ", "w");
        Add("ýÿŷ", "y");
        Add("źżž", "z");
        map['ß'] = "ss";
        map['æ'] = "ae";
        map['œ'] = "oe";
        map['þ'] = "th";
        return map.ToFrozenDictionary();
    }
}
