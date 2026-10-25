# Operations

How to run Viatica on one machine, how to put it back to a clean state, and what to do when something goes
wrong. The runbooks arrive with the milestones that build the parts they cover.

## Local stack

```bash
docker compose up -d --wait     # starts everything in compose.yaml and returns once all of it is healthy
bash deploy/check-stack.sh      # proves each dependency answers, as the compose job in CI does
```

The first start pulls about 1.9 GB of images (2.4 GB with the optional OpenSearch Dashboards). With the images
present, `up -d --wait` on empty volumes returned healthy in 63 seconds on the development laptop (16 GB, Docker
Desktop on WSL 2, 2026-10-24); Keycloak is the slow one.

Every password below is a public development value for a stack bound to localhost. None of them is a secret,
and none of them may be reused in any other environment.

| Service | Image | Host port | Credentials | What it is for |
|---|---|---|---|---|
| `postgres` | `postgres:18.6-alpine` | 5450 | superuser `postgres` / `postgres`; one role per service, password = role name | One database per service: `stays`, `cars`, `flights`, `booking`, `payments`, `airlinesim` |
| `kafka` | `apache/kafka:4.4.0` | 9094 (host), `kafka:9092` (containers) | none (plaintext) | Integration events between services, KRaft mode, one node |
| `kafka-init` | `apache/kafka:4.4.0` | none | none | One-shot: creates the topics, then exits 0 |
| `opensearch` | `opensearchproject/opensearch:3.9.0` | 9200 | none, security plugin disabled | The Search service's read model of stays and cars |
| `opensearch-dashboards` | `opensearchproject/opensearch-dashboards:3.9.0` | 5601 | none | Optional index browser, only with `--profile dashboards` |
| `valkey` | `valkey/valkey:8.1.10-alpine` | 6379 | none | Flight offer cache and rate limit counters (Redis protocol) |
| `keycloak` | `quay.io/keycloak/keycloak:26.8.1` | 8080 | admin console `admin` / `admin`; users below | Identity: realm `viatica`, OIDC for the web app and tokens for the services |
| `dashboard` | `mcr.microsoft.com/dotnet/aspire-dashboard:13.6.1` | 18888 (UI), 4317 (OTLP gRPC), 4318 (OTLP HTTP) | none | OpenTelemetry sink: traces, metrics and logs from every service |
| `stays-migrate` | built from `src/Services/Stays` | none | `stays` role | One-shot: applies the Stays migrations, then exits 0 |
| `stays` | built from `src/Services/Stays` | 8101 | tokens from Keycloak, audience `stays-api` | The Stays service: catalogue, rates, quotes and holds (`docs/services/stays.md`) |
| `psp-sim` | built from `src/Simulators/Viatica.PspSim` | 8202 | none; its admin API is unauthenticated, never expose it | The payment provider simulator: Stripe shaped, in memory, test cards and failure knobs (`docs/psp-sim.md`) |
| `payments-migrate` | built from `src/Services/Payments` | none | `payments` role | One-shot: applies the Payments migrations, then exits 0 |
| `payments` | built from `src/Services/Payments` | 8106 | tokens from Keycloak, audience `payments-api` | The Payments service: card tokens, authorise, capture, void and refund (`docs/services/payments.md`) |
| `booking-migrate` | built from `src/Services/Booking` | none | `booking` role | One-shot: applies the Booking migrations, then exits 0 |
| `booking` | built from `src/Services/Booking` | 8105 | tokens from Keycloak, audience `booking-api` | The Booking service: trips, the checkout saga, cancellation (`docs/services/booking.md`) |

Images are pinned by digest in `compose.yaml`; the table shows the tags.

Valkey stands in for Redis. Redis 8 is AGPL, which the licence policy in `docs/requirements.md` does not allow,
and Valkey (BSD) speaks the same protocol, so the services use an ordinary Redis client against it.

### Postgres

Each service has a login role with the same name as its database, owning that database and nothing else. The
roles are `NOSUPERUSER NOBYPASSRLS`, and `PUBLIC` has no access to any service database, so `stays` cannot even
connect to `booking`. `deploy/check-stack.sh` tries exactly that for every role.

