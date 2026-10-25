# Changelog

All notable changes to this project are documented here. The format follows
[Keep a Changelog](https://keepachangelog.com/en/1.1.0/), and the project uses
[semantic versioning](https://semver.org/spec/v2.0.0.html).

## [Unreleased]

### Fixed

- Outbox: events of one key could reach Kafka out of order when two transactions overlapped. Writers now take a
  transaction scoped advisory lock per key before inserting (ADR 0007, #42).
- `viatica.outbox.age` is kept per relay and tagged with the context's full name and the topic, so two relays of one
  context no longer overwrite each other and one stopping no longer removes the other's gauge (#53).
- Repeatable requests: a running request holds its claim on a 30 second lease that it renews until it finishes, and
  every claim has an owner so a stale completion or release cannot touch a newer claim. A crashed request frees its
  id after one lease instead of after the window. `IRepeatabilityStore` gains `RenewAsync` and an owner overload of
  `ReleaseAsync` as default interface methods; `RepeatableResponse` gains an optional `Owner` (#45).
- A repeatable request from a caller without a subject is refused with `401`; anonymous callers used to share one
  namespace of ids and could be served each other's stored responses (#51).
- `AddViaticaAuth` refuses an http `Viatica:Auth:Authority` outside Development at start-up unless
  `Viatica:Auth:AllowInsecureMetadata` is true, which `compose.yaml` sets; `RequireHttpsMetadata` no longer depends on
  the authority's scheme (#46).

### Added

- `compose.yaml` with the local infrastructure: PostgreSQL 18 with one database and one least-privilege role per
  service, Kafka 4.4 in KRaft mode with the five service topics and their dead letter twins, OpenSearch 3.9 with
  a 1 GB heap, Valkey 8.1, Keycloak 26.8 with the `viatica` realm, and the Aspire dashboard as the OTLP sink.
  Every long-running container but the distroless Aspire dashboard has a health check, so
  `docker compose up -d --wait` returns once the stack is usable.
- `deploy/check-stack.sh`, run by a new `compose` CI job, which signs in to each database as its service, checks
  the topics, and decodes real Keycloak tokens for their audiences and the `partnerId` claim.
- `docs/operations.md` with the local stack, how to reset it, and its known limitations.
- `CONTRIBUTING.md`, `SECURITY.md`, and Dependabot for NuGet, GitHub Actions, Docker and npm.
- Flights service (`src/Services/Flights`, port 8103): flight search that asks four airlines at once under a 2.5 s
  deadline and names the ones it left out, a circuit breaker per airline, offers cached in Valkey and paged with the
  Kernel's keyset paging, re-price that shows a changed price instead of applying it, and orders, ticketing, void
  and cancel driven by the saga's events. A timed out order creation is looked up by idempotency key before it is
  accepted or rejected. Passenger data is sealed on the broker and at rest and never logged
  (`docs/services/flights.md`, `http/flights.http`).
- `Viatica.Kernel.Pii`: AES-256-GCM sealing with associated data, and the passenger list format Booking and
  Flights share. `Viatica.Testing` gains a Valkey fixture.
- `airline-sim` and its migrate step in `compose.yaml` (port 8201), and `flights` with its own.
- `FlightRejectionReason.InvalidPassengers` in `Viatica.Contracts`.
- Stays service (port 8101): partner-owned properties and room types with `ETag`/`If-Match`, a bulk calendar edit
  over up to a year in one transaction, signed 15 minute quote tokens, and holds that reserve every night of a stay
  or none in one SQL statement per hold. Consumes Booking's hold, commit, release and cancel requests, publishes
  listing snapshots and availability versions, and expires unclaimed holds with a hosted sweeper. Proved by a race
  of 200 parallel holds for the last 10 rooms. See `docs/services/stays.md`.
- Cars service (`src/Services/Cars`, port 8102): rental locations and car categories with ETags and client chosen ids,
  daily rates and allotments set over a range in one transaction, quotes with a signed 15 minute token (opening
  hours, one-way fee, young driver surcharge), and holds that take one car on every rental day in a single conditional
  `UPDATE`. Commit, release and cancel are idempotent, and a sweeper expires holds after 15 minutes
  (`docs/services/cars.md`, `http/cars.http`).
- `cars` and `cars-migrate` in `compose.yaml`, and a Cars probe in `deploy/check-stack.sh`.
- Search service (`src/Services/Search`, port 8104): stays search by destination text or map box, with availability
  on every night of the stay, filters, three sorts, facet counts and keyset paging; car search by location or radius
  with the one way rule; autocomplete over cities, airports and properties. A read model of `stays.v1` and
  `cars.v1` that drops stale and repeated events by version, reports its lag as a metric, and rebuilds from the
  topics behind an alias swap with no failed query (`docs/services/search.md`, ADR 0005,
  `docs/benchmark-results/search-query.md`, `http/search.http`). Also adds `search` and `search-migrate` to compose,
  `Viatica.Testing.OpenSearchFixture`, `Paging.BindPageRequest` and `IntegrationEventTypes.Read` in the Kernel, and
  `action.auto_create_index` on the OpenSearch node so a write to a missing alias fails instead of creating an index.
