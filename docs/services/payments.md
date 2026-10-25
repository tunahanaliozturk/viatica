# Payments

Payments takes the traveller's money for a trip, and gives it back. It tokenises a test card, holds the trip total on
it, captures the total when the trip is confirmed, releases the hold when the trip fails, and refunds in parts when
an item is cancelled. It does all of that against a payment provider it does not own, which can time out after it
has already taken the money.

It is requirements PAY-1 to PAY-3. The provider is [psp-sim](../psp-sim.md); Payments reaches it over HTTP and does
not reference it.

| | |
|---|---|
| Port | 8106 (`payments` in compose, `http/payments.http` has every request) |
| Stores | Postgres database `payments` (payments, payment methods, operation records, outbox, inbox) |
| Publishes | `payments.v1`: `PaymentAuthorized`, `PaymentDeclined`, `PaymentCaptured`, `PaymentVoided`, `PaymentRefunded`, `PaymentOperationRefused` |
| Consumes | `booking.v1`: `PaymentAuthorizationRequested`, `PaymentCaptureRequested`, `PaymentVoidRequested`, `PaymentRefundRequested` (other types on the topic are skipped) |
| Projects | `Viatica.Payments.{Domain,Application,Infrastructure,Api}`, tests in `tests/Services/Payments` |

## What it owns

- **Payment methods.** A token Booking can carry (`pm_...`, 192 random bits), the provider's own token behind it, and
  the brand, last four digits and expiry a receipt shows. The card number is not among them.
- **The payment of a trip.** `Payment`, one per trip, in the `payments` database: what was authorised, captured and
  refunded, and the provider's id for the authorisation. The money rules live in this aggregate and nowhere else.
- **A record of every concluded request.** `OperationRecord`, unique on trip id, kind and operation id. It is what
  makes a request idempotent, and it says whether the provider's answer had to be found by lookup.
- **The provider adapter.** `PspGateway`: one HTTP client with a timeout and a circuit breaker and no retry. Nothing
  outside it knows the provider's field names, status codes or error texts.

It does not decide whether a trip may be paid for, how much it costs, or what a cancellation refunds. Booking
decides and asks. It also does not know who a payment belongs to: events carry no traveller id, so the read endpoint
is for admins only.

## Endpoints

All need `?api-version=2026-11-01` except health.

| Method and path | Auth | Does |
|---|---|---|
| `POST /payments/methods` | traveller | Tokenises a test card (PAY-1). `201` with `token`, `brand`, `last4`, `expMonth`, `expYear`. `400` `InvalidRequest` with one detail per broken field, `400` `PaymentMethodInvalid` when the provider will not take the card, `503` `ProviderUnavailable` with `Retry-After` |
| `GET /payments/{paymentId}` | admin | A payment with its authorised, captured and refunded money and every concluded operation, with an `ETag`. No card data. `404` `PaymentNotFound` |
| `GET /health/live`, `GET /health/ready` | none | Ready means Postgres answers. The provider is not part of it, on purpose: an outage is handled per call by the circuit breaker |

Payments are not created over HTTP. Booking asks for them over Kafka, because the checkout saga is event driven and
every step of it must be repeatable by id.

```json
POST /payments/methods?api-version=2026-11-01
{ "number": "4242 4242 4242 4242", "expMonth": 12, "expYear": 2030, "cvc": "123" }
```

```json
201
{ "token": "pm_Qm9v2k6...", "brand": "visa", "last4": "4242", "expMonth": 12, "expYear": 2030 }
```

The number may have spaces or hyphens. Every problem is reported at once, with the code and the field and never the
value: `InvalidCardNumber` (`number`), `InvalidExpiry` (`expMonth`, `expYear`), `CardExpired` (`expYear`),
`InvalidCvc` (`cvc`). A card that passes those checks and is not one of the simulator's test cards is refused by the
provider, and the answer is `PaymentMethodInvalid` with the provider's code (`incorrect_number`) in `innererror`
(`TokenisationTests`). Every test card tokenises, including the ones that fail later, because the card decides the
outcome when it is charged.

## Operations and keys

Booking generates the ids before it asks (docs/design.md), so every request is idempotent by id. An operation is
named by the trip, its kind and an id:

