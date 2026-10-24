# 0005: One nested document per property, availability as ranges

Status: accepted, 2026-10-25

## Context

Search has one query that is hard. "Which properties have a room type with at least `rooms` units left on every night
of the stay, and what does the cheapest such room cost for those nights", for stays of up to 30 nights, sorted by
that price, with filters, facets and keyset paging, over a year of nights per room type. Cars ask the same of a
category and its rental days.

Two facts about the events shape the answer (`docs/design.md`, ADR 0002).

- `StayListingUpdated` is a whole snapshot with a `ListingVersion`. `StayAvailabilityChanged` is a **delta**: only the
  nights that changed, with one `AvailabilityVersion` per room type.
- Delivery is at least once and not ordered across partitions or replays, so the projection must give the same result
  whatever order, and however many times, it sees the events.

## Decision

**A property is one OpenSearch document; its room types are nested documents inside it.** A rental location is one
document with its categories nested the same way. A result is a property, so the unit of paging, of sorting and of
facet counts is a document, and the cheapest room type is a sort over the nested ones.

**A room type keeps its nights in a ledger and the searchable fields are derived from it on every write.** The ledger
is the units left, the rate and the version of the event that last wrote each night. From it:

- `open.N` for N = 1 to 9, an `integer_range` field holding the maximal runs of consecutive days with at least N
  units left. A stay of nights `[in, out)` for R rooms is possible exactly when one run of `open.R` contains
  `[in, out - 1]`. That is a single `range` query with `relation: contains`, answered from the index without reading
  any night. A night nobody has reported belongs to no run, so a gap excludes the stay.
- `rates`, a sorted multi-valued `long` holding `day << 32 | cents` for each reported night. The price of a stay is a
  short Painless function: binary search for the first night, then add up the next `n` values. It runs only for
  nested documents that already passed the availability filter. The same function sorts (nested script sort,
  `mode min`, restricted by the availability filter so only qualifying rooms count), filters by price range, and
  prices the cheapest room in `inner_hits`.

**Idempotency comes from versions, per night.** An availability event may change a night only if its version is
higher than the version stored on that night. A listing or unpublish event applies only if its version is higher than
the document's. There is no inbox. Applying the same events in any order, and any number of times, gives the same
ledger (`NightLedgerTests.Any_order_of_the_same_events_gives_the_same_ledger`, 200 random orders with repeats;
`ProjectionTests.Availability_delivered_out_of_order_ends_where_in_order_delivery_ends`, every permutation).

**Writes are read-merge-write with a conditional index.** A batch of events is grouped by property, the properties are
read in one `_mget`, the events are applied in memory in arrival order, and the changed documents are written in one
`_bulk`, each with `if_seq_no` and `if_primary_term`. A document someone else changed in between is read again and the
batch's events for it are applied again on top. All writes go through the aliases `stays` and `cars`.

**Rebuild** creates new indices, replays both topics into them, switches the aliases atomically, and replays once
more from where it stopped, relying on the same versioning to make the overlap harmless (`docs/services/search.md`).

## Measured

`docs/benchmark-results/search-query.md`, from `benchmarks/Viatica.Search.Benchmarks`: 10,000 properties x 3 room types
x 365 nights (10.95 million room-nights) and 1,000 rental locations x 8 categories x 365 days, on an Intel Core Ultra 7
255H laptop (16 logical cores, 16 GB visible to Docker, Windows 11), OpenSearch 3.9.0 in a container with the 1 GB heap
that compose gives it, one shard. The load generator and the node share the machine, and other containers were
running; read the figures as a laptop's, not a server's.

- The index is 394 MB for the stays, about 39 KB per property, most of it the ledger stored in `_source`. Cars: 71 MB.
- One writer projects the 40,000 events in 26 s, 1,550 events/s. The requirements ask for 200 inventory changes per
  second; the write path has that much headroom. Projection **lag** under that load is a system test (M8) and is not
  claimed here.
- A single client sees p99 between 10 and 65 ms for every query shape, including the widest one (every property, 3
  nights, 9,386 matches, sorted by price: p50 17 ms, p99 31 ms), the longest stay (30 nights: p99 18 ms) and page two
  by `search_after` (p99 45 ms).
- Sixteen clients at once push throughput to 200 to 650 requests per second for stays, and p99 to 51 to 151 ms. The
  generator shares the cores with the node.
- The slowest shape is the one with a price range (p50 51 ms, where the shapes without one are 10 to 21 ms). The range
  is a script evaluated per candidate room. It has not been profiled further; the other shapes are well inside the
  150 ms target of the requirements, and a faster version is a candidate for the load test in M8.

## Consequences

- Every availability event rewrites its whole property document, three room types of a year each. That is what the
  index size and the 1,550 events/s above include. At 100,000 properties the index would be about 4 GB; the
  requirements' load target is for that size and will be measured in M8.
- The ledger duplicates what the derived fields say, so the document is larger than the searchable fields need. In
  exchange the derived fields can never disagree with the data they come from, and a mapping change is a rebuild.
- `rooms` is limited to 9, one range field per threshold. A search for more is `RoomsOutOfRange`.
- Nights older than yesterday are trimmed and nights beyond 800 days ahead are ignored, which bounds a document
  whatever a producer sends.
- A room type that a later listing stops naming stays in the document, unlisted, with its nights, so a listing that
  names it again finds them. It is never offered meanwhile.
- Prices are EUR. A night in another currency is left out.
- Scripts are Painless with a constant source and parameters, compiled once. A change to the function is a new
  source and one compilation per node.

## Alternatives considered

None of these was built; the reasons are arithmetic and OpenSearch's documented behaviour, not measurements.

- **One document per room type and night**, using OpenSearch's external versioning on each (`version_type=external`
  with the event's version). It makes each night's version check free and needs no read-merge-write. But a search then
  has to group 3 x 365 x 10,000 documents by room type and count nights, at 11 million documents for this corpus and
  more per search than it has results; paging by price over that needs a bucket sort over every property.
- **One document per room type with a field per night** (`n.20261201`). The mapping grows by a field per calendar day
  and passes the default limit of 1,000 fields within three years of operation; a search names up to 30 fields; each
  night would still need a version field of its own.
- **One flat document per room type with the property's fields copied in, collapsed by property.** Facet counts would
  count room types, so exact counts per property need a `cardinality` aggregation, which is approximate above its
  precision threshold, and collapsing combined with `search_after` and a nested script sort is a combination not tried. The nested layout counts properties
  exactly with an ordinary `terms` aggregation (`StaySearchTests.Facets_count_the_properties_that_matched...`).
- **Precomputing the total for every start night and length.** 365 x 30 numbers per room type, rewritten on every rate
  change, to save one binary search and at most 29 additions at query time.
- **A version per room type, as the event's field suggests.** The delta carries only the changed nights, so dropping
  an event older than the room type's latest would lose nights that only it carried. Per night is exact.
- **OpenSearch external versioning on the property document.** One version cannot say "this event is newer for night 3
  and older for night 5"; the conditional write on `_seq_no` guards the document and the ledger guards the nights.
