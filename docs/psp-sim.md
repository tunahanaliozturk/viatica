# psp-sim

`src/Simulators/Viatica.PspSim` plays a card payment provider behind a Stripe shaped HTTP API. It is a test double,
not part of the product. The Payments service talks to it the way it would talk to a real provider, and the system
tests use it to make a payment slow, decline a card, or time out after the money has already moved.

## Why it exists

The payment half of the checkout saga is only interesting when the provider misbehaves. The case that matters most
is the one a real sandbox cannot be told to produce on cue: a capture that takes the money and then never answers.
The caller cannot tell it from a capture that did nothing, and a blind retry could take the money twice. The sim
makes that case a card number, so a test can have it whenever it needs it.

It does not try to look like our own APIs. It speaks snake_case JSON, ids with a type prefix (`tok_`, `pi_`, `re_`),
amounts as integers in the currency's minor unit and Stripe's error object, so the Payments adapter is written
against a foreign contract, as it will be in production. Two differences from Stripe are on purpose: requests are
JSON, not form encoded, and there is one extra endpoint, the lookup of an operation by idempotency key (below).

Everything it keeps is in memory. There is no database and no schema step; a restart is a reset.

## Test cards

The number decides the outcome, as Stripe's test cards do. Only these numbers are accepted. A number that passes the
Luhn check and is not in the table is refused with `incorrect_number`, as Stripe's test mode refuses a real card,
so a real number typed into a demo is never remembered.

| Number | Brand | What happens |
|---|---|---|
| `4242424242424242` | visa | Everything succeeds. |
| `5555555555554444` | mastercard | Everything succeeds. |
| `378282246310005` | amex | Everything succeeds. |
| `4000000000000002` | visa | The authorisation is declined: `402`, `card_declined`, decline code `generic_decline`. |
| `4000000000009995` | visa | The authorisation is declined: `402`, `card_declined`, decline code `insufficient_funds`. |
| `4000009990000013` | visa | **Times out and still happens.** The first attempt of every operation (authorise, capture, cancel, refund) does its work and then hangs, ending in `504`. A repeat under the same key gets the recorded answer. |
| `4000009990000021` | visa | **Times out and does not happen.** The first attempt of every operation hangs and does nothing, ending in `504`. A repeat under the same key does the work. |

The last two numbers are ours, in a range Stripe does not use. A timeout card acts on the first attempt under an
idempotency key only. The repeat is what shows whether the caller settled the doubt correctly, so it has to be
allowed to succeed. A timeout card tokenises normally: it misbehaves when it is charged. `GET /admin/cards` lists the
table.

Proved by `CardTests` (every number passes Luhn, each maps to its behaviour, a valid unlisted number is unknown),
`ProviderApiTests.A_declining_card_is_a_402_card_error_with_its_decline_code` and, for the two timeout cards,
`BehaviourKnobTests.The_timeout_card_that_performs_does_the_work_then_answers_504_and_a_repeat_gets_the_recorded_answer`,
`...The_performing_card_times_out_on_every_kind_of_operation_and_each_is_found_by_its_key` and
`...The_timeout_card_that_does_not_perform_leaves_no_trace_and_a_repeat_under_the_same_key_does_the_work`.

## The API

Base path `/v1`. Bodies are JSON with snake_case names. Every mutating call needs an `Idempotency-Key` header.

| Call | Does |
|---|---|
| `POST /v1/tokens` | Turns a test card into a token. `{ "card": { "number", "exp_month", "exp_year", "cvc" } }`. The answer carries brand, last four and expiry, never the number. Tokens stay valid for repeated use here; Stripe's are single use. |
| `POST /v1/payment_intents` | Authorises. `{ "amount", "currency", "payment_method", "capture_method": "manual", "metadata" }`. `requires_capture` on success. Only manual capture is simulated. |
| `POST /v1/payment_intents/{id}/capture` | Captures `amount_to_capture` or, with no body, everything. Less than the authorisation releases the rest, and the intent cannot be captured again. |
| `POST /v1/payment_intents/{id}/cancel` | Voids an authorisation that was not captured. |
| `POST /v1/refunds` | Refunds `amount` of a captured intent, or what is left. Refunds add up to at most what was captured. |
| `GET /v1/operations?idempotency_key=...` | What the provider recorded under a key. See below. |
| `GET /v1/payment_intents`, `GET /v1/payment_intents/{id}`, `GET /v1/refunds` | The ledger, for tests and for the system tests' reconciliation. |

```http
POST /v1/payment_intents
Idempotency-Key: 0192f0c4...:authorize:0192f0c4...
{ "amount": 31250, "currency": "eur", "payment_method": "tok_...", "capture_method": "manual",
  "metadata": { "trip_id": "...", "payment_id": "..." } }
```

```json
{ "id": "pi_9c1f...", "object": "payment_intent", "amount": 31250, "currency": "eur", "status": "requires_capture",
  "amount_capturable": 31250, "amount_received": 0, "amount_refunded": 0, "payment_method": "tok_...",
  "capture_method": "manual", "metadata": { "trip_id": "..." }, "created": 1792950000 }
```

### Idempotency

A repeat of a call under the same key is the same operation, not a second one. It gets the answer the first call got,
status and body, with the header `Idempotent-Replayed: true`, and changes nothing. That includes a decline: the
`402` is recorded and replayed. The same key for a different request (another amount, another intent) is refused as
`400 idempotency_error` and the first result stands. A call with no key is `400 idempotency_key_missing` and does
nothing. Keys are remembered until the process ends; Stripe forgets them after 24 hours.