| Request | Operation id | Answered with |
|---|---|---|
| `PaymentAuthorizationRequested` | the payment id | `PaymentAuthorized` or `PaymentDeclined` |
| `PaymentCaptureRequested` | the payment id | `PaymentCaptured` or `PaymentOperationRefused` |
| `PaymentVoidRequested` | the payment id | `PaymentVoided` or `PaymentOperationRefused` |
| `PaymentRefundRequested` | the refund id | `PaymentRefunded` or `PaymentOperationRefused` |

A payment has one authorisation, one capture and one void, so the payment id alone names three operations. The kind
is part of the key for that reason. The key a request has, here and at the provider, is
`{tripId:N}:{kind}:{operationId:N}` (`OperationRecord.KeyFor`). Its only inputs are those three, so a retry after a
timeout, a redelivery and a crash between the provider call and the commit all arrive at the same operation
(`PaymentRulesTests.The_idempotency_key_depends_on_the_trip_the_kind_and_the_operation_id_and_on_nothing_else`).

`PaymentOperationRefused` is new in `Viatica.Contracts` with this service. The contracts had an answer for a declined
authorisation and nothing for a capture, void or refund that cannot be done, which would have left Booking waiting
for ever. It carries the payment, the trip, which operation, the operation id and a reason: one of the money rule
codes below, or the provider's own code.

## How a request runs

```
request in
  operation already concluded?        yes: nothing. No call, no event, a debug line. Stop
  payment known? amount is money?     no:  refuse (PaymentNotFound, InvalidAmount)
  does the money rule allow it?       no:  refuse with the rule's code. The provider is not asked
  call the provider, key = the operation's key
     answered yes                     change the payment, record the operation, queue the event
     answered no                      record the operation, queue the refusal (or PaymentDeclined)
     no answer                        see "Unknown outcomes"
  state, record and event are saved in one transaction with the inbox row
```

The event is in the outbox, so it exists if and only if the state change committed. A request that is a repeat of a
concluded one produces **no** second event: the requirement is exactly one outcome per operation id, and since the
first outcome is committed with the operation, it cannot have been lost. (Flights answers a repeat again; Payments
does not, and `docs/design.md` says a replay in the target state is a no-op recorded at debug.)

### The money rules

Asked of `Payment` before the provider hears about the request. Each has a question (`WhyNot...`) and an action that
asks again and throws, so a caller that forgets to ask cannot break the rule.

| Rule | Refusal code | Test |
|---|---|---|
| A capture never exceeds the authorisation | `ExceedsAuthorization` | `PaymentRulesTests.A_capture_of_the_whole_authorisation_or_less_is_allowed_and_more_is_not`, `OperationTests.A_capture_above_the_authorisation_is_refused_by_us_and_never_reaches_the_provider` |
| A payment is captured once, in full or less; less releases the rest | `AlreadyCaptured` | `PaymentRulesTests.Capturing_less_than_was_authorised_closes_the_authorisation`, `OperationTests.A_partial_capture_takes_less_and_releases_the_rest` |
| Nothing is captured after a void | `PaymentVoided` | `PaymentRulesTests.Nothing_is_captured_after_a_void`, `OperationTests.A_void_releases_the_hold_and_a_capture_after_it_is_refused` |
| A captured payment is refunded, not voided | `AlreadyCaptured` | `OperationTests.A_captured_payment_cannot_be_voided_and_the_provider_is_not_asked` |
| Refunds add up to at most the capture | `ExceedsCapture` | `PaymentRulesTests.Refunds_add_up_to_the_capture_and_no_further`, `OperationTests.Refunds_in_parts_add_up_to_the_capture_and_one_more_cent_is_refused_without_a_provider_call` |
| Nothing is refunded before a capture | `NotCaptured` | `OperationTests.A_refund_before_any_capture_is_refused` |
| A declined payment can do nothing | `NotAuthorized` | `OperationTests.A_declining_card_is_answered_with_PaymentDeclined_and_the_provider_code_and_nothing_can_follow` |
| Amounts are positive and in the payment's currency | `InvalidAmount`, `CurrencyMismatch` | `PaymentRulesTests.An_amount_that_is_not_positive_is_refused`, `...Money_in_another_currency_is_refused_not_added_to_euros` |
| A payment id this service never saw | `PaymentNotFound` | `OperationTests.Requests_about_a_payment_this_service_has_no_record_of_are_refused_not_lost` |
| One payment per trip | decline `PaymentAlreadyExistsForTrip` | `OperationTests.One_trip_has_one_payment_a_second_payment_id_is_declined_and_the_first_is_untouched` |