```text
Host=localhost;Port=5450;Database=stays;Username=stays;Password=stays          # from the host
Host=postgres;Database=stays;Username=stays;Password=stays                     # from a container
```

### Kafka topics

| Topic | Partitions | Dead letters |
|---|---|---|
| `stays.v1` | 12 | `stays.v1.dlq` |
| `cars.v1` | 12 | `cars.v1.dlq` |
| `flights.v1` | 12 | `flights.v1.dlq` |
| `booking.v1` | 12 | `booking.v1.dlq` |
| `payments.v1` | 12 | `payments.v1.dlq` |

Automatic topic creation is off. A topic exists because `deploy/kafka/create-topics.sh` made it, and a producer
writing to a misspelt name gets an error instead of a new topic nobody reads.

### Keycloak

Realm `viatica`, imported from `deploy/keycloak/viatica-realm.json` on every start. Every user's password is
`viatica`.

| User | Role | `partnerId` claim |
|---|---|---|
| `traveller1` | `traveller` | |
| `traveller2` | `traveller` | |
| `partner-stays1` | `partner` | `c0000000-0000-4000-8000-000000000001` |
| `partner-cars1` | `partner` | `c0000000-0000-4000-8000-000000000002` |
| `admin1` | `admin` | |

User ids are fixed (`a0000000-0000-4000-8000-00000000000N`), so the `sub` claim, and anything a service stores
against it, survives a Keycloak restart.

Clients:

- `web`: public, authorization code with PKCE (S256), redirect `http://localhost:5173/*`. No password grant.
- `viatica-cli`: public, password grant only, for tests and `curl`. It exists because this is a development
  realm; a production realm would not have it.
- `stays-api`, `cars-api`, `flights-api`, `search-api`, `booking-api`, `payments-api`: bearer-only, one per
  service. They hold no secrets and issue nothing; they are the audiences. Tokens issued to `web` and
  `viatica-cli` carry all six in `aud`, and each service accepts only tokens naming its own.

`partnerId` is a declared user profile attribute that only an administrator can edit. If partners could edit it
from the account console, one could claim another partner's hotels.

A token from the command line:

```bash
curl -s http://localhost:8080/realms/viatica/protocol/openid-connect/token \
  -d grant_type=password -d client_id=viatica-cli -d username=traveller1 -d password=viatica
```

Brute force protection is on. **Tests must sign in once per user and reuse the token.** Keycloak counts
password grants for one account that arrive in parallel as an attack and locks the account, and the lock then
fails every test that uses it.

### Telemetry

Services export OTLP to `http://dashboard:18889` from a container, or to the OpenTelemetry default
`http://localhost:4317` when run from an IDE. Open <http://localhost:18888> to see traces, metrics and logs.

## Resetting state

| To reset | Run |
|---|---|
| Everything | `docker compose down -v && docker compose up -d --wait` |
| Postgres only (after editing `deploy/postgres/init.sql`) | `docker compose rm -sf postgres && docker volume rm viatica_postgres && docker compose up -d --wait` |
| Kafka only (all topics and offsets) | `docker compose rm -sf kafka && docker volume rm viatica_kafka && docker compose up -d --wait` |
| OpenSearch indices | `docker compose rm -sf opensearch && docker volume rm viatica_opensearch && docker compose up -d --wait` |
| Keycloak (users, sessions, lockouts) | `docker compose up -d --wait --force-recreate keycloak` |
| Valkey | `docker compose restart valkey` (it never writes to disk) |

`init.sql` runs only on an empty volume, so editing it does nothing until the Postgres volume is removed.
Resetting Kafka without Postgres, or the reverse, leaves outbox and inbox tables disagreeing with the broker;
reset both when in doubt.

## Known limitations of the dev setup

- **No transport security anywhere.** Postgres, Kafka, OpenSearch, Valkey and Keycloak all speak plaintext,
  OpenSearch has its security plugin disabled, and the Aspire dashboard has no sign-in. Fine on localhost,
  wrong everywhere else.
- **One of everything.** One Postgres instance holds every service database, one Kafka broker holds every topic
  with replication factor 1, one OpenSearch node holds every index. Losing a container loses that dependency;
  nothing here demonstrates failover.
