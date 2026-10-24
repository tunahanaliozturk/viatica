# Search

Search answers "what can I book for these dates" for stays and cars. It owns no data. Everything in its OpenSearch
indices is a copy of what Stays and Cars published on Kafka, shaped for questions, and can be thrown away and built
again from the topics. It publishes nothing.

| | |
|---|---|
| Port | 8104 (`search` in compose, 8080 inside the container) |
| Store | OpenSearch 3.x, two indices behind the aliases `stays` and `cars` |
| Consumes | `stays.v1`, `cars.v1`, as consumer group `search`: `StayListingUpdated`, `StayUnpublished`, `StayAvailabilityChanged`, `CarLocationUpdated`, `CarLocationUnpublished`, `CarAvailabilityChanged`. Every other type on those topics is skipped. |
| Publishes | nothing |
| Auth | anonymous. `AddViaticaAuth("search-api")` is wired, so a token is validated when sent, but no endpoint requires one. |
| Code | `src/Services/Search/Viatica.Search.{Application,Infrastructure,Api}`. No Domain project: a read model has no invariants of its own to protect. |
| Tests | `tests/Services/Search/Viatica.Search.UnitTests`, `...IntegrationTests` (real OpenSearch and Kafka) |

Requirements covered: SRC-1 to SRC-5, issues #17, #18 and #19.

## Endpoints

All take `api-version=2026-11-01`, answer errors in the shared envelope (`docs/api/errors.md#search`), and refuse
parameters they do not define. Examples in order: `http/search.http`.

### `GET /stays/search`

| Parameter | Meaning |
|---|---|
| `destination` | Free text over property name, city and region. Case and accents are ignored, a typo or two is forgiven (`lisbonn`), every word must match. |
| `bbox` | `west,south,east,north`. West greater than east crosses the antimeridian. Either `destination` or `bbox` is required; both together narrow each other. |
| `lat`, `lon` | The point "near" is measured from. Defaults to the centre of `bbox`. |
| `checkIn`, `checkOut` | Required, `yyyy-MM-dd`. At most 30 nights. |
| `guests` (2), `rooms` (1) | Guests are spread over the rooms: 5 guests in 2 rooms needs rooms for 3. |
| `minPrice`, `maxPrice` | EUR per room and night, on the average nightly rate of the stay. |
| `stars` | Comma separated, any of 1 to 5. |
| `amenities` | Comma separated; the property must have all of them. |
| `sort` | `price` (default), `rating` (stars, then price) or `distance` (needs a point). |
| `$top`, `$skiptoken` | Page size (20, at most 100) and keyset position. |

A property is returned only when one of its room types has at least `rooms` units left on **every** night of the
stay (SRC-3), sleeps the guests, and passes the price filter. The item carries the property summary, the cheapest
room type that qualifies, the average price per night and the total for all rooms:

```json
{
  "value": [{
    "propertyId": "0192f0c4-...", "name": "Hotel Lisboa", "starRating": 4, "city": "Lisbon", "countryCode": "PT",
    "location": { "lat": 38.72, "lon": -9.14 }, "amenities": ["pool", "wifi"], "distanceKm": 3.2,
    "room": { "roomTypeId": "0192f0c4-...", "name": "Standard", "maxOccupancy": 2, "beds": "1 double" },
    "nights": 3, "rooms": 1,
    "pricePerNight": { "amount": "90.00", "currency": "EUR" },
    "total": { "amount": "270.00", "currency": "EUR" }
  }],
  "nextLink": "https://.../stays/search?...&$skiptoken=AQI...",
  "totalCount": 57,
  "facets": { "stars": [{ "value": 4, "count": 12 }], "amenities": [{ "value": "pool", "count": 30 }] }
}
```

The price is indicative. The binding price is the quote from Stays, which can differ (a rate edited a second ago, a
night that sold out since the last refresh).

