# Viatica: requirements

> Working name. *Viatica* is the plural of the Latin *viaticum*: what a traveller was given for the
> road, money and provisions for the whole journey. Renaming is a find and replace until the first
> push.

Status: draft 2, 2026-10-24 (scope widened from hotels only to stays, flights and car rental). This
document is the contract every service and every agent builds against. Changes go through a PR that
edits this file first.

## 1. What it is

A travel search and booking platform built as microservices, in the shape of Expedia or
Booking.com. A traveller searches stays, flights and rental cars, puts any mix of them into one
trip, and pays once. Partners manage their own hotels and rental fleets. Flights come from airlines
the platform does not own, reached through supplier APIs.

The point of the repository is not the screens. It is four hard problems solved and measured:

1. **All or nothing trips.** A trip with a flight, a hotel and a car is either fully confirmed and
   charged once, or nothing is confirmed and nothing is charged. A ticketing failure after the hotel
   was already held must unwind cleanly. Checked by reconciliation across every database after
   injected supplier failures and service kills.
2. **No overselling under contention.** Many travellers racing for the last rooms of a night or the
   last cars of a day: exactly as many bookings succeed as there are units, never one more.
3. **Supplier search with a deadline.** Flight search fans out to several airlines, some slow, some
   down. The traveller gets an answer inside a fixed deadline, with the missing suppliers named,
   instead of waiting for the slowest one.
4. **A search read model that keeps up.** Stays and cars are searched on their own store, fed by
   events. How stale it gets is measured and published, not assumed.

### Two kinds of inventory, on purpose

| | Stays, Cars | Flights |
|---|---|---|
| Who owns the inventory | The platform (partners load it) | Airlines (external suppliers) |
| How search works | Pre indexed in OpenSearch, queried locally | Live fan out to supplier APIs, short lived offer cache |
| How a hold works | Atomic counter update in our own database | Supplier order with a ticketing time limit |
| How it can fail at confirmation | Only if the hold expired | Ticketing can be refused by the airline |

That contrast is deliberate. It is the reason the booking saga is interesting, and an ADR explains
why flights are not indexed (fares change too often and belong to someone else).

### Non-goals

- Real airlines, GDS or NDC connections, real payment providers, real card data, PCI scope. Airlines
  and the payment provider are simulators in the repository that fail, time out and answer twice on
  purpose.
- Currency conversion: every price on the platform is in EUR for v1. Taxes, invoices, loyalty,
  reviews, seat maps, ancillaries (bags, meals), multi city flight itineraries beyond one way and
  return, activities and trains.
- Individual room numbers or licence plates. Stays are counted per room type per night, cars per
  category per location per day, the way channel managers and rental systems do it.
- Multi region deployment, Kubernetes manifests.

## 2. Headline claims (each one backed by a named test or a published measurement)

| Claim | Proved by |
|---|---|
| 10,000 trip checkouts with 20% injected ticketing failures and random supplier timeouts: zero trips charged with an unconfirmed item, zero confirmed items without a captured payment | `TripAtomicityTests` + `ReconciliationTests` (system tests), `load/checkout` |
| 1,000 concurrent booking attempts for 10 remaining rooms produce exactly 10 confirmed stays (same for cars) | `LastUnitRaceTests`, `load/last-room` |
| Flight search answers within its 2.5 s deadline at p99 with one airline down and one slow, and names the missing airlines | `FlightSearchDeadlineTests`, `load/flight-search` |
| Stays search p99 under 150 ms at 500 requests per second over 100,000 properties; projection lag p99 under 2 s at 200 inventory changes per second | `load/stays-search`, `load/projection-lag` |

The numbers are targets. Whatever the measurement says gets published, with the hardware named,
including a miss.

## 3. Actors and roles

| Role | Can |
|---|---|
| Anonymous | Search stays, flights, cars; view details and prices |
| Traveller | Everything above; build trips, check out, cancel; see own trips |
| Partner | Manage own hotels (stay partner) or own rental locations and fleet (car partner); see bookings for own inventory |
| Admin | Read everything, suspend a partner, unpublish any listing |