- **Keycloak keeps its state inside the container** (`start-dev`, embedded database). Recreating it reimports
  the realm and drops sessions and lockouts, which is convenient for tests and useless for anything real.
- **The cluster turns yellow once an index has replicas.** A single OpenSearch node cannot place them, so the
  health check waits for yellow, not green.
- **Memory.** The stack takes about 2.9 GB before any service runs. Measured with `docker stats` right after
  start on the laptop above: OpenSearch 1.6 GB (1 GB of it heap), Keycloak 850 MB, Kafka 410 MB, the Aspire
  dashboard 66 MB, Postgres 52 MB, Valkey 3 MB. OpenSearch Dashboards adds 280 MB. On a 16 GB machine, stop
  other container stacks first.
- **Fixed ports.** 5450, 9094, 9200, 6379, 8080, 18888, 4317 and 4318 must be free. Change the left side of the
  `ports:` entry if one is taken.
- **Two containers have no health check.** `kafka-init` runs once and exits; `up --wait` waits for it to exit 0,
  and services wait on `condition: service_completed_successfully`. The Aspire dashboard image is distroless,
  with no shell or HTTP client to run a check with; `deploy/check-stack.sh` probes it from outside, and no
  service should wait on it.

## Runbooks

Each runbook says how the incident shows up (metric, log, alert), how to confirm it, and how to recover without
losing or duplicating an effect. They are written with the milestone that builds the part they cover.

### Stuck checkout saga

**Shows up as** a checkout operation that stays `Running` well past the 15 minutes a hold lasts; `booking.sagas` ending
fewer trips than `booking.checkouts` starts; `booking.deadlines.reached` rising (holds or tickets are not arriving in
time); `booking.compensations` rising with no deadline behind it (answers are failing); and, in the logs, `reached its
deadline` (warning) and `The scheduled check of trip ... failed` (error). A trip is stuck if it is in `Holding`,
`Authorizing`, `Ticketing` or `Unwinding` with `updated_at` more than a few minutes old, or in `Committing` or `Capturing`
for more than a minute or two.

**Confirm** which trips and why, in the `booking` database:

```sql
select id, status, failure, payment_status, checkout_started_at, deadline, next_check_at, updated_at
from booking.trips
where status in ('Holding','Authorizing','Ticketing','Committing','Capturing','Unwinding')
order by checkout_started_at limit 20;

select id, kind, status, awaiting, order_id, hold_id, expires_at, refusal
from booking.trip_items where trip_id = '<trip id>' order by added_at;
```

`awaiting` is the one request the trip has sent and not had answered: `Hold` (the first request), `Ticketing`,
`Commit`, `Release`, `Void` or `Cancel`. Read the trip from it:

- `Holding`, `Authorizing` or `Ticketing`, `deadline` in the past, still not `Unwinding`: the scheduled check is not
  running. It runs in every instance, every 5 seconds, against `next_check_at`. Look for `The scheduled check ... failed`
  (the database or a lock), and check the service is up. Nothing is lost: the first check that runs unwinds the trip.
- `Unwinding`, some item `awaiting` `Release`, `Void` or `Cancel`: the owner has not confirmed. The request is sent
  again every 30 seconds, so a slow owner finishes on its own. If it stays, the owner is down or the message is on its
  dead letter topic (`booking.v1.dlq` carries what Booking sent, `<owner>.v1.dlq` what the owner could not deliver back;
  see "Dead letter queue growth"). Fix the owner, replay, and the trip finishes when the answer lands.
- `Unwinding`, a flight `Pending` with `awaiting = Hold` and no `order_id`: Flights never answered the order request.
  Booking cannot void an order whose id it was never given, so it waits. The late `FlightOrderCreated`, if it ever
  comes, is voided at once. If the airline cannot have made the order (ask for the item id as the idempotency key at the
  airline, `docs/airline-sim.md` for the simulator), settle the item by hand and let the next check finish the trip:

  ```sql
  update booking.trip_items set status = 'Rejected', awaiting = 'None' where id = '<item id>' and order_id is null;
  update booking.trips set next_check_at = now() where id = '<trip id>';
  ```

  An unwinding trip is looked at as a whole again at that check, so it goes to `Failed` and announces `TripFailed`.
