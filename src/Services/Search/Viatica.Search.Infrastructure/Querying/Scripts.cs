using System.Text.Json.Nodes;

namespace Viatica.Search.Infrastructure.Querying;

/// <summary>
/// The one piece of Painless the service runs: adding up a stay's rates. A nested document keeps one packed number
/// per night (<c>day &lt;&lt; 32 | cents</c>) in a sorted multi-valued field, so the first night is found by binary
/// search and the next <c>nights - 1</c> values are the rest of the stay.
/// </summary>
/// <remarks>
/// The availability filter has already established that every night exists, so the checks for a missing or
/// out-of-sequence night never fire for a document that passed it. They are there so that a document which breaks
/// that promise sorts last instead of failing the whole search with a script exception.
/// </remarks>
internal static class Scripts
{
    private const string Function = """
        long nightsCents(def rates, int first, int nights) {
          int size = rates.size();
          long key = ((long) first) << 32;
          int lo = 0;
          int hi = size;
          while (lo < hi) {
            int mid = (lo + hi) >>> 1;
            if (rates.get(mid) < key) { lo = mid + 1; } else { hi = mid; }
          }
          long sum = 0;
          for (int i = 0; i < nights; i++) {
            if (lo + i >= size) { return Long.MAX_VALUE; }
            long packed = rates.get(lo + i);
            if ((packed >>> 32) != (long) (first + i)) { return Long.MAX_VALUE; }
            sum += packed & 0xFFFFFFFFL;
          }
          return sum;
        }
        """;

    /// <summary>The sum of the stay's rates for one nested document.</summary>
    public static JsonObject Sum(string ratesField, int firstDay, int nights) => new()
    {
        ["lang"] = "painless",
        ["source"] = $"{Function}\nreturn nightsCents(doc['{ratesField}'], params.first, params.nights);",
        ["params"] = new JsonObject { ["first"] = firstDay, ["nights"] = nights },
    };

    /// <summary>True when the sum lies within <c>[min, max]</c> cents, both for the whole stay.</summary>
    public static JsonObject SumWithin(string ratesField, int firstDay, int nights, long min, long max) => new()
    {
        ["lang"] = "painless",
        ["source"] = $"{Function}\nlong total = nightsCents(doc['{ratesField}'], params.first, params.nights); return total >= params.min && total <= params.max;",
        ["params"] = new JsonObject { ["first"] = firstDay, ["nights"] = nights, ["min"] = min, ["max"] = max },
    };
}
