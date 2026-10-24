Measured 2026-10-24 on Microsoft Windows 10.0.26100, 16 logical cores, .NET 11.0.0-rc.2.26504.105, OpenSearch heap 1024 MB. The load generator and OpenSearch share the machine.

## Corpus

- **Stays:** 10,000 properties x 3 room types x 365 nights = 10,950,000 room-nights, from 40,000 events (one listing and one availability event per room type per property).
- Projected through `ProjectionWriter` in batches of 400 events in 26 s (1,554 events/s, 389 properties/s), one writer, no concurrency.
- Index after a force merge to one segment: 394.1 MB on disk, 40,000 Lucene documents (nested rooms included).
- **Cars:** 1,000 locations x 8 categories x 365 days = 2,920,000 category-days, indexed in 5 s; index 71.3 MB on disk, 9,000 Lucene documents (nested rooms included).

## Query latency

1000 requests per row, parameters drawn at random from the seeded corpus (city, start date, filters) with a fixed seed, after a warm-up of 200 requests. Latency is the time from `SearchAsync` being called to its result being parsed: the service's own query code over HTTP to OpenSearch, without the API host in front.

| Query | Matches (median) | 1 client p50 | p95 | p99 | max | 16 clients p50 | p95 | p99 | requests/s |
|---|---:|---:|---:|---:|---:|---:|---:|---:|---:|
| Stays: one city, 3 nights, 2 guests, by price | 95 | 12.2 ms | 24.4 ms | 34.9 ms | 79.5 ms | 31.4 ms | 53.5 ms | 71.7 ms | 481 |
| Stays: one city, 7 nights, 4-5 stars, 1 amenity, price range | 13 | 51.1 ms | 57.2 ms | 64.7 ms | 91.7 ms | 53.4 ms | 67.0 ms | 83.9 ms | 290 |
| Stays: a region (about 1,000 properties), 14 nights, by rating | 109 | 9.5 ms | 14.5 ms | 19.4 ms | 35.4 ms | 22.9 ms | 41.6 ms | 51.0 ms | 647 |
| Stays: every property, 3 nights, by price (the widest query: most rooms qualify) | 9,386 | 17.0 ms | 23.1 ms | 31.3 ms | 44.3 ms | 67.5 ms | 110.1 ms | 127.2 ms | 228 |
| Stays: every property, 30 nights, by price (the longest stay) | 758 | 9.8 ms | 14.0 ms | 18.3 ms | 30.1 ms | 27.4 ms | 49.6 ms | 60.5 ms | 543 |
| Stays: every property, 7 nights, by distance, page 2 (search_after) | 20 | 21.5 ms | 33.7 ms | 45.0 ms | 74.1 ms | 76.3 ms | 126.8 ms | 151.4 ms | 197 |
| Cars: 25 km around a city, 3 days | 10 | 8.8 ms | 15.7 ms | 21.5 ms | 32.9 ms | 20.4 ms | 39.5 ms | 52.7 ms | 709 |
| Cars: 200 km, 10 days, compact automatic | 5 | 5.7 ms | 9.1 ms | 11.0 ms | 20.4 ms | 12.3 ms | 26.1 ms | 33.2 ms | 1151 |
| Autocomplete: cities for the first three letters of a city | 1 | 1.8 ms | 6.0 ms | 9.6 ms | 43.6 ms | 5.2 ms | 20.1 ms | 34.4 ms | 2192 |
| Autocomplete: property names for "hotel" and the start of a city | 8 | 4.6 ms | 7.9 ms | 11.2 ms | 17.7 ms | 12.1 ms | 27.8 ms | 38.6 ms | 1146 |