- `Committing`, an item `awaiting = Commit` for minutes: Stays or Cars has not answered the commit. Its own sweeper
  decides: if the hold expired you will see `StayHoldExpired` or `CarHoldExpired` and the trip unwinds; if the
  owner is down, bring it back and the commit lands.
- `Capturing` for minutes: Payments has not answered. A refused capture comes back as `PaymentOperationRefused` and
  unwinds the trip, so a trip waiting here has lost a message. Look in Payments for the operation (`payment_id` on the trip) and its outcome;
  if it was captured, replay `PaymentCaptured` to `payments.v1` (the inbox makes a replay of something applied a
  no-op); if it cannot be, the tickets and rooms are real and the decision is a person's.
- `Failed` but an owner still shows a live hold, order or authorisation (a reconciliation failure): the trip is
  `Failed` with an item `awaiting` `Release` or `Void` still set. It is resent every 30 seconds while that stays set;
  an answer that never comes is the same case as above. The owner's own sweeper gives a stay or car back after 15
  minutes and an airline releases an order at its ticketing time limit, which bounds the damage.

**Recover** nothing by editing statuses except the single case above. Every state change is an event consumed by the
trip's aggregate, and a row changed by hand skips the events that would have followed it. Prefer fixing the owner and
letting the resend finish the trip.

### Airline outage

To be written with Flights (M3).

### Payment provider outage

**Shows up as** `payments.provider.calls` with outcomes other than `200` climbing, `payments.lookups{result="failed"}`
above zero, `PaymentDeclined` events with the reason `ProviderUnavailable`, and the warning `... could not be resolved
(...); nothing is decided and the request will be tried again` in the Payments log. A capture, void or refund the
provider cannot settle is not answered: its trip waits, and the consumer logs `Handler for PaymentCaptureRequested ...
failed on attempt N of 8`.

**Confirm** whether the provider or Payments is at fault, and where one trip stands:

```bash
curl -s localhost:8202/health/ready            # the simulator; a real provider has its own status page
docker compose logs payments | grep -E "could not be resolved|looking the operation up"
```

```sql
-- payments database: what was concluded for a trip, and whether the provider's answer had to be looked up
select kind, result, reason, resolved_by_lookup, created_at
from payments.payment_operations where trip_id = '<trip id>' order by created_at;
```

- Authorisations declined as `ProviderUnavailable`: nothing was held. Booking fails those trips and the traveller tries
  again. Nothing to repair.
- Captures, voids and refunds without an outcome: each is retried up to eight times over about a minute, under the same
  idempotency key, and asks the provider what it recorded before it decides. When the provider is back they conclude
  by themselves, once. `resolved_by_lookup = true` marks the ones where the provider had done the work and only the
  answer was lost.
- `lookups{result="failed"}` while operations work: the lookup alone is down. Operations are still retried, and none is
  decided until it answers.

**Recover** a request that ran out of attempts (a message on `booking.v1.dlq` of type `PaymentCaptureRequested`,
`PaymentVoidRequested` or `PaymentRefundRequested`) once the provider is back: put it on `booking.v1` again with its
key and value unchanged. A replay is safe, because the operation's key makes it the same operation at the provider
and the operation record here makes a second conclusion impossible. For a drill against the simulator, `PATCH
/admin/knobs` with `{ "Outage": true }` and `POST /admin/knobs/reset` afterwards (`docs/psp-sim.md`).

**Never** capture or refund by hand at the provider without the operation's key: the next retry would not know the
money had moved, and the provider could take it twice.

### Projection lag

**Shows up as** `viatica.search.projection.lag` (histogram, seconds: an event's `occurredAt` to the index write that
carries it) with a p99 above 2 s, the target in `docs/requirements.md`; or `viatica.search.events` stuck while
Stays or Cars keep publishing. Searches still answer, from older data: a property can look free after it sold out
(the quote and the hold are authoritative, so a booking is refused, not oversold) or look sold out after a release.

**Confirm** where the time goes:

```bash
docker compose exec kafka /opt/kafka/bin/kafka-consumer-groups.sh --bootstrap-server localhost:9092 --describe --group search
curl -s localhost:9200/_cluster/health?pretty; curl -s localhost:9200/_cat/thread_pool/write?v
```

