# Messaging

`Viatica.Kernel.Messaging` moves integration events between services over Kafka: a transactional outbox and relay
on the way out, a consumer with an inbox and a dead letter topic on the way in. Why it is ours and not
OrionGuard's: ADR 0004. How events are shaped and which topic each goes to: ADR 0002 and `EventCatalog`.

## Wiring a service

**The DbContext** (Infrastructure) maps the outbox and inbox tables and lets Application queue events without
seeing Kafka:

```csharp
public sealed class StaysDbContext(DbContextOptions<StaysDbContext> options) : DbContext(options), IStaysDb
{
    public void Publish(IIntegrationEvent @event) => this.AddIntegrationEvent(@event);

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        modelBuilder.HasDefaultSchema("stays");
        modelBuilder.AddMessaging();   // outbox_messages and inbox_messages, created by the service's migrations
        ...
    }
}
```

A command handler changes its aggregate, calls `db.Publish(new StayHoldPlaced(...))` and calls `SaveChangesAsync`
once. The event row and the state change commit together or not at all. Nothing is sent from the request.

**The host** registers Kafka, the relay and one subscription per consumed topic:

```csharp
builder.Services.AddDbContextPool<StaysDbContext>(o => o.UseNpgsql(connectionString));
// The handler must get the same instance the consumer saves, or its changes miss the inbox transaction.
builder.Services.AddScoped<IStaysDb>(sp => sp.GetRequiredService<StaysDbContext>());

builder.Services.AddMessaging(
    new IntegrationEventTypes(ContractsJsonContext.Default, EventCatalog.TopicByType.Keys),
    o => o.BootstrapServers = builder.Configuration["Kafka:BootstrapServers"]!);

builder.Services.AddOutboxRelay<StaysDbContext>(Topics.Stays);

builder.Services.AddSubscription<StaysDbContext>(Topics.Booking, consumerGroup: "stays")
    .Handle<StayHoldRequested, StayHoldRequestedHandler>()
    .Handle<StayHoldReleaseRequested, StayHoldReleaseRequestedHandler>();
```

**A handler** lives in Application. It implements `IIntegrationEventHandler<TEvent>` from `Viatica.Contracts`,
changes state through `I<Service>Db`, and does not save: the consumer saves its changes and the inbox row in one
transaction. Throw to retry. An event for a state the aggregate has already left is not an error; the handler
records why and returns.

A host that queues events must register `AddOutboxRelay` or `AddSubscription` for that context, even when the relay
runs elsewhere: they add the interceptor that takes the per key lock, and `AddIntegrationEvent` throws without it.
Do not hold a transaction open across a slow call after queuing an event; other writers of the same key wait for it.

**Tracing and metrics:** add `MessagingTelemetry.Name` (`Viatica.Messaging`) to the host's OpenTelemetry tracing
and metrics.

**Topics, including `<topic>.dlq`,** are created by `kafka-init` in compose. The broker refuses to create topics
on first use, so a typo fails instead of inventing a topic.

## What it guarantees

| Guarantee | How | Test |
|---|---|---|
| An event is published if and only if its state change committed | Outbox row in the same transaction | `An_event_saved_with_the_state_change_reaches_kafka_in_the_envelope_and_is_applied_once` |
| Events with the same `PartitionKey` reach Kafka, and are applied, in the order they were saved, with any number of relay instances | Identity column order; writers of one key take a transaction scoped advisory lock before inserting, so sequence order is commit order (ADR 0007); a relay owns a key by locking its oldest row with `FOR UPDATE SKIP LOCKED`; a key stops at its first failed row | `Per_key_order_holds_through_two_relays_and_a_consumer`, `A_later_event_of_a_key_is_not_published_before_an_earlier_one_whose_transaction_is_still_open` |
| An event is applied once per consumer group, even when Kafka delivers it again | Inbox row keyed by (consumer group, event id) in the handler's transaction; offset stored only after that commits | `A_consumer_killed_between_handling_and_offset_commit_does_not_apply_the_event_twice` |
| A failing message holds its partition for a bounded time | `MaxAttempts` (default 5) with exponential backoff, then `<topic>.dlq` with the error in headers | `A_poison_message_goes_to_the_dlq_after_the_configured_attempts_and_its_partition_moves_on` |
| An unreadable message never reaches a handler | Parked on the DLQ at once, `dlq-attempts: 0` | `A_message_that_cannot_be_read_goes_to_the_dlq_without_retries`, `A_null_valued_record_goes_to_the_dlq_without_retries_and_the_next_record_is_applied` |
| One trace from the request that raised an event to the handler that applied it | `traceparent` in the outbox row and in the envelope | `Trace_context_of_the_code_that_raised_the_event_reaches_the_handler` |

## What it does not guarantee

- **Exactly once delivery.** Delivery is at least once: a relay that dies between Kafka's acknowledgement and its
  own commit sends those rows again. The inbox makes the effect happen once; a consumer that does not use the
  inbox will see duplicates.
- **Order across keys or topics.** Two trips' events interleave freely, and so do a trip's events on
  `booking.v1` and `stays.v1`. The saga must accept a reply that arrives before it expected one.
- **Order after a dead letter.** A message parked on the DLQ is skipped; the events behind it on its key are
  applied without it. Handlers must check the aggregate's state rather than assume the previous event happened.
- **Progress for a key whose row cannot be produced.** A row Kafka rejects for good blocks the rows behind it on
  that key, on purpose. `viatica.outbox.age` climbs; see "Outbox backlog" in `docs/operations.md`.
- **A DLQ replay tool.** The original bytes and headers are kept so one can be written; none exists yet.
- **Inbox cleanup.** Inbox rows are never deleted. At saga volumes that is fine for a long time; a retention job
  that deletes rows older than the topic retention is the fix when it is not.

## Wire format

```json
{
  "id": "0192f0c4-7a3b-7cde-8f00-4f7c2b1d9e01",
  "type": "StayHoldPlaced",
  "version": 1,
  "occurredAt": "2026-10-24T10:15:03.120Z",
  "traceparent": "00-4bf92f3577b34da6a3ce929d0e0e4736-00f067aa0ba902b7-01",
  "data": { "tripId": "...", "holdId": "..." }
}
```

The Kafka key is the event's `PartitionKey`. `id` is a UUID v7 minted when the event is queued, and is what
inboxes deduplicate on. `type` is the CLR record name. A consumer reads `version` 1 only; anything else goes to the
DLQ unread. A dead lettered message keeps its key, value and headers and gains `dlq-error-type`,
`dlq-error-message`, `dlq-attempts`, `dlq-topic`, `dlq-partition`, `dlq-offset` and `dlq-consumer-group`.

## Tuning

| Option | Default | Notes |
|---|---|---|
| `OutboxRelayOptions.BatchSize` | 500 | Rows, and keys locked, per round |
| `OutboxRelayOptions.PollInterval` | 100 ms | Idle wait; a full round starts the next at once. No LISTEN/NOTIFY: at this interval polling costs a few cheap queries per second and keeps the relay to one moving part |
| `OutboxRelayOptions.RetryDelay` / `MaxRetryDelay` | 1 s / 1 min | Backoff for a row whose produce failed |
| `SubscriptionOptions.MaxAttempts` | 5 | Handler attempts before the DLQ |
| `SubscriptionOptions.RetryDelay` / `MaxRetryDelay` | 200 ms / 10 s | Backoff between attempts, while the partition waits |

Relay throughput on the development laptop, from `benchmarks/Viatica.OutboxSpike` (ADR 0004): 2,315 rows/s with
ten events per key and one relay, 5,434 rows/s with two relays.