Proved by `LedgerTests.A_repeat_under_the_same_key_returns_the_recorded_answer_and_changes_nothing`,
`...A_repeated_refund_is_one_refund`, `...The_same_key_for_a_different_request_is_refused_not_answered_with_the_first_result`
and, over HTTP, `ProviderApiTests.Every_mutating_call_needs_an_idempotency_key` and
`...A_repeat_under_the_same_key_is_flagged_returns_the_same_body_and_captures_once`.

### The lookup

Stripe has no way to ask what an idempotency key did. A caller whose request timed out needs one, so the sim has it:

```http
GET /v1/operations?idempotency_key=...
```

```json
{ "object": "operation", "idempotency_key": "...", "type": "capture", "http_status": 200,
  "result": { "id": "pi_9c1f...", "status": "succeeded", "amount_received": 31250 }, "created": 1792950001 }
```

`http_status` and `result` are exactly what the first call answered, so the caller reads them the way it would have
read that answer. An unknown key is `404 resource_missing`: the call never reached the provider, or it was a timeout
card that hung without working. A real provider offers some equivalent (a list filtered by your reference, a
retrieve by your own id); the adapter is where that difference lives.

### Errors

Stripe's object, `{ "error": { "type", "code", "message", "decline_code", "param" } }`.

| Status | Type, code | When |
|---|---|---|
| 400 | `invalid_request_error`: `idempotency_key_missing`, `parameter_invalid_integer`, `parameter_invalid_enum`, `parameter_missing`, `payment_intent_unexpected_state`, `amount_too_large` | The request or the state of the intent does not allow it. |
| 400 | `idempotency_error`: `idempotency_key_in_use` | The key was used for another request. |
| 402 | `card_error`: `card_declined` (with a decline code), `incorrect_number`, `invalid_expiry_month`, `expired_card`, `invalid_cvc` | The card is refused. The message never repeats the number. |
| 404 | `invalid_request_error`: `resource_missing` | No such token, intent, or recorded operation. |
| 500, 503, 504 | `api_error` | Injected by the knobs or a timeout card. |

## Knobs

Test cards decide what happens to one card. The knobs decide what happens to every call, live, through the admin API.

| Knob | Effect |
|---|---|
| `LatencyMedianMs`, `LatencySigma` | Log-normal delay added to every call. A trace shows it as a `pspsim.injected_latency` span. Default 80 ms. |
| `Outage` | Every call answers `503` at once, before anything is done, the lookup included. |
| `LookupOutage` | Only the lookup answers `503`. Operations keep working. This is how a test makes the doubt after a timeout impossible to settle. |
| `ErrorRate` | Share of mutating calls that answer `500` without doing anything. |
| `TimeoutRate` | Share of mutating calls that hang for `TimeoutHangMs` and end in `504`. |
| `PerformedRate` | Share of those timeouts that did their work before hanging. |
| `TimeoutHangMs` | How long a timed out call hangs. Default 30 s, longer than any sane client timeout. |

```http
GET   /admin/knobs            the live knobs and the defaults
PATCH /admin/knobs            { "TimeoutRate": 1.0, "PerformedRate": 1.0 }   fields left out keep their value
POST  /admin/knobs/reset      defaults, and the start of the random stream
GET   /admin/cards            the test cards
```

The random decisions come from one stream seeded from `PspSim:Seed` (default `20261025`), so the same calls in the
same order see the same failures on every run, and a reset repeats a run from the start. Concurrent callers
interleave their draws; the rates still hold (`BehaviourTests.The_same_seed_decides_the_same_fates_and_reset_starts_over`).

Each knob is shown to change behaviour in `BehaviourKnobTests`: outage on and off, the lookup outage failing only the
lookup, an error rate of one doing nothing, random timeouts that did and did not happen, latency following the knob,
the hang lasting as long as it says, and bad knobs refused.

The admin API is unauthenticated. The sim runs only inside a test stack and must never be exposed outside one.
Knobs and the ledger live in process memory: run one instance.

## Running it

```bash
docker build -f src/Simulators/Viatica.PspSim/Dockerfile -t viatica/psp-sim .
docker run --rm -p 8202:8080 viatica/psp-sim
```

Locally, `dotnet run --project src/Simulators/Viatica.PspSim` listens on port 8202. In compose it is `psp-sim`, with
the same health check as `airline-sim`. Health: `/health/live` and `/health/ready`. OpenTelemetry: `Viatica.PspSim`
ActivitySource and Meter with `pspsim.injected_latency`, `pspsim.faults` (by kind) and `pspsim.operations` (by
operation, status and whether it replayed); exported over OTLP when `OTEL_EXPORTER_OTLP_ENDPOINT` is set.

The card number is held for the length of one request. The sim keeps the brand, the last four digits and the test card
it matched, never the number, and no log line or trace tag has it (`ProviderApiTests.The_card_number_is_in_no_log_line_of_a_full_flow`,
which searches every log entry at Trace level and proves its capture sees the request lines first).

## Limits, known and accepted

- One lock guards the whole ledger. A simulator at test volumes gains nothing from finer locking, and a single lock
  cannot deadlock. Add a lock per intent if a load test ever shows contention.
- Authorisations never expire. A real card hold lapses after days; nothing here models that.
- One capture per authorisation, as with Stripe's manual capture. Multiple partial captures are not simulated.
- Idempotency keys are kept forever and the ledger grows with every call.
- A key is a plain string of at most 255 characters. The comparison of a repeat is by operation type and its
  parameters; a tokenise repeat compares the last four digits and the expiry, never the number.
- No 3-D Secure, no disputes, no webhooks, no currencies beyond a three letter lower case code.
