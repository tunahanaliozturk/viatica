using System.Globalization;
using System.Text;

namespace Viatica.Search.Infrastructure.Indexing;

/// <summary>The two indices the service owns, each reached through an alias of the same name.</summary>
public enum IndexKind
{
    Stays,
    Cars,
}

/// <summary>
/// Settings and mappings. Mappings are strict: a field the code does not know about is an error at write time, not a
/// silently dynamic field that grows the mapping.
/// </summary>
/// <remarks>
/// <para>
/// Each room type or car category is a nested document of its property or location. Its availability is two
/// derived fields. <c>open.N</c> is an <c>integer_range</c> per threshold N: the runs of consecutive days with at
/// least N units left. "Every night of the stay has N left" is then one range query with
/// <c>relation: contains</c>, answered from the index without reading any night. <c>rates</c> is one number per
/// night, <c>day &lt;&lt; 32 | cents</c>, from which a script adds up the stay's price. <c>state</c> is the ledger the
/// two are derived from; it is stored but not indexed. ADR 0005 has the reasoning and the measurements.
/// </para>
/// <para>
/// One shard and no replica: the corpus is a few hundred megabytes at most and the node is a single machine.
/// </para>
/// </remarks>
public static class IndexDefinitions
{
    public const string Stays = "stays";
    public const string Cars = "cars";

    public static string AliasOf(IndexKind kind) => kind == IndexKind.Stays ? Stays : Cars;

    public static string Body(IndexKind kind) => kind == IndexKind.Stays ? StaysBody : CarsBody;

    private static string StaysBody { get; } = $$"""
        {
          "settings": {
            "index": { "number_of_shards": 1, "number_of_replicas": 0, "refresh_interval": "1s" },
            "analysis": {
              "analyzer": { "folded": { "type": "custom", "tokenizer": "standard", "filter": ["lowercase", "asciifolding"] } },
              "normalizer": { "folded": { "type": "custom", "filter": ["lowercase", "asciifolding"] } }
            }
          },
          "mappings": {
            "dynamic": "strict",
            "properties": {
              "propertyId": { "type": "keyword" },
              "partnerId": { "type": "keyword" },
              "listingVersion": { "type": "long" },
              "published": { "type": "boolean" },
              "name": { "type": "text", "analyzer": "folded" },
              "city": { "type": "text", "analyzer": "folded", "fields": { "key": { "type": "keyword", "normalizer": "folded" } } },
              "region": { "type": "text", "analyzer": "folded" },
              "countryCode": { "type": "keyword" },
              "location": { "type": "geo_point" },
              "starRating": { "type": "byte" },
              "amenities": { "type": "keyword" },
              "updatedAt": { "type": "date" },
              "rooms": {
                "type": "nested",
                "properties": {
                  "id": { "type": "keyword" },
                  "listed": { "type": "boolean" },
                  "name": { "type": "keyword", "index": false },
                  "maxOccupancy": { "type": "byte" },
                  "beds": { "type": "keyword", "index": false },
                  "open": { "properties": { {{OpenFields(9)}} } },
                  "rates": { "type": "long" },
                  "state": { "type": "object", "enabled": false }
                }
              }
            }
          }
        }
        """;

    private static string CarsBody { get; } = $$"""
        {
          "settings": {
            "index": { "number_of_shards": 1, "number_of_replicas": 0, "refresh_interval": "1s" },
            "analysis": {
              "analyzer": { "folded": { "type": "custom", "tokenizer": "standard", "filter": ["lowercase", "asciifolding"] } },
              "normalizer": { "folded": { "type": "custom", "filter": ["lowercase", "asciifolding"] } }
            }
          },
          "mappings": {
            "dynamic": "strict",
            "properties": {
              "locationId": { "type": "keyword" },
              "partnerId": { "type": "keyword" },
              "listingVersion": { "type": "long" },
              "published": { "type": "boolean" },
              "name": { "type": "text", "analyzer": "folded" },
              "city": { "type": "text", "analyzer": "folded", "fields": { "key": { "type": "keyword", "normalizer": "folded" } } },
              "region": { "type": "text", "analyzer": "folded" },
              "countryCode": { "type": "keyword" },
              "airportCode": { "type": "keyword" },
              "location": { "type": "geo_point" },
              "updatedAt": { "type": "date" },
              "categories": {
                "type": "nested",
                "properties": {
                  "id": { "type": "keyword" },
                  "listed": { "type": "boolean" },
                  "acriss": { "type": "keyword" },
                  "name": { "type": "keyword", "index": false },
                  "seats": { "type": "byte" },
                  "transmission": { "type": "keyword" },
                  "fuel": { "type": "keyword", "index": false },
                  "bags": { "type": "byte" },
                  "minimumDriverAge": { "type": "byte" },
                  "imageUrl": { "type": "keyword", "index": false },
                  "open": { "properties": { {{OpenFields(1)}} } },
                  "rates": { "type": "long" },
                  "state": { "type": "object", "enabled": false }
                }
              }
            }
          }
        }
        """;

    private static string OpenFields(int thresholds)
    {
        var builder = new StringBuilder();
        for (int n = 1; n <= thresholds; n++)
        {
            builder.Append(n == 1 ? "" : ", ").Append('"').Append(n.ToString(CultureInfo.InvariantCulture)).Append("\": { \"type\": \"integer_range\" }");
        }

        return builder.ToString();
    }
}
