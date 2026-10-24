# 0004: Our own outbox, not OrionGuard.EntityFrameworkCore's

Status: accepted, 2026-10-24

## Context

Every service publishes integration events through a transactional outbox (requirements, sections 6 and 9).
OrionGuard 7.0.0, which the domain layers already use, ships an outbox in `OrionGuard.EntityFrameworkCore`: a
`SaveChanges` interceptor stores the domain events an `AggregateRoot<TId>` raised, and a hosted service hands each
stored row to an in-process `IDomainEventDispatcher`. A dispatcher that produces to Kafka would make it ours.

Four questions decided it. The spike is `benchmarks/Viatica.OutboxSpike`; every number below comes from it, run
against Postgres 18 and Kafka 3.9.2 in Testcontainers.

1. Does it run on EF Core 11 at all? The package is compiled against EF Core 10; we run 11 rc.2.
2. Does it keep the order of one aggregate's events?
3. How many rows per second does its one dispatcher move to Kafka through a single handler?
4. Does it fit a model where Kafka carries integration events (records in `Viatica.Contracts`), not domain events?

Machine: Intel Core Ultra 7 255H, 31 GB RAM, Windows 11, Docker Desktop 29.5.3 on WSL2. Postgres data on tmpfs,
one Kafka broker, a 12 partition topic, producer with `acks=all` and idempotence. Single runs; the clock starts
when the host starts and stops when the outbox is empty, so connection and producer start-up are included.

## What the spike found

| Question | Result |
|---|---|
| EF Core 11 | Works. With EF Core pinned to 11 centrally, `typeof(DbContext).Assembly` reports 11.0.0.0 at run time and an event goes from `SaveChanges` to the dispatcher (`RunsOnEfCore11`). |
| Order, no failures | Broken. 200 aggregates with 10 events each, raised in one `SaveChanges` per 100 aggregates: all 200 were dispatched out of order (`KeepsOrderPerAggregateWithoutFailures`). |
| Order, one transient failure | Broken. Event 1 of an aggregate fails once: dispatch order was `2,6,3,7,4,5,8,9,10,1` (`KeepsOrderPerAggregateWhenOneEventFailsOnce`). |
| Throughput, one handler producing to Kafka | 62 rows/s with librdkafka's default 5 ms linger; 223 rows/s with linger 0. Batch size 100 and 500 made no difference (63 and 64 rows/s). |
| Fit with integration events | No. See below. |

Why order breaks: the dispatcher reads `ORDER BY OccurredOnUtc` and nothing else. Events raised in one method call
get timestamps microseconds apart, Postgres keeps microseconds, and the 2,000 rows above carried only 472 distinct
values; rows that tie come back in whatever order the scan finds them. Separately, a row that fails is recorded and
the loop moves on to the next row, so the rest of that aggregate overtakes it.

Why throughput is low: rows are dispatched one at a time, each in its own scope, and the handler awaits its produce
before the next row starts. With the default linger every row pays at least 5 ms of batching delay plus the
broker round trip and two database round trips. A distributed lock lets one dispatcher per table work at a time,
so a second replica adds nothing.

Why it does not fit:

- The dispatcher only accepts types that implement OrionGuard's `IDomainEvent`. Our integration events live in
  `Viatica.Contracts`, which may depend on nothing (`The_contracts_depend_on_nothing`). Each one would need a
  domain event twin and a mapping in the handler.
- A row has no partition key column; the handler would have to rebuild the Kafka key from the payload.
- Payloads are read with reflection-based `JsonSerializer.Deserialize(string, Type)`, falling back to the
  assembly-qualified type name stored in the row. Our events are serialised with a source-generated context and
  named by CLR name on the wire (ADR 0002).

## Decision

Write a small outbox in `Viatica.Kernel.Messaging` (`docs/messaging.md`) and do not take
`OrionGuard.EntityFrameworkCore`. OrionGuard stays in Domain for guards, validators and the DDD base classes.

The relay is shaped by the findings:

- Rows are ordered by an identity column, not a timestamp.
- A round locks the oldest pending row of each key with `FOR UPDATE SKIP LOCKED`. Holding that row owns the key,
  so any number of relay instances can run and per-key order still holds
  (`Per_key_order_holds_through_two_relays_and_a_consumer`; with the `NOT EXISTS` clause removed that test fails).
- A key's rows go out one after another and stop at the first failure; different keys go out concurrently.

Same machine, same harness (`ViaticaRelaySpike`):

| Shape | Rows/s |
|---|---|
| 10 events per key, one relay | 2,315 |
| 1 event per key, one relay | 8,604 |
| 10 events per key, two relays | 5,434 |

That is 37 times OrionGuard's dispatcher at default settings for the saga-like shape, and it grows with replicas.

## Consequences

- About a thousand lines of messaging code, comments included, to own, test and document, instead of a
  package update.
- Integration events are queued explicitly (`db.AddIntegrationEvent(...)`, behind the service's `I<Service>Db`).
  Nothing is published by raising a domain event; a handler that forgets to queue the event publishes nothing.
- The outbox SQL is Postgres-specific (`FOR UPDATE SKIP LOCKED`), which is fine: every service runs Postgres.
- A row that cannot be produced at all (larger than the broker accepts, say) blocks its key until someone deletes
  it. Order wins over progress for a key; `viatica.outbox.age` shows it and `docs/operations.md` says what to do.
- The spike project stays in the repository so the numbers can be reproduced. CI builds it but does not run it.

## Alternatives considered

- **OrionGuard's outbox with a Kafka dispatcher.** Fails order and fit, and moves a tenth of what the relay
  above moves even with linger tuned to 0.
- **Fix it upstream.** Ordering by a sequence and stopping a key at its first failure are real improvements to
  OrionGuard and worth proposing there. They would not remove the `IDomainEvent` constraint or the single
  dispatcher, and Viatica cannot wait for a release.
- **MassTransit's outbox.** MassTransit 9 is commercial; 8.x adds a rider abstraction over the same Kafka client
  for little gain here (requirements, section 9).
- **Debezium change data capture.** Reads the Postgres WAL instead of polling, but adds Kafka Connect and a
  connector per service to a stack that already strains a 16 GB laptop.