Identity comes from Keycloak (OIDC, authorization code with PKCE for the web app). Authorization is
deny by default and resource based: a partner token alone is not enough to edit a hotel, the service
checks ownership.

## 4. Services and ownership

| Service | Owns | Store |
|---|---|---|
| **Stays** | Properties, room types, nightly rates, allotments, holds | PostgreSQL |
| **Cars** | Rental locations, car categories, daily rates, allotments, holds | PostgreSQL |
| **Flights** | Supplier adapters, offer cache, flight orders mirrored from airlines | PostgreSQL + Redis |
| **Search** | Query shaped copies of published stays and cars with availability | OpenSearch |
| **Booking** | Trips, trip items, the checkout saga, cancellation policy evaluation | PostgreSQL |
| **Payments** | Payment intents, provider calls, refunds | PostgreSQL |
| **Gateway** | Routing, token validation, rate limiting | none |
| **Web** | Traveller app and partner console (Vue) | none |
| *airline-sim* | Simulated airlines (test double, not part of the product) | in memory + PostgreSQL |
| *psp-sim* | Simulated payment provider (test double) | in memory |

A service never reads another service's database. State changes cross boundaries over Kafka;
queries a user is waiting for go over HTTP through the gateway.

## 5. Functional requirements

### Stays

- **STY-1** Partner creates a property (name, description, address, geo point, star rating,
  amenities, check-in and check-out times, cancellation policy). Starts as `Draft`.
- **STY-2** Partner adds room types (name, max occupancy, beds, amenities, base rate).
- **STY-3** Partner sets nightly rate and allotment per room type for a date range up to 365 days
  ahead, as one bulk request.
- **STY-4** Partner publishes or unpublishes a property. Only published properties reach search.
- **STY-5** Quote: nightly breakdown and total for a room type, dates and quantity, plus a quote
  token valid for 15 minutes that pins the price.
- **STY-6** Hold: atomically reserve the quantity on every night of the stay, or none. Expires after
  15 minutes unless committed. Commit and release are idempotent. A sweeper releases expired holds
  and announces `StayHoldExpired`.

Invariant: for every room type and night, `sold + held <= allotment`, enforced in one SQL statement
(`ExecuteUpdateAsync` with the condition in the `WHERE` clause), never read then write.

### Cars

- **CAR-1** Partner manages rental locations (address, geo point, opening hours) and car categories
  (ACRISS code, seats, transmission, fuel, bags, image).
- **CAR-2** Partner sets daily rate and allotment per category per location for a date range.
- **CAR-3** Search by pick-up location and time, drop-off location and time. One way rentals are
  allowed between locations of the same partner and carry a one way fee.
- **CAR-4** Driver age rules per category (minimum age, young driver surcharge).
- **CAR-5** Quote, hold, commit, release, expiry: same contract as stays, counted per rental day.

### Flights

- **FLT-1** Search one way or return trips by origin, destination, dates, passengers (adults,
  children, infants) and cabin. Search fans out to every configured airline in parallel with a
  2.5 s deadline. Results that arrive late are dropped from this response and the response lists
  them under `incompleteSuppliers`.
- **FLT-2** Results are offers with an `expiresAt`. Offers are cached in Redis for their lifetime
  under a search id, so paging through results does not call the airlines again.
- **FLT-3** Before an offer goes into a trip it is re-priced with the airline. A price change is
  shown to the traveller, never applied silently.
- **FLT-4** Hold: create an order with the airline, which returns a booking reference and a
  ticketing time limit. Ticket on confirmation. Void within the airline's void window, otherwise
  cancel with the fare's refund rules.
- **FLT-5** Passenger details (names as in passport, date of birth) are validated before the order
  is created. They are personal data: stored encrypted, never logged.
- **FLT-6** `airline-sim` runs four airlines with different behaviour: one fast and reliable, one
  slow (p50 1.5 s), one that refuses ticketing on a configurable share of orders, one that times out
  and sometimes creates the order anyway. Fares are generated from a seeded route network so tests
  are repeatable. Seat inventory is counted per fare class.

### Search (stays and cars)

