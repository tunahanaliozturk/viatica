# 0007: Outbox writers take an advisory lock per key

Status: accepted, 2026-10-25

## Context

ADR 0004 and `docs/messaging.md` promise that events with the same `PartitionKey` reach Kafka in the order they
were saved. Issue #42 found a hole in that promise.

The order is the outbox's `sequence` column, an identity value. Postgres hands out an identity value when the row is
inserted, not when the transaction commits. The relay only sees committed rows. So:

1. Transaction A inserts sequence 1 for key K and stays open.
2. Transaction B inserts sequence 2 for key K and commits.
3. The relay sees sequence 2 only, sends it, deletes it.
4. A commits. The relay sends sequence 1.

Kafka now holds 2 before 1, and the consumer applies them in that order. The relay's `NOT EXISTS` check on lower
sequences cannot help: it cannot see rows of an open transaction. This is a real defect, not a theoretical one.
`A_later_event_of_a_key_is_not_published_before_an_earlier_one_whose_transaction_is_still_open` reproduced it before
the fix: sequence 2 was on the topic while A was still open.

The window is as wide as the time between A's insert and A's commit. A handler that calls a slow service between
`SaveChanges` and `Commit` makes it wide; the consumer's inbox transaction is the other long one.

## Options

| Option | Verdict |
|---|---|
| The relay refuses to send past a gap in the sequence | A gap is also what a rolled back transaction leaves behind. The relay cannot tell a gap that will fill from one that never will, so it would wait on a timeout and still be wrong for a transaction longer than the timeout. |
| Stamp rows with `pg_current_xact_id()` and send only rows below the oldest active transaction | Transaction ids are assigned at a transaction's first write, not at the insert of the outbox row. A transaction can hold the lower id and insert later than one with a higher id, so the horizon is not an order. Waiting for a moment with no open transaction at all would starve under load. |
| `pg_locks` on the outbox table to see open writers | The relays hold the same table lock while they work, so a relay would wait on the other relay. |
| A trigger that locks and then reassigns the sequence | Exact and free of round trips, but it is DDL every service must carry in its own migrations, and `EnsureCreated` in tests would not create it. |
| **Writers take a transaction scoped advisory lock per key before inserting** | Chosen. |

## Decision

`OutboxKeyLocks`, an EF Core `SaveChangesInterceptor`, runs before every save that adds outbox rows. In one statement
it takes `pg_advisory_xact_lock(hashtextextended(key, 0))` for each distinct key, sorted ordinally. If the context has
no transaction it opens one and commits it when the save ends, because a transaction scoped lock taken outside a
transaction is released at once.

The second writer of a key now waits for the first writer's commit before its insert, so its sequence is handed out
after the first is visible. Within a key, sequence order is commit order, and the relay needed no change.

- Keys are locked in sorted order, so two transactions that write the same keys in opposite order cannot deadlock
  (`Transactions_writing_the_same_keys_in_opposite_order_do_not_deadlock`; without the sort Postgres reports
  `40P01 deadlock detected`).
- The sort covers one save. A transaction that calls `SaveChanges` several times locks each save's keys as it goes,
  so two such transactions that reach the same keys in a different order can still deadlock. Postgres then aborts
  one of them with `40P01`: a consumer retries it like any other failure, an HTTP request answers 500. The rule is
  therefore one save per transaction for outbox writes.
- Advisory locks are per database, so services do not share a key space. Two keys whose 64 bit hashes collide only
  wait for each other needlessly.
- EF Core takes interceptors from the context options, not from the application service provider. `AddOutboxRelay`
  and `AddSubscription` add it with `ConfigureDbContext<TDbContext>`, once per context type. `AddIntegrationEvent`
  throws when the context was not set up that way, because an unlocked context would reorder under load and say
  nothing. A host that only writes events, with no relay in its process, therefore still has to register one of the
  two. No public signature changed.

## Measured cost

`benchmarks/Viatica.OutboxSpike`, `ViaticaRelaySpike.WriteCost`, Release, same machine as ADR 0004: 8 concurrent
writers, 500 saves each after an equal warm-up pass, one event per save. Two runs of each, before and after.

| Save | Before | After |
|---|---|---|
| One event, distinct keys | 3,794 and 4,233 saves/s | 1,246 and 1,346 saves/s |
| State change and event, distinct keys | 2,570 and 2,990 saves/s | 1,708 and 1,592 saves/s |
| State change and event, all writers on one key | 3,086 and 3,120 saves/s | 426 and 411 saves/s |

- A bare event is the worst case. EF Core saves a single statement without a transaction; the interceptor has to open
  one, so the save goes from one round trip to four. A command handler never saves a bare event.
- A state change next to the event already runs in a transaction. The lock adds one round trip to three, which is the
  35 to 45 percent seen above, about 1.5 ms per save with eight writers on this laptop.
- Writers of one key take turns for the length of their transaction, here about 2.4 ms. That is the price of the
  guarantee and it is the point: events of one aggregate must be written in order. It caps one hot key near 400
  saves/s on this machine. Keys in the saga are trip, hold and booking ids, which see a handful of events each.
- The relay reads and sends exactly as before. Rerun with the same harness: 2,494 rows/s (10 events per key, one
  relay), 12,504 (1 event per key), 3,880 (10 events per key, two relays), against 2,587, 13,394 and 3,944 without the
  interceptor in the same session. The 2,315 to 8,604 rows/s in ADR 0004 came from an earlier session; run to run
  variation on this laptop is as large as the difference.

## Consequences

- A save that adds outbox rows costs one extra statement, and one extra transaction when the caller had none.
- Do not hold a transaction open across a slow call after queuing an event: every other writer of that key waits.
  The same was true of the row locks the aggregate update takes, so this adds no new rule for a handler that saves
  once at the end.
- A writer waiting for a key's lock holds its database connection while it waits. Many concurrent writers of one hot key
  therefore occupy that many pool slots, which the Stays integration suite showed: with the hosts' pools left open it ran
  out of the test server's 100 connections, and its host now clears its pool on dispose, as Cars and the airline
  simulator do.
- A pooled context is safe: the interceptor remembers the transaction it opened per context instance and forgets it
  when the save ends, whether it succeeded, failed or was cancelled.
- Contexts that never queue an event pay a `ChangeTracker.Entries<OutboxMessage>()` scan per save.