- `LAG` growing on every partition, OpenSearch healthy: the consumer is slower than the producers. A batch is up to 500
  events and one `_mget` plus one `_bulk`; a second instance of the service takes half the partitions.
- `LAG` growing and the log says `The index is unavailable`: OpenSearch is down or rejecting writes (`write` queue
  rejections, red cluster, disk watermark). The consumer waits with backoff and loses nothing; fix OpenSearch and it
  catches up on its own.
- Lag only on one partition: one property is hot (many availability events). Its document is rewritten whole for each
  batch; look for a producer sending changes one night at a time.
- `viatica.search.events{outcome="dead_lettered"}` above zero: see "Dead letter queue growth". Search is the one
  consumer where a replay is always safe: put the message back on `stays.v1` or `cars.v1` and it applies or is dropped
  by version.

**Recover** a projection that is wrong rather than late (a bug fixed, a mapping changed, an index lost): rebuild it
from the topics, which does not interrupt searching.

```bash
docker compose run --rm search-migrate rebuild
```

It reads `stays.v1` and `cars.v1` from the start into new indices, switches the aliases in one request and deletes the
old indices (`docs/services/search.md`, "Rebuild"). It is only as complete as Kafka's retention, seven days by
default: older events are gone from the replay. If the rebuild fails, the old indices are untouched and still live.

### Outbox backlog

**Shows up as** `viatica.outbox.age` (seconds, tagged `outbox` with the DbContext type's full name and `messaging.destination.name` with the topic) above 30, the limit
`docs/design.md` sets after a quiet run. Logs carry `Producing outbox row ... failed on attempt N` (warning) or
`Outbox relay round for ... failed` (error).

**Confirm** in the service's database:

```sql
select partition_key, count(*), min(occurred_at), max(attempts), max(last_error)
from <schema>.outbox_messages group by partition_key order by min(occurred_at) limit 20;
```

- Many keys, `attempts` 0: the relay is not running or not keeping up. Check the service is up and its log for
  relay round errors (database or broker unreachable). More replicas drain faster; each owns different keys.
- Many keys, `attempts` above 0, the same `last_error`: Kafka is down or rejecting the producer. Fix the broker;
  rows retry with backoff up to a minute and drain on their own.
- One key, `attempts` climbing: that row can never be produced (too large, say), and the rows behind it on the
  same key wait on purpose. Copy the row out for the owning team, then delete it:
  `delete from <schema>.outbox_messages where id = '<id>';`. Its consumers never see that event, so treat it like
  a dead letter: whoever owns the aggregate decides whether to repair state by hand.

**Never** delete rows in bulk or truncate the table to "unstick" it: each row is an effect another service is
waiting for.

### Dead letter queue growth

**Shows up as** messages on any `<topic>.dlq`, `viatica.messaging.consumed{outcome="dead_lettered"}` above zero,
and the error log line `Message at <topic>/<partition>@<offset> sent to <topic>.dlq: <exception type>`. Outside a
chaos test that expects one, any dead letter is an incident.

**Confirm** what was parked and why. The value is the original envelope, untouched; the reason is in headers:

```bash
docker compose exec kafka /opt/kafka/bin/kafka-console-consumer.sh --bootstrap-server localhost:9092   --topic booking.v1.dlq --from-beginning --property print.headers=true --property print.key=true
```

- `dlq-attempts: 0` and a `System.Text.Json` error type: the message could not be read (malformed JSON, or an
  envelope `version` this consumer does not know). A producer is ahead of the consumer or broken; fix the code.
- `dlq-attempts` equal to the subscription's `MaxAttempts`: the handler threw every time. The service log has
  each attempt with its exception, by event id (`Handler for <type> <id> failed on attempt ...`).

**Recover.** Fix the cause, then put the message back on its original topic with its key and value unchanged.
The consumer's inbox makes a replay of something already applied a no-op, so replaying is safe. Remember the
events behind it on the same key were applied without it: the handler must cope with arriving late, and the
reconciliation in `docs/design.md` says whether state needs repair. There is no replay tool yet
(`docs/messaging.md`, "What it does not guarantee").