- **SRC-1** Stays: free text destination or map bounding box, dates, guests, rooms. Filters: price
  per night, star rating, amenities. Sort: price, rating, distance. Facet counts for stars and
  amenities.
- **SRC-2** Cars: pick-up and drop-off location (or radius around a point), times, filters by
  category, transmission, seats, price. Sort: price.
- **SRC-3** Only listings available for the whole stay or rental period are returned. The price shown
  is indicative; the binding price is the owning service's quote.
- **SRC-4** Autocomplete across cities, airports, and properties.
- **SRC-5** The projection is rebuildable from scratch by replaying events into a new index swapped
  in by alias, so search stays up during a rebuild.

### Booking (trips)

- **BKG-1** A traveller builds a trip: any combination of stay items, flight items and car items,
  each carrying its quote token or re-priced offer. A trip can be built anonymously and is claimed
  at sign in.
- **BKG-2** Checkout creates the trip with a client generated id (`PUT /trips/{id}/checkout`)
  plus traveller details and a payment method token. Repeating the request changes nothing.
- **BKG-3** The checkout saga (section 6) ends with every item `Confirmed` and the payment captured,
  or every item released or voided and the payment voided. No other end state is allowed.
- **BKG-4** Cancellation per item. Each item's own policy decides its refund (stay policy, car
  policy, fare rules); the trip total and the refund are recalculated and refunded as one payment
  operation.
- **BKG-5** Traveller lists own trips; partners list bookings against their own inventory.
- **BKG-6** Every state change is an event on the `booking` topic.

### Payments

- **PAY-1** Tokenise a test card (Stripe style test numbers decide the outcome). The card number never
  leaves this service and is never stored or logged.
- **PAY-2** Authorise, capture, void, refund (full or partial). Each operation is idempotent on trip
  id plus operation id.
- **PAY-3** A provider timeout is resolved by asking the provider for the operation's outcome, never
  by retrying blindly.

## 6. The checkout saga

Booking is the orchestrator. Saga state lives on the `Trip` aggregate and its items; there is no
separate saga engine.

```
1. Hold every item in parallel
   Stays  <- StayHoldRequested       -> StayHoldPlaced | StayHoldRejected
   Cars   <- CarHoldRequested        -> CarHoldPlaced  | CarHoldRejected
   Flights<- FlightOrderRequested    -> FlightOrderCreated | FlightOrderRejected
   any rejection: release everything already held, trip Failed, nothing charged

2. Authorise the trip total
   Payments <- PaymentAuthorizationRequested -> PaymentAuthorized | PaymentDeclined
   declined: release everything, trip Failed

3. Confirm the items that can fail first
   Flights <- FlightTicketingRequested -> FlightTicketed | FlightTicketingRefused
   refused: void the tickets already issued, release stays and cars, void the payment, trip Failed

4. Confirm the items that cannot fail while their hold is valid
   Stays, Cars <- commit (idempotent)

5. Capture
   Payments <- PaymentCaptureRequested -> PaymentCaptured
   trip Confirmed
```

Why this order: an airline can refuse to ticket; our own hold cannot be refused while it is valid.
Doing the refusable step first means a failure never has to undo a committed stay. The deadline of
the whole saga is the shortest hold or ticketing time limit among the items, minus a safety margin;
a saga that reaches it unwinds. The ADR for this records the alternatives (capture first, ticket
last) and why they lost.

Rules every consumer follows:

- At least once delivery is assumed. Every consumer is idempotent through an inbox table keyed by
  event id, written in the same transaction as the state change.
- Events can arrive out of order. A consumer that sees an event for a state it has already moved past
  ignores it and records why.
- Every outgoing event goes through a transactional outbox in the same transaction as the state
  change. Nothing is published from inside a request handler.
- Partition key is the trip id for booking events and the aggregate id elsewhere, so events for one
  trip are ordered.
- An airline that timed out on order creation may have created the order. The Flights service looks
  the order up by our idempotency key before deciding it failed.

## 7. API conventions (Microsoft REST API Guidelines)