`PaymentRulesTests.Whatever_order_requests_arrive_in_the_money_invariants_hold` runs 3000 random sequences of
captures, voids and refunds and checks after every step that captured is at most authorised, refunded is at most
captured, and a voided payment has had nothing taken.

## Unknown outcomes

This is the part worth arguing with. A provider that times out may have done the work. The rule is PAY-3: ask what
it recorded under the key, never retry blindly, and if that cannot be answered, decide nothing and try again later.

| Situation | What Payments does | Why |
|---|---|---|
| The call times out, drops, or answers `5xx` other than `503` | Looks the operation up by key, then decides | A guess either takes money twice or reports a payment that never happened |
| The lookup finds the operation | Takes its answer as the outcome, `ResolvedByLookup` is true | The provider did it; asking it to do it again is what is forbidden |
| The lookup finds nothing | Sends the call once more under the same key | With the same key the provider treats a copy that is still on its way as the same operation, so the resend cannot double |
| The lookup fails too | Throws; the consumer retries with backoff, nothing is decided | "Not found" is only an answer if the provider gave it |
| The resend fails too | Throws, even when the failure says nothing was sent | The first copy may still land. The next attempt asks again under the same key |
| Authorisation, circuit open or `503`: certainly not reached | `PaymentDeclined("ProviderUnavailable")` at once, no lookup | Nothing was held. The saga is waiting with holds that expire; failing fast lets it release them and tell the traveller to try again |
| Capture, void or refund, circuit open or `503` | Throws; retried with backoff | These cannot be declined. Booking has ticketed or cancelled on the strength of the money, and "not captured" would be a statement about the provider nobody checked |
| The provider refuses (`402`, `400`, `404`, `409`, `422`) | `PaymentDeclined` with its decline code, or `PaymentOperationRefused` with its error code | A no is an answer. It is not a failure to retry, and it does not count against the circuit |
| A request already concluded arrives again | Nothing | Exactly one outcome event per operation id |
| The process dies after the provider answered, before the commit | Nothing special: the redelivery calls the provider under the same key and gets the recorded answer | The key is derived from the request alone |
| Attempts run out (eight, about a minute) | The message goes to `booking.v1.dlq` | See "Known limits" |

**Circuit breaker open: decline fast, or retry?** Both, by operation, and the table above is the reason. An open
circuit means nothing was sent, so for an authorisation the truth is "nothing is held", and an answer the saga can act
on within its deadline is worth more than a wait. For the three operations that move or release money there is no
true answer to give until the provider is back, so they wait. The consumer's patience is sized for it:
`Payments:Consumer` allows eight attempts with the delay doubling from half a second to twenty, about a minute, which
outlasts the 15 second break. Proved by `CircuitBreakerTests.With_the_circuit_open_an_authorisation_is_declined_at_once_without_a_call_and_a_capture_waits_and_happens_once`:
the fourth authorisation never reaches the provider, and the waiting capture reaches it exactly once, after the
circuit closes.

| Claim | Test |
|---|---|
| A timeout that happened ends in the one right outcome, found by lookup, for authorise, capture, void and refund | `TimeoutTests.An_authorisation_that_timed_out_and_happened_ends_in_one_PaymentAuthorized_found_by_lookup`, `A_capture_that_...`, `A_void_that_...`, `A_refund_that_timed_out_and_happened_ends_in_one_PaymentRefunded_found_by_lookup` |
| The call that happened is not made again | the same tests: one `POST` and one `GET` on the wire for the key |
| A timeout that did not happen is sent again once, under the same key | `TimeoutTests.A_timeout_that_did_not_happen_is_sent_again_once_under_the_same_key_and_ends_in_one_outcome` (the wire reads POST, GET, POST), `A_capture_that_timed_out_without_happening_is_sent_again_and_captured_once` |
| A failed lookup leads to a retry and no guess, whether the work was done or not | `TimeoutTests.A_lookup_that_cannot_be_answered_decides_nothing_while_the_provider_may_have_captured_and_the_request_is_tried_again`, `...when_nothing_happened_is_not_read_as_a_failure_either` |
| A `500` is not a decline | `TimeoutTests.A_provider_that_answers_500_is_not_a_decline_it_is_asked_again_under_the_same_key_until_it_answers` |
| The decision procedure, call by call | `SettlementTests` (each test counts the calls that reached the provider) |
| Statuses and failures classified as might-have-happened or certainly-not | `PspGatewayTests` |