`totalCount` and `facets` come with the first page only. Facets count the properties that matched everything,
availability and every filter included, so they never promise a result the list would not show.

### `GET /cars/search`

| Parameter | Meaning |
|---|---|
| `pickUpLocationId` or `lat` + `lon` | A location, or a point with `radiusKm` (default 25, at most 200). One of the two, not both. |
| `dropOffLocationId` | Where the car goes back; defaults to the pick-up. A different location is a one way rental. |
| `pickUpAt`, `dropOffAt` | Required, RFC 3339 with an offset. A rental day is every started 24 hours, counted from the calendar day of `pickUpAt` as written. At most 30 days. |
| `category` | An ACRISS code or its first letters: `E` is every economy car. |
| `transmission`, `minSeats`, `minPrice`, `maxPrice` | Filters; prices are EUR per day. |
| `$top`, `$skiptoken` | As above. |

The result is a list of pick-up locations, cheapest first by their cheapest qualifying car, each with the
categories that are free on every rental day (`offers`, cheapest first, with price per day and total). The unit of a
result is the location, not the individual car type, because paging by price and showing a location's whole
selection together are both easier from one document per location (ADR 0005).

**One way rule.** With both locations named and different partners, the answer is `400 OneWayNotAllowed`. With a
point and a `dropOffLocationId`, only locations of the drop-off's partner are listed, and `oneWay` is true on each
one that is not the drop-off itself. Opening hours are not checked here; Cars rejects a closed pick-up when it quotes.

### `GET /autocomplete`

`q` (2 to 60 characters), `types` (any of `city`, `airport`, `property`; all by default), `$top` (8, at most 20).
Cities come from published stays and car locations, deduplicated; properties from published stays; airports from
`airports.json` in the service (IATA code, name, city, country, position), which holds the 40 airports the airline
simulator flies between, because Search must not reference the simulator. Order: an airport whose code is exactly
what was typed, cities, other airports, properties. There is no `nextLink`; it is a short list for a type-ahead box.

### Health

`/health/live` always answers. `/health/ready` answers when OpenSearch does and both aliases exist, so a stack where
`migrate` has not run is not ready.

## How the index is built

One OpenSearch document per property, with its room types nested inside; one per rental location, categories
nested. The reasoning and the measurements are ADR 0005 and `docs/benchmark-results/search-query.md`. In short:

- A room type's nights are stored as a ledger (units, rate and the version of the event that wrote each night) and
  from it two searchable fields are derived on every write: `open.N`, an `integer_range` of the runs of nights with
  at least N units, and `rates`, one packed number per night. "Free on every night" is one range query with
  `relation: contains`; the price of the stay is a small script that adds up `rates`.
- Listing events are whole snapshots; the highest `ListingVersion` wins. Availability events are deltas; each night
  keeps the version of the last event that wrote it, so an event may change a night only if it is newer than what the
  night holds. A late event still lands on the nights nothing newer touched.

### Idempotency without an inbox

The other consumers record each event id in an inbox table in the same transaction as their state change. Search has
no relational store, and needs none: applying an event is a pure function of the document and the event that
ignores anything not newer than the document (`NightLedgerTests`, `ProjectionTests`). So a redelivered batch, a
replay, and events that arrive out of order change nothing they should not. Offsets are stored after a batch is
written; a crash in between replays the batch.

Writes are batched per Kafka poll (up to 500 events): one `_mget` for the properties touched, the events applied in
order in memory, one `_bulk`. Each write is conditional on the document's `_seq_no`; if another writer got there
first the document is read again and the batch's events for it are replayed on top (`ProjectionWriterTests`).

An event that cannot be applied goes to `<topic>.dlq` with the usual headers. An index that is down is waited out
with backoff and never dead-letters anything: parking good messages because OpenSearch restarted would lose them.

### Projection lag