All public endpoints follow the [Microsoft REST API Guidelines](https://github.com/microsoft/api-guidelines).
Shared types live in `Viatica.Kernel.Web` so every service answers the same way.

**Versioning.** Every request carries `?api-version=2026-11-01`. Missing or unknown version: `400`
with code `MissingApiVersionParameter` or `UnsupportedApiVersionValue`.

**Collections and paging.** Server driven paging, keyset based, never offset:

```json
GET /stays/search?destination=lisbon&checkIn=2026-12-01&checkOut=2026-12-04&guests=2&$top=20&api-version=2026-11-01

{
  "value": [ { "id": "...", "name": "...", "fromPrice": { "amount": "312.00", "currency": "EUR" } } ],
  "nextLink": "https://api.viatica.local/stays/search?...&$skiptoken=eyJrIjpb...&api-version=2026-11-01"
}
```

- `$top` sets the page size, default 20, maximum 100. Larger values are clamped, not rejected.
- `$skiptoken` is opaque (base64url of the keyset position plus a hash of the query), so a token
  replayed against a different query is rejected with `400 InvalidSkipToken`.
- `nextLink` is absent on the last page. Clients follow it as is.
- `$orderby` and `$filter` accept a small allowlisted grammar per endpoint (`eq`, `ne`, `gt`, `ge`,
  `lt`, `le`, `and`, `in`). Anything else is `400 InvalidFilter`, never silently ignored.
- Flight search pages over the cached offer set of one search id; the page body also carries
  `searchId`, `expiresAt` and `incompleteSuppliers`.

**Errors.** Every non 2xx response has this body:

```json
{
  "error": {
    "code": "InvalidRequest",
    "message": "The request is invalid.",
    "target": "trip",
    "details": [
      { "code": "CheckOutBeforeCheckIn", "message": "checkOut must be after checkIn.", "target": "items[0].checkOut" }
    ],
    "innererror": { "code": "StayTooLong" }
  }
}
```

`code` values are a closed, documented list per service (`docs/api/errors.md`) and are part of the
contract; messages are for humans and can change.

**Creation and idempotency.** Resources a client creates use `PUT` with a client generated id, so a
retry is naturally idempotent. Operations without a natural id (refund, bulk rate update) accept
`Repeatability-Request-ID` and `Repeatability-First-Sent` headers.

**Long running operations.** Checkout returns `202` with an `Operation-Location` header. `GET` on
that URL returns `{ "id", "status": "NotStarted | Running | Succeeded | Failed | Canceled", "error"?,
"resourceLocation"? }`. The web app also gets a server sent events stream so it does not poll.

**Concurrency.** Mutable resources return an `ETag`. `PATCH` and `PUT` on an existing resource
require `If-Match`; a mismatch is `412 PreconditionFailed`.

**Representation.** camelCase JSON, enums as strings, dates as RFC 3339, instants in UTC with offset,
airports as IATA codes, money as `{ "amount": "312.00", "currency": "EUR" }` with the amount as a
string so no client parses it into a float.

## 8. Application design

### CQRS

- Commands change state through the domain model, loaded and saved with a tracked `DbContext`.
- Queries never touch the domain model. They project straight to response types with
  `AsNoTracking`, compiled queries on hot paths, and keyset paging.
- Search is the extreme end of the same split: a separate store fed only by events.
- Commands and queries are Relevox requests (`IRequest<T>`), dispatched by Relevox, not MediatR.
  Use cases live in feature folders: `Features/PlaceHold/{PlaceHoldCommand, PlaceHoldValidator,
  PlaceHoldHandler, PlaceHoldEndpoint}.cs`.

### Persistence

- EF Core `DbContext` per service, used directly by handlers. No repository layer, no generic
  `IRepository<T>`.
- The Application layer sees the context through a narrow `I<Service>Db` interface exposing the
  `DbSet`s it needs and `SaveChangesAsync`, so Application references EF Core but never a provider.
- Migrations live in Infrastructure and run as a one shot step in compose, not on startup.

### DDD

Used where there are real invariants, skipped where there are none.

- Aggregates with behaviour: `Trip` (Booking), `RoomTypeInventory` (Stays), `CarCategoryInventory`
  (Cars), `FlightOrder` (Flights), `Payment` (Payments).
- Value objects: `Money`, `StayDates`, `RentalPeriod`, `Occupancy`, `PassengerMix`, `IataCode`,
  `QuoteToken`, strongly typed ids.
- Catalogue editing (properties, locations, car categories) is mostly CRUD and stays thin.
- C# 15 union types model outcomes that are not errors (`HoldOutcome = HoldPlaced | SoldOut |
  NotBookable`, `TicketingOutcome = Ticketed | Refused | Unknown`), with exhaustive switches checked
  by the compiler. Closed hierarchies model trip item states if they read better than an enum plus
  a transition table; an ADR picks.

### Validation (OrionGuard)

Decision after reviewing OrionGuard 7.0.0 (MIT, only dependency is
`Microsoft.Extensions.DependencyInjection.Abstractions`):

| Part | Use it? | Why |
|---|---|---|
| `AbstractValidator<T>` + `GuardResult` for commands and queries | Yes | `ValidationError(ParameterName, Message, ErrorCode)` maps one to one onto the Microsoft error `details[]` (`target`, `message`, `code`). Async rules cover checks that need I/O. |
| `Ensure.That(...)` inside value objects and aggregates | Yes | Invariant guards in constructors, no hand written `if/throw` blocks. |
| `AggregateRoot<TId>`, `Entity`, `ValueObject`, business rules, domain events | Yes | Saves writing the same base classes again; Domain takes one small dependency. |
| `OrionGuard.AspNetCore` ProblemDetails filter | No | It answers with RFC 9457 ProblemDetails and status 422. The contract here is the Microsoft error envelope with 400. A Relevox pipeline behaviour runs the validator and the Kernel maps `GuardResult` to the envelope. |
| `OrionGuard.MediatR` | No | We use Relevox. The validation behaviour is about 30 lines in `Viatica.Kernel`. |
| `OrionGuard.EntityFrameworkCore` outbox | Spike in M0 | It dispatches outbox rows to an in process `IDomainEventDispatcher`; one handler would produce to Kafka. Open questions: it is compiled against EF Core 10 and we run EF Core 11; one dispatcher per table behind a lock caps throughput; per aggregate ordering must hold. If the spike fails any of these, we write a small outbox and say why in an ADR. |
| SQL injection / XSS heuristics | No | Queries are parameterised by EF Core and output is encoded by Vue. Heuristics add false positives and no protection. |

## 9. Messaging

- Kafka in KRaft mode. Client: `Confluent.Kafka` directly. MassTransit 9 is commercial and its 8.x
  Kafka rider adds little over the raw client for this shape.
- Topics: `stays.v1`, `cars.v1`, `flights.v1`, `booking.v1`, `payments.v1`, 12 partitions each.
- Messages are JSON with an envelope (`id`, `type`, `version`, `occurredAt`, `traceparent`, `data`).
  Schemas live in `Viatica.Contracts` as C# records serialised with source generated
  `System.Text.Json`. Contract tests pin the JSON shape. No schema registry: Confluent Schema
  Registry is under the Confluent Community License, which the licence gate rejects.
- Producers: `acks=all`, idempotence on. Consumers commit offsets only after the inbox write.
- A poison message goes to `<topic>.dlq` after a bounded number of attempts, with the error.

## 10. Architecture and layout

Per service: `Domain` (no framework dependencies except OrionGuard), `Application` (use cases,
Relevox handlers, validators, `I<Service>Db`), `Infrastructure` (EF Core, Kafka, OpenSearch, Redis,
supplier adapters), `Api` (minimal API host). Shared code in a kernel. An architecture test reads
each assembly's real references and fails on a wrong arrow.

```
viatica/
├── src/
│   ├── Kernel/
│   │   ├── Viatica.Kernel/             # errors, paging types, Relevox validation behaviour, money
│   │   ├── Viatica.Kernel.Web/         # error envelope, paging, api-version, ETag, repeatability
│   │   └── Viatica.Kernel.Messaging/   # Kafka producer/consumer, outbox relay, inbox
│   ├── Contracts/Viatica.Contracts/    # integration events
│   ├── Services/
│   │   ├── Stays/Viatica.Stays.{Domain,Application,Infrastructure,Api}/
│   │   ├── Cars/Viatica.Cars.{Domain,Application,Infrastructure,Api}/
│   │   ├── Flights/Viatica.Flights.{Domain,Application,Infrastructure,Api}/
│   │   ├── Search/Viatica.Search.{Application,Infrastructure,Api}/   # read model only, no domain
│   │   ├── Booking/Viatica.Booking.{Domain,Application,Infrastructure,Api}/
│   │   └── Payments/Viatica.Payments.{Domain,Application,Infrastructure,Api}/
│   ├── Gateway/Viatica.Gateway/
│   └── Simulators/{Viatica.AirlineSim, Viatica.PspSim}/
├── web/                                # Vue app (traveller + partner console)
├── tests/
│   ├── Viatica.ArchitectureTests/
│   ├── Viatica.<Service>.UnitTests/
│   ├── Viatica.<Service>.IntegrationTests/  # Testcontainers: Postgres, Kafka, OpenSearch, Redis
│   ├── Viatica.SystemTests/                 # whole stack: saga, chaos, reconciliation
│   └── Viatica.Testing/                     # shared fixtures, event probe, test tokens
├── load/  benchmarks/  tools/Viatica.LicenseAudit/
├── deploy/keycloak/  deploy/opensearch/  deploy/seed/
├── docs/adr/  docs/api/  docs/measurements/  docs/operations.md  docs/design.md
└── compose.yaml  Viatica.slnx  Viatica.http  ...
```

## 11. Stack (versions checked against nuget.org and npm on 2026-10-24)

| Area | Choice |
|---|---|
| Runtime | .NET 11 prerelease (`11.0.100-rc.2`, go-live licence) with C# 15; moved to 11.0 GA when it ships in November, before v1.0.0 |
| Web | ASP.NET Core 11 minimal APIs, built in OpenAPI, Scalar for the API reference |
| Mediator | Relevox 1.3.0 |
| Validation, DDD primitives | OrionGuard 7.0.0 (section 8) |
| Persistence | EF Core 11 + Npgsql provider 11 (rc.2 now), PostgreSQL 18 |
| Cache | Redis (flight offers, rate limiting state) |
| Messaging | Apache Kafka (KRaft), Confluent.Kafka 2.16.0 |
| Search | OpenSearch 3.x (Apache 2.0), OpenSearch.Client 2.2.0 |
| Gateway | YARP 2.3.0 |
| Identity | Keycloak |
| Observability | OpenTelemetry 1.20.x, Aspire dashboard container as the OTLP sink |
| Tests | xUnit v3 on Microsoft.Testing.Platform, Shouldly, Testcontainers 4.16, Playwright |
| Load | k6 run as an external tool, or a small .NET harness; NBomber excluded (commercial) |
| Frontend | Vue 3.5, Vite 8, TypeScript 7, Pinia 4, Vue Router 5, MapLibre GL for maps |

Elasticsearch is excluded on licence grounds (SSPL / ELv2 / AGPL). Redis server 8 is AGPL /
RSAL / SSPL; it runs as a separate process the product talks to over the network, which the licence
policy allows, and Valkey (BSD) is the drop in if that ever changes. Aspire hosting packages stay out
or pinned to 13.4.6 because newer ones pull in JsonPatch.Net, which asks a maintenance fee.

## 12. Non-functional requirements

**Performance.** Targets in section 2. Hot paths: no N+1, `AsNoTracking`, compiled queries, source
generated JSON, pooled `DbContext`. Benchmarks for the skiptoken codec, the filter parser and offer
cache serialisation.

**Reliability.** Every service survives a Kafka restart and its own restart mid saga without losing
or duplicating an effect. A supplier outage degrades flight search, never stays or cars. Health
endpoints `/health/live` and `/health/ready`. Supplier calls have timeouts and a circuit breaker
per airline.

**Security.**
- JWT validated at the gateway and again in each service (audience per service).
- Ownership checks for partner operations; a traveller only ever sees own trips. Tests try the cross
  tenant cases and expect `404`, not `403`, so ids do not leak.
- Rate limits on search, quote, re-price and checkout, per user and per IP.
- No card data outside Payments. Passenger personal data encrypted at rest in Flights and Booking.
  No tokens, card numbers, passport data or emails in logs.
- Least privilege database roles: each service's login owns only its own schema.
- Dependencies: permissive licences at every depth, enforced by the licence audit in CI.

**Observability.** One `ActivitySource` and one `Meter` per service. Trace context crosses Kafka in
the envelope, so one trace shows a click on "Pay" through holds, ticketing and capture. Metrics that
matter: holds placed / rejected / expired, saga duration and outcome, compensations run, supplier
latency and error rate per airline, offers re-priced, projection lag, outbox age, consumer lag.

## 13. Frontend (Vue)

- **Traveller:** one search box with tabs for stays, flights and cars; result lists with filters,
  facets, map, and infinite scroll driven by `nextLink`; flight results that show which airlines
  are missing and refresh when an offer expires; detail pages with live quotes; a trip basket mixing
  all three; checkout with passenger details and a test card; live checkout progress per item over
  SSE; my trips with per item cancellation and the refund each policy gives.
- **Partner console:** hotels and room types, or rental locations and fleet; a rate and allotment
  calendar with bulk range edits; bookings against own inventory.
- Error envelope `code` values map to translated messages; `details[].target` highlights the field.
- Accessibility checked in CI (axe), Playwright journeys against the composed stack.
- Built to the house frontend standard: strict TypeScript, no `any`, lint and type check gates.

## 14. Quality gates

1. `dotnet build Viatica.slnx` with zero warnings (`TreatWarningsAsErrors`, latest recommended
   analysers, `.editorconfig` enforced).
2. Unit, integration and architecture tests.
3. System tests on the composed stack, including chaos and reconciliation.
4. Licence audit.
5. `dotnet format whitespace` and `style` verify.
6. Web: type check, lint, unit tests, Playwright, axe.
7. A compose job that runs the README quick start and asserts a flight + hotel + car trip reaches
   `Confirmed` with one captured payment.

## 15. Definition of done for v1.0.0

- All FRs above implemented and covered.
- The four headline claims measured and published with hardware named.
- README with architecture, quick start, measured results, screenshots; ADRs for every decision in
  this document someone could reasonably argue with; `docs/operations.md` with a runbook per
  anticipated incident (stuck saga, airline outage, projection lag, outbox backlog, DLQ growth) and
  honest known limitations; `Viatica.http` covering every endpoint.
- On .NET 11 GA, CI green, tagged and released.

## 16. Milestones

| | Scope | Who |
|---|---|---|
| M0 | Scaffold, Kernel, Contracts, compose, CI, licence audit, `docs/design.md`; spikes: OrionGuard outbox on EF Core 11, Relevox and C# 15 unions on .NET 11 | lead |
| M1 | Stays service | agent |
| M2 | Cars service | agent |
| M3 | Flights service + airline-sim | agent |
| M4 | Search service (stays + cars projection) | agent |
| M5 | Booking + Payments + psp-sim, checkout saga | agent, after M1 to M3 contracts exist |
| M6 | Gateway, Keycloak, authorisation, rate limits | lead |
| M7 | Vue web app | agent, starts against contract mocks alongside M1 |
| M8 | System tests, chaos, reconciliation, load runs, measurements | lead |
| M9 | Docs, .NET 11 GA bump, v1.0.0 | lead |

## 17. Risks

| Risk | Plan |
|---|---|
| Scope is large | Milestones ship independently; the checkout saga (M5) is the centre, everything else can be thinner if time runs out |
| .NET 11 GA slips or breaks something from RC2 | RC2 is go-live; the bump is its own PR and CI decides |
| A dependency has no .NET 11 / EF Core 11 build | Checked in M0: Npgsql 11 rc.2 exists; OrionGuard EF package is the open one |
| Dev machine memory (OpenSearch, Kafka, Keycloak, Redis, four Postgres schemas, seven services) | One Postgres instance with a database per service; OpenSearch heap capped at 1 GB; load runs in CI or on a stated machine |
| Docker Hub pulls are slow on the office network | Pre pull images; heavy container tests run in GitHub Actions |