## Idempotency

Twice over: the consumer's inbox drops an event id it has applied, and the operation record answers a request under a
new event id with nothing.

| Claim | Test |
|---|---|
| The same event delivered twice is applied once | `IdempotencyTests.The_same_event_delivered_twice_is_applied_once` |
| The same request under new event ids captures once and answers once | `IdempotencyTests.The_same_request_under_new_event_ids_captures_once_and_answers_once` |
| The same for authorise, void and refund | `The_same_authorisation_asked_twice_holds_the_amount_once`, `The_same_void_asked_three_times_releases_once`, `The_same_refund_asked_twice_refunds_once_and_a_different_refund_for_the_rest_is_a_second_refund` |
| A refusal asked again is refused once | `IdempotencyTests.A_refused_request_asked_again_is_refused_once` |
| A crash after the provider captured ends in one capture and one answer | `IdempotencyTests.A_crash_after_the_provider_captured_and_before_the_commit_ends_in_one_capture_and_one_answer` (two calls on the wire with one key, one capture at the provider) |
| Duplicate provider answers do not double-capture | `IdempotencyTests.The_provider_answering_the_same_capture_twice_is_one_capture_and_a_second_key_cannot_capture_again`, at the simulator, `LedgerTests.A_repeat_under_the_same_key_returns_the_recorded_answer_and_changes_nothing` |

Every duplicate test also asserts that no handler failed. That matters: with the operation record's check removed,
a duplicate capture still ended with one outcome event, because the unique index on the record rejected the second
one, and the consumer then retried the message eight times and parked it. The index is a backstop for two consumers
that race, not a way to handle a repeat.

## Card data

PAY-1: the card number never leaves this service except to the provider, and is never stored or logged.

- **Where it is.** In the request body from the traveller, in `CardDetails` for the length of one request, and in the
  body of one `POST /v1/tokens` to the provider (`PspGatewayTests.The_card_number_is_in_the_tokenise_request_and_in_no_other_request`).
- **How it is kept out of everything else.** `CardNumber` and `CardDetails` print as `[card number]` and `[card
  details]`, the command and the request print as their names, and the one way to the digits is a method called
  `Reveal`, called in one place (`CardTests.A_card_prints_as_what_it_is_and_never_as_what_it_holds`). Log lines carry ids,
  operation names and outcome names. Validation messages never repeat what was typed.
- **What is stored.** The brand, the last four digits, the expiry and the provider's token. There is no column
  that could hold a number.
- **What proves it.** `NoCardDataStoredOrLoggedTests`: a full run (every test card, spaced and plain, good and bad,
  a provider outage during tokenisation, authorise, capture, refund, void and both timeout cards) is searched in
  four places. Every log entry of every category at Trace level, with structured values and exceptions. Every text,
  character, json and binary column of every table in the schema, the outbox payloads included. Every event on `payments.v1`. Every answer a caller got. Each search has a control that proves it would see
  a leak (`The_log_capture_would_see_a_leak`, `The_database_scan_would_see_a_leak`, which plants a number in a text
  column and a json column of a scratch table and finds both).
- **The security code** is sent to the provider with the number and appears nowhere else. The test searches for it
  next to the word `cvc` in the logs, and for `cvc` itself in the database.
- **Not here.** TLS to the provider (the local stack is plaintext), and anything a real PCI scope would add.

## Configuration

| Key | Default | Meaning |
|---|---|---|
| `ConnectionStrings:Payments` | none | Postgres, role `payments` |
| `Kafka:BootstrapServers` | none | Brokers |
| `Kafka:BookingTopic`, `Kafka:PaymentsTopic` | `booking.v1`, `payments.v1` | Only the integration tests change them |
| `Viatica:Api:SkipTokenKey` | none | 32+ bytes base64 |
| `Viatica:Auth:Authority`, `ValidIssuer` | Keycloak | Token validation, audience `payments-api` |
| `Payments:Provider:BaseUrl` | `http://localhost:8202/` | The provider's API root |
| `Payments:Provider:RequestTimeout` | `00:00:05` | Longest any provider call may take; past it the operation is looked up |
| `Payments:Provider:CircuitBreaker:*` | 0.5, 10, 30 s, 15 s | Failure ratio, minimum calls, sampling window, break duration |
| `Payments:Consumer:MaxAttempts`, `RetryDelay`, `MaxRetryDelay` | 8, 0.5 s, 20 s | How long a request the provider cannot settle is retried before it is parked |

