# 0002: Event shapes and topics

Status: accepted, 2026-10-24

## Context

Six services exchange state over Kafka. Two consumers have very different needs. The search projection wants the
current state of a listing and does not care how it got there. Booking runs the checkout saga and needs every hold,
order and payment outcome for one trip, in order, exactly once in effect.

## Decision

- **One topic per producing service**, `<service>.v1`, 12 partitions, plus a `.dlq` twin. A consumer subscribes to
  the producers it needs and ignores types it does not handle.
- **Listings are versioned snapshots.** `StayListingUpdated` and `CarLocationUpdated` carry the whole listing and a
  `ListingVersion`; availability changes carry an `AvailabilityVersion` per room type or category. The projection
  keeps the highest version. Order across partitions, duplicates and replays stop mattering, and a rebuild is a
  replay of the latest snapshots.
- **Saga traffic is keyed by trip.** Hold, order, payment and trip events use the trip id as the Kafka key, so all
  of one trip's events land on one partition in order.
- **Booking's instructions are events on Booking's topic** (`StayHoldRequested`, `PaymentCaptureRequested`, ...).
  They behave as commands (one consumer each) but live on `booking.v1`, so the saga's whole conversation is two
  topics deep and the instructions go through Booking's own outbox.
- **Wire format** is camelCase JSON from a source-generated `JsonSerializerContext`, enums as strings, money as
  `{ "amount": "312.00", "currency": "EUR" }` with the amount as a string. The envelope `type` is the CLR record
  name. Contract tests pin every record's JSON against committed snapshots.
- **No schema registry.** Confluent Schema Registry is under the Confluent Community License, which the licence
  audit rejects; Apicurio would be another moving part for six services in one repository. The contracts project
  plus snapshot tests give the same protection here: a breaking change fails a build.
- **Personal data stays off the broker in plain text.** Passenger details cross from Booking to Flights encrypted
  (AES-256-GCM, trip and item ids as associated data). Everything else uses ids.

## Consequences

- Search handles fewer, larger messages for listings; a listing with many room types repeats unchanged data on
  every edit. Acceptable at catalogue edit rates; availability changes, the frequent ones, are small.
- Renaming an event record is a breaking change, because the name is on the wire. Contract tests make that loud.
- Services that need another service's data keep a projection of it rather than calling over HTTP mid-request.

## Alternatives considered

- **Delta events for listings** (`RoomTypeAdded`, `AmenityRemoved`, ...). Precise, but the projection then depends
  on order and completeness, and a rebuild needs the full history.
- **A topic per event type.** More topics to create, monitor and keep in sync with consumers, and per-trip
  ordering across a saga would be lost.
- **Commands over HTTP from Booking.** Couples the saga to every service being up at the moment of checkout, and
  needs its own retry and idempotency story that the outbox and inbox already provide.
