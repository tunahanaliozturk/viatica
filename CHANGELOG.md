# Changelog

All notable changes to this project are documented here. The format follows
[Keep a Changelog](https://keepachangelog.com/en/1.1.0/), and the project uses
[semantic versioning](https://semver.org/spec/v2.0.0.html).

## [Unreleased]

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