A missing connection string, broker or provider address stops the host at start-up.

## Observability

One `ActivitySource` and one `Meter`, both `Viatica.Payments`. `payments.provider.calls` (operation, outcome) and
`payments.provider.duration` are the provider's error rate and latency; `payments.operations` (operation, result)
counts concluded requests; `payments.lookups` (found, none, failed) and `payments.resends` show how often a doubt had
to be settled and how; `payments.tokenisations` counts cards by result. Tags carry operation and outcome names, never
an id, an amount or a card. Traces cross Kafka in the envelope, so one trace shows a request from the saga to the
provider call.

## What proves it

Unit tests (80, no containers) and integration tests (55, real Postgres and Kafka, the real simulator in process and a
Booking stand-in that writes to `booking.v1` through an outbox). Run them with
`dotnet run --project tests/Services/Payments/Viatica.Payments.UnitTests` and `...IntegrationTests` (Docker must be
running). The simulator has its own, 51 unit and 23 integration tests (`docs/psp-sim.md`).

| Claim | Test |
|---|---|
| A card tokenises, and only brand and last four come back | `TokenisationTests.A_test_card_becomes_a_token_with_its_brand_and_last_four_and_nothing_else` |
| Bad cards are refused with every broken field, without the value | `TokenisationTests.A_bad_card_is_a_400_with_every_broken_field_and_without_the_values_in_the_answer`, `CardTests` |
| The card number goes to the provider in one request and to nobody else | `TokenisationTests.The_card_number_goes_to_the_provider_in_one_request_and_to_nobody_else` |
| Card data is never stored or logged | `NoCardDataStoredOrLoggedTests` |
| Authorise, capture (full, partial), void, refund (in parts) against the real provider | `OperationTests` |
| The money rules | `PaymentRulesTests`, `OperationTests` |
| Repeats and redeliveries change nothing | `IdempotencyTests` |
| Timeouts are settled by lookup, never by a blind retry | `TimeoutTests`, `SettlementTests` |
| An open circuit declines an authorisation and holds a capture | `CircuitBreakerTests`, `PspGatewayTests.A_circuit_opens_after_repeated_failures_and_the_provider_is_not_called_again_until_it_closes` |
| Only travellers tokenise, only admins read | `TokenisationTests.Tokenising_needs_a_traveller_token`, `OperationTests.An_unknown_payment_is_a_404_PaymentNotFound_and_only_an_admin_may_look` |

The idempotency and lookup tests were checked by breaking the code on purpose and watching them fail; the list is in
the pull request that added them.

## Known limits

- A request that runs out of attempts is parked on `booking.v1.dlq` and there is no replay tool yet
  (`docs/messaging.md`). For a capture, void or refund that means the provider stayed down for more than a minute.
  Booking's deadline unwinds the trip meanwhile, and a replay is safe: the key makes it the same operation.
- A payment method token is not bound to a traveller. Only Booking can use one, through events, and it is unguessable,
  but Payments does not check that the traveller who tokenised it is the one paying.
- An authorisation that the provider made after a lookup said "nothing recorded" cannot happen with this simulator,
  because a resend under the same key is the same operation. A real provider that accepts a late copy under a new
  idempotency scope would leave a hold that its own expiry ends.
- Authorisations do not lapse here, as they do after days at a real card network. A capture of a lapsed authorisation
  would come back as a provider refusal and be reported as `PaymentOperationRefused`.
- One currency per payment and one capture per authorisation. Several partial captures need a capture id Booking
  does not send yet.
- The lookup endpoint is the simulator's. A real provider has some equivalent (a list filtered by your own
  reference); the adapter is the only place that changes.
- A refund whose id repeats with a different amount is treated as the same operation: the first request wins and the
  second is ignored.