`viatica.search.projection.lag` (histogram, seconds, tagged by event type): the time from an event's `occurredAt`
to the acknowledged write that carries it into the index. A document is searchable one refresh later (`refresh_interval`
1 s). Dropped events are measured too; the projection is as current as its last handled event. Next to it,
`viatica.search.events` counts events by outcome (`applied`, `dropped`, `ignored`, `dead_lettered`, `replayed`).
`ProjectionTests.Projection_lag_is_the_time_from_the_events_occurred_at_to_its_index_write` pins the definition. The
runbook is in `docs/operations.md`.

## Commands

The Api host takes one argument, like `migrate` in the other services:

| Command | What it does |
|---|---|
| `migrate` | Creates the `stays` and `cars` aliases with an index behind each, if they are missing. Safe to repeat. Compose runs it as `search-migrate` before the service starts. |
| `rebuild` | Builds both indices again from the beginning of the topics and switches the aliases over. See below. |
| `healthcheck` | Asks the running server for `/health/ready` and exits 0 or 1. The runtime image has no curl. |

### Rebuild (SRC-5)

`docker compose run --rm search-migrate rebuild`, or `dotnet Viatica.Search.Api.dll rebuild` with the same
configuration. Search keeps answering from the old indices and the live consumers keep projecting throughout.

1. Create new, empty indices that nothing reads.
2. Replay both topics from offset 0 into them with a consumer group of their own (`search-rebuild-<time>`), up to
   the end as it was when the replay began, then again for what arrived meanwhile until a pass reads under 1,000
   messages.
3. Switch both aliases in one atomic `_aliases` request.
4. Replay once more from where step 2 stopped. Events the live consumer wrote to the old index just before the
   switch are in the topic after that offset, so they reach the new index now. Replays overlap safely because the
   projection ignores what is not newer.
5. Delete the old indices.

If a message that was read cannot be applied, the rebuild stops before the switch and deletes the new indices. A
message that cannot be read at all is skipped with a warning (the live consumer parked it already).
`RebuildTests` runs it against 190 properties with four loops querying all the while: no query fails, a document
deleted from the old index is back afterwards, and an event published during the rebuild is in the new index.

Limits: a rebuild is only as complete as the topics. Kafka's retention (seven days by default) decides how far back
it can read; availability deltas cannot be log compacted because dropping an old delta loses its nights. Two
rebuilds at once are not guarded against; run one.

## Decisions worth arguing with

- **One document per property, rooms nested, versus one document per room type.** Results are properties, facets are
  counts of properties, and the cheapest room is a sort on the nested documents; a flat document per room type needs
  a collapse and approximate distinct counts for the facets. ADR 0005.
- **Per-night versions rather than a version per room type.** The contract says availability events carry a version
  per room type, but their payload is the changed nights only. Dropping an event older than the room type's latest
  would lose the nights only it carried. Version per night is exact, and costs a ledger in `_source`.
- **Plain query parameters instead of `$filter`.** The kernel's grammar compiles to LINQ for EF Core. Search filters
  on a multi-valued field (amenities), takes a bounding box and returns facets, none of which fit it, and OpenSearch
  would need its own translator. Unknown parameters are refused, so nothing is silently ignored, which is the rule the
  grammar exists to keep.
- **No facets after page one.** They would be recomputed identically on every page of a long scroll.
- **Rate limits belong to the gateway.** The endpoints are bounded instead: page size at most 100, stays at most
  30 nights, text at most 100 characters, 15 s request timeout to OpenSearch. Per-IP limiting from the service would
  see only the gateway's address.

## Known limits

- Only EUR. A night priced in another currency is left out, not converted.
- `rooms` is capped at 9, because the index holds one range field per threshold.
- Search is eventually consistent: a quote from Stays or Cars is authoritative and can disagree with a result.
- A room type that a later listing no longer names stays in the document, unlisted, with its nights.
- No opening-hours check for cars, no driver age rules; those are quote-time checks in Cars.
