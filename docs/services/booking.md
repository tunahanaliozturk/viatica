# Booking

Booking is where a trip is built, paid for once, and either confirmed in full or undone in full. A traveller puts any
mix of stays, cars and flights into a trip, checks out, and Booking runs the saga of `docs/requirements.md` section 6
against Stays, Cars, Flights and Payments. It is requirements BKG-1 to BKG-6.

| | |
|---|---|
| Port | 8105 (`booking` in compose, `http/booking.http` has every request) |
| Store | Postgres database `booking` (trips, items, outbox, inbox) |
| Publishes | `booking.v1`: the requests to the owners (`StayHoldRequested`, `FlightTicketingRequested`, `PaymentCaptureRequested`, ...), `TripConfirmed`, `TripFailed`, `TripItemCancelled`, and the state changes `TripStateChanged`, `TripItemStateChanged`, `TripClaimed` |
| Consumes | `stays.v1`, `cars.v1`, `flights.v1`, `payments.v1`: every hold, order and payment outcome, including `PaymentOperationRefused` (other types on those topics are skipped) |
| Projects | `Viatica.Booking.{Domain,Application,Infrastructure,Api}`, tests in `tests/Services/Booking` |

The order of the saga, and the alternatives that lost, are ADR 0006. This page is what the service does and where it
chose.

## What it owns

- **Trips and their items.** A `Trip` holds items of three kinds. Each item carries what the traveller asked for (a
  quote token, or a re-priced offer id and the total shown), then what the owner confirmed (the authoritative total,
  when the hold or order lapses, and the owner's cancellation terms).
- **The checkout saga**, as state on the aggregate. There is no engine: `Trip` is a function from its state and one
  cause to its new state and the instructions to send next. The instructions are plain records (`SagaDirective`)
  that the Application layer turns into outbox rows in the same transaction.
- **Cancellation of one item**, with the refund worked out from that item's own policy.
- **The deadline**, and the scheduled check that acts on it.

It does not own prices (the owners' totals are authoritative), inventory, or card data. A payment method token passes
through sealed and is gone when the saga ends.

## Endpoints

All need `?api-version=2026-11-01` except health and OpenAPI. Someone else's trip answers `404`, never `403`.

| Method and path | Who | Does |
|---|---|---|
| `PUT /trips/{id}` | traveller, or anonymous with `Trip-Access-Key` | Starts a draft under a client chosen id. `201`; again `200`; the id taken by someone else `409 TripAlreadyExists` |
| `GET /trips/{id}` | owner, key holder, admin | The trip, its items and states, what it costs now. `ETag` is the row version |
| `GET /trips` | traveller | Own trips, newest first. Keyset paged; `$filter` and `$orderby` on `status`, `createdAt`, `updatedAt` |
| `PUT /trips/{id}/items/{itemId}` | owner, key holder | Adds a stay or car (`quoteToken`) or a flight (`offerId`, `expectedTotal`). Draft only. Same item `200`, another with that id `409 ItemConflict` |
| `DELETE /trips/{id}/items/{itemId}` | owner, key holder | Removes an item from a draft; `204` also when it was not there |
| `POST /trips/{id}/claim` | traveller | Takes an anonymous trip, with its key as proof |
| `PUT /trips/{id}/checkout` | traveller | Starts the saga. `202` with `Operation-Location`. Same body again changes nothing; another body `409 CheckoutConflict` |
| `GET /operations/{id}` | owner, admin | A checkout (the trip's id) or a cancellation (the item's id): `NotStarted`, `Running`, `Succeeded`, `Failed` with the reason |
| `GET /trips/{id}/events` | owner, admin | Server sent events: the trip every time it changes, until the checkout ends. The event id is the trip's revision |
| `POST /trips/{id}/items/{itemId}/cancel` | traveller | Asks the owner to give one confirmed item back. `202` with `Operation-Location` |
| `GET /partner/bookings` | partner | Confirmed and cancelled stays and cars against the partner's own inventory. Keyset paged; `$filter` on `tripId`, `kind`, `status`, `bookedAt`, `startsAt`, `total` |

### Items

A stay or a car is added with the quote token its owner issued. Booking reads the token's readable payload
(`docs/design.md`, "Quote tokens") for the dates, the rooms, the driver's age and the price, and rejects one that is
unreadable, of the wrong kind, not in EUR (`400 QuoteInvalid`) or past its expiry (`409 QuoteExpired`). It does not
verify the signature and does not trust the price: the owner verifies both when it places the hold, and the total in
its answer is what is charged. A flight is added with the offer id `POST /flights/offers/{id}/price` returned and the
total the traveller was shown; Flights compares that total with the airline's at order time.

(`ApiSurfaceTests`, `ApplicationTests.A_stay_quote_is_read_for_its_dates_rooms_price_and_expiry`)

### Anonymous trips

Anyone can build a trip. The id alone would not protect it, because the client chooses it and a client may choose
badly, so an anonymous trip also needs a secret the builder makes up and sends as `Trip-Access-Key` (22 to 128
characters). Only a SHA-256 hash of it is stored, compared in constant time. Signing in and claiming the trip with that
key makes it the traveller's, clears the hash, and the key stops working. A checkout of an anonymous trip by a signed-in
traveller who holds its key claims it in the same request, so a double click cannot fall between the two.

(`TenancyTests.An_anonymous_trip_belongs_to_whoever_holds_its_key_until_a_traveller_claims_it`,
`A_traveller_who_checks_out_an_anonymous_trip_with_its_key_takes_it_in_the_same_request`)

## The checkout

```
Draft -> Holding -> Authorizing -> Ticketing -> Committing -> Capturing -> Confirmed
            |            |             |             |
            +------------+-------------+-------------+--> Unwinding -> Failed
```

`Ticketing` is skipped when there is no flight, `Committing` when there is no stay or car. A confirmed trip whose items
were all cancelled afterwards is `Cancelled`.

| In (topic) | Out (`booking.v1`) |
|---|---|
| `StayHoldPlaced`, `CarHoldPlaced`, `FlightOrderCreated` (`stays.v1`, `cars.v1`, `flights.v1`) | When every item is held: `PaymentAuthorizationRequested` |
| `PaymentAuthorized` (`payments.v1`) | `FlightTicketingRequested` per flight; then `StayHoldCommitRequested`, `CarHoldCommitRequested` |
| `FlightTicketed`, `StayHoldCommitted`, `CarHoldCommitted` | `PaymentCaptureRequested` once everything is committed |
| `PaymentCaptured` | `TripConfirmed` |
| any refusal, a decline, a lost hold, the deadline | `StayHoldReleaseRequested`, `CarHoldReleaseRequested`, `FlightOrderVoidRequested`, `StayBookingCancelRequested` (for what was committed), `PaymentVoidRequested` (if authorised), then `TripFailed` |

Every state change is also an event: `TripStateChanged` for the trip, `TripItemStateChanged` for each item (including
`CancelRequested` and `Removed`), `TripClaimed`. They carry no instruction and nothing consumes them yet; they are the
trip's history for anyone who wants it (BKG-6). All are keyed by trip id.

### The cases section 6 and the service notes name

| Case | What the trip does | Test |
|---|---|---|
| A hold is rejected | Releases what was held, voids the orders, charges nothing; never authorises | `FailureTests.A_refused_hold_releases_what_was_held_voids_the_order_and_charges_nothing`, `SagaBranchTests.A_rejected_hold_releases_everything_already_held_and_charges_nothing` |
| `FlightOrderRejected(InvalidPassengers)` | An ordinary rejection | `SagaBranchTests.A_rejected_flight_order_is_an_ordinary_rejection_including_invalid_passengers` |
| The card is declined | Releases and voids the orders; there is no authorisation to void | `FailureTests.A_declined_card_releases_everything_and_there_is_no_authorisation_to_void` |
| The airline refuses to ticket (or `FlightTicketingRefused("SupplierUnavailable")`) | Voids the orders, releases the rooms, voids the authorisation; nothing was committed | `FailureTests.A_refused_ticket_voids_the_order_releases_the_rooms_and_voids_the_authorisation_without_committing_anything` |
| A commit is lost to the owner's sweeper after the flights were ticketed | Cancels the committed stay or car, voids the tickets, voids the authorisation | `FailureTests.A_commit_lost_to_the_owners_sweeper_after_the_flight_was_ticketed_unwinds_the_whole_trip` |
| A hold arrives after the trip gave up (the owner judged a redelivered request again) | One release for it | `FailureTests.A_hold_that_arrives_after_the_trip_gave_up_on_the_item_is_released_exactly_once` |
| `FlightOrderCreated` arrives for an item that was rejected, unwound or past its deadline (the airline created the order after a timeout) | One void request | `FailureTests.An_order_that_appears_after_the_trip_gave_up_on_the_flight_is_voided_exactly_once` |
| `PaymentAuthorized` arrives for a trip that is unwinding or failed | One void | `FailureTests.An_authorisation_that_arrives_after_the_trip_failed_is_voided_exactly_once` |
| Payments refuses the capture (`PaymentOperationRefused`) | Nothing was taken: the tickets are voided, the committed stays and cars cancelled, the authorisation voided; `TripFailed(PaymentDeclined)`. The capture is not asked for again | `FailureTests.A_capture_that_payments_refuses_gives_everything_back_and_the_trip_fails_instead_of_waiting_for_ever`, `SagaBranchTests.A_refused_capture_gives_back_the_tickets_and_the_rooms_and_voids_the_authorisation` |
| Payments refuses a void (the payment was declined or never made) | Nothing is left to void; the payment counts as voided | `SagaBranchTests.A_refused_void_means_there_is_nothing_left_to_void_and_a_refused_refund_leaves_the_item_cancelled` |
| Payments refuses a refund | The item stays cancelled, `refundStatus` is `Refused`, the money is owed and a person settles it | the same test |
| A void is answered with `FlightOrderCancelled` (the airline's void window had closed) | The item is `Cancelled`; no refund to make, the traveller was never charged | `SagaBranchTests.A_flight_void_answered_as_a_cancellation_closes_the_item_because_the_void_window_had_passed` |
| A release for a hold the owner never placed is answered `Released` | The speculative release sent to a `Pending` item completes it | `Trip.UnwindItem`, `SagaBranchTests.Reaching_the_deadline_while_holding_unwinds_and_a_hold_that_arrives_later_is_released` |
| A cancel for an order Flights never made gets no answer | The cancellation times out after 2 minutes, can be asked again, and a late answer is still taken | `CancellationTests.A_cancel_its_owner_never_answers_times_out_can_be_asked_again_and_a_late_answer_still_counts` |

### The deadline

On checkout the trip records `deadline = min(expiries) - 2 minutes`. An item that has not reported counts as expiring
15 minutes after the checkout began; as holds and orders report, their real expiry or ticketing time limit replaces
the guess. A background service (`TripCheckService`) asks every 5 seconds for trips whose `next_check_at` has passed
and runs each in its own scope and transaction. A trip in `Holding`, `Authorizing` or `Ticketing` at its deadline goes to
`Unwinding` with `DeadlineReached`; one that has started `Committing` does not, because its flights are ticketed.

(`DeadlineTests`: unwinding at each of the three stages with the late answer compensated, no unwinding while committing
or capturing, a capture answered five hours late still confirming, and a deadline that follows the airline's shorter
ticketing limit.)

### Unwinding

`Unwinding` sends a release, void, cancel or payment void for everything held, ordered, committed or authorised, and
waits for each answer. It does not guess. A flight order that is not known yet cannot be voided by id (Flights decides
the id), so the trip waits for `FlightOrderCreated` or `FlightOrderRejected`. A payment authorisation that was asked
for and not answered is waited for too. A commit that was asked for and not answered is waited for, because owners
ignore a release for a hold being committed. Every 30 seconds an unanswered release, void or cancel is sent again; they
are idempotent at the owners (`FailureTests.An_unanswered_release_is_sent_again_after_the_resend_interval`).

### Concurrency

A trip's answers come on four topics, so four consumers can reach one trip at once. Every handler takes a row lock on
the trip (`select ... for update`) inside the consumer's transaction before it loads it, so they are applied one after
the other against the latest state instead of racing on `xmin` and retrying. Commands from the API lock the same way.
Repeated checkouts sent at the same moment run the saga once
(`CheckoutTests.Checkout_repeated_at_the_same_moment_runs_the_saga_once`, 12 at once).

## Cancellation (BKG-4)

`POST .../items/{itemId}/cancel` records the moment of the request and asks the owner (`StayBookingCancelRequested`,
`CarBookingCancelRequested`, `FlightOrderCancelRequested`). When the owner confirms, the refund is worked out and
sent as one `PaymentRefundRequested` with its own refund id; a refund of zero sends none. The refund uses the policy
copied onto the item from the hold (so a partner who tightens a policy later changes nothing for earlier bookings)
and the moment of the **request**, not of the answer (`CancellationTests.The_refund_is_worked_out_for_the_moment_the_traveller_asked_not_the_moment_the_owner_answered`).

| Item | Refund |
|---|---|
| Stay or car, asked at least `freeUntilHoursBeforeStart` hours before it starts (the boundary is free) | The whole item total |
| Stay or car, after that, penalty `FirstUnit` | The total less one night's (or one day's) share, `total / units` rounded to the cent in the partner's favour |
| Stay or car, after that, penalty `FullAmount` | Nothing |
| Stay or car with no terms on record | Nothing: a refund nobody promised is not Booking's to invent |
| Flight | What `FlightOrderCancelled.Refund` says (the fare rules, as Flights reports them), never more than the item cost; `FlightOrderVoided` for an order that was never ticketed refunds nothing |

A stay or car that has started cannot be cancelled (`409 ItemNotCancellable`). The trip total and the refunded total
follow: `total` is what the non-cancelled items cost, `refunded` what has been given back. When every item is cancelled
the trip is `Cancelled`. The table is `PolicyTableTests` (17 rows for stays and cars including both boundaries and the
rounding rows, nine for rental day counting); the path is `CancellationTests` (integration), which covers the free
window, both penalties, a car, a flight, asking twice, and the owner that never answers.

## Personal data

Decided here (requirements section 12: "Passenger personal data encrypted at rest in Flights and Booking. No tokens,
card numbers, passport data or emails in logs").

- **A draft holds none.** A trip is item references and prices until checkout.
- **At checkout** the contact details, the passenger list and the payment method token are sealed as one blob,
  `trips.sealed_traveller_data`: AES-256-GCM, a fresh 12 byte nonce, the trip id as associated data, under
  `Viatica:Pii:StorageKey`, which only Booking holds. A blob copied onto another trip's row does not open.
- **It lives as long as the saga needs it and no longer.** It is read to build the flight order requests and the
  authorisation, and set to null the moment the trip is `Confirmed` or `Failed`. After that Booking keeps only ids,
  prices and states. The trade: Booking cannot show a traveller who was on a trip, and cannot resend a flight order
  after the saga ends; Flights holds the passenger list, sealed under its own key.
- **On the broker** the passenger list travels as `FlightOrderRequested.EncryptedPassengers`, sealed with
  `Viatica.Kernel.Pii.PassengerSealing` under `Viatica:Pii:PassengerKey` with `{tripId:N}:{itemId:N}` as associated
  data, the format Flights opens. The key is one value in `compose.yaml` (`x-passenger-key`) that both services read.
- **Never in** an event (the stand-in tests read every message Booking put on its topic), a response (a trip view has
  descriptions and prices only), a partner's list (no traveller is named), or a log. The checkout fingerprint that
  tells a repeat from a different request is an HMAC keyed from the storage key, not a plain hash, because a plain hash
  of a name and a date of birth can be guessed.
- **Logs.** Log lines carry ids, states and counts. `TravellerDetails`, `ContactDetails` and `PassengerDetails` print
  as `{ redacted }`, and validation messages never repeat a value.

(`PersonalDataTests`: the blob holds none of the details and opens for its own trip only; it is erased on success and on
failure; the topic carries none; a full run at `Trace` level, including requests refused with the traveller's details in
the body, logs none of the details, either key or the sealed list, with a control that proves the capture would see a
leak.)

## Errors

The codes are in `docs/api/errors.md` under "Booking". Validation uses OrionGuard validators and answers one `400
InvalidRequest` listing every broken field with the code it has, never repeating a value.

## Configuration

| Setting | Default | Meaning |
|---|---|---|
| `ConnectionStrings:Booking` | none | Postgres, role `booking` |
| `Kafka:BootstrapServers` | none | Brokers |
| `Kafka:BookingTopic`, `StaysTopic`, `CarsTopic`, `FlightsTopic`, `PaymentsTopic` | the `*.v1` names | Only the integration tests change them |
| `Viatica:Api:SkipTokenKey` | none | 32+ bytes base64; every instance shares it |
| `Viatica:Pii:PassengerKey` | none | 32 bytes base64, shared with Flights |
| `Viatica:Pii:StorageKey` | none | 32 bytes base64, Booking only |
| `Viatica:Auth:Authority`, `ValidIssuer` | Keycloak | Token validation, audience `booking-api` |
| `Booking:DeadlineMargin` | `00:02:00` | Taken off the shortest expiry |
| `Booking:AssumedHoldLifetime` | `00:15:00` | What an item that has not reported is assumed to last |
| `Booking:ResendAfter` | `00:00:30` | Before an unanswered release, void or cancel is sent again |
| `Booking:CancelTimeout` | `00:02:00` | Before a cancellation nobody answered is marked timed out |
| `Booking:CheckInterval`, `CheckBatchSize` | `00:00:05`, 50 | The scheduled check |
| `Booking:ProgressPollInterval`, `ProgressKeepAlive` | `00:00:00.5`, `00:00:15` | The event stream |

A missing connection string or key stops the host at start-up. `dotnet Viatica.Booking.Api.dll migrate` applies the
migrations and exits; it never runs on start-up.

## Observability

One `ActivitySource` and one `Meter`, both `Viatica.Booking`. `booking.checkouts` (started or repeated),
`booking.sagas` and `booking.saga.duration` (by outcome and failure reason), `booking.compensations` (release, void a
flight, void the payment, cancel a booking), `booking.deadlines.reached` (by the stage it was in),
`booking.events.ignored` (an answer the trip had moved past, by event type and why) and `booking.cancellations`. Traces
cross Kafka in the envelope, so one trace shows a click on "Pay" through holds, ticketing and capture.

## Decisions worth arguing with

**The aggregate returns instructions; it does not send them.** `Trip` references the Kernel and nothing else, and a
method like `OnFlightOrderCreated` returns the list of `SagaDirective` the caller must queue. The cost is a mapping
layer (`DirectivePublisher`). What it buys is that the saga is tested without Kafka or Postgres: `RandomRunTests` runs
it 40,000 times against models of the four owners.

**Answers are taken by what the item is waiting for, not by the event alone.** The item records the one request
that has no answer. That is what makes "the same hold twice", "a commit nobody asked for" and "a late hold" three
different, cheap decisions. The weakness is that an item can wait forever for an owner that never answers; the
scheduled check resends compensations but cannot invent an answer (see Known limits).

**Row lock per trip, not retry on `xmin`.** With four consumers on one trip, optimistic concurrency would turn bursts
(three hold answers landing together) into retries, and the consumer retries five times before dead lettering. The lock
costs a short wait; a dead letter in the middle of a saga costs a stuck trip. `xmin` is still the ETag.

**Refunds use the terms copied at the hold.** `StayHoldPlaced` and `CarHoldPlaced` gained an optional `Terms` (partner,
start, cancellation policy). Booking could have followed `StayListingUpdated` instead, but a listing changes after the
booking, and the policy that applies is the one the traveller saw. This is an additive change to the contracts and to
Stays and Cars (see the PR for the list).

**A first night is the average night.** The hold carries the total and the nights, not each night's rate, so
`FirstUnit` keeps `total / units`. For a stay with a dearer first night the partner loses a little; the alternative
(per night rates on the hold event) is a contract change for a rounding question.

**Check-in is read as UTC.** Properties have no time zone yet (`docs/services/stays.md`, Known limits), so a stay
"starts" at its check-in date and the property's check-in time as UTC, and that is the instant the free window is
measured from. Cars carry an offset in `pickUpAt` and are exact.

**Booking trusts nothing in a quote token and the owner's total is the price.** If the total the owner confirms differs
from the token's, the authorisation is for the owner's total. Flights rejects an order whose total differs from the
traveller's (`PriceChanged`), so the traveller never pays a flight price they did not see.

## Known limits

- **A flight that never answers** keeps an unwinding trip `Unwinding`: a pending order cannot be voided by an id Flights
  has not issued. `FlightOrderCreated` arriving later is voided at once. The runbook "Stuck checkout saga" shows how to
  find such trips.
- **A capture Payments never answers** (it answers a refusal with `PaymentOperationRefused`, but a lost message is lost) leaves the trip `Capturing`.
- **A refused refund is not retried.** The item says `Refused` and the money is owed; there is no automatic second attempt.
- **Dead lettered compensations have no replay tool yet** (`docs/messaging.md`). The resend after 30 seconds covers a
  lost message, not a poisoned one.
- **A stay's start is the check-in instant read as UTC**, and its first night the average night (above).
- **Partners see a booking, not a traveller.** The traveller's details are erased when the saga ends, so there is no
  guest name to show; the partner list gives ids, dates and money.
- **One currency.** Every price is EUR; a quote in another currency is `400 QuoteInvalid`.
- **Inbox rows are never deleted** (`docs/messaging.md`).

## Tests

`dotnet run --project tests/Services/Booking/Viatica.Booking.UnitTests` (no containers) and
`dotnet run --project tests/Services/Booking/Viatica.Booking.IntegrationTests` (needs Docker: Postgres 18 and Kafka; the
real host against stand-ins for Stays, Cars, Flights and Payments that answer over real topics through an outbox).

| Claim | Test |
|---|---|
| Whatever the owners answer, in whatever order, however often, a trip ends confirmed and paid or failed with nothing held, ordered or authorised | `RandomRunTests` (20,000 runs per delivery mode) |
| The order: holds, authorise, tickets, commits, capture | `CheckoutTests.A_trip_with_a_stay_a_car_and_a_flight_is_held_authorised_ticketed_committed_and_captured_in_that_order` |
| Every failure branch ends legally | `SagaBranchTests`, `FailureTests`, `DeadlineTests` |
| The same message twice, the same content under a new event id, answers in any order change nothing | `RedeliveryTests` (unit and integration) |
| A late hold, order or authorisation gets exactly one compensating request | `RedeliveryTests`, `FailureTests` |
| A repeated checkout changes nothing; another body is 409 | `CheckoutTests` |
| Policies for stays, cars and fares | `PolicyTableTests`, `CancellationTests` |
| No personal data at rest after the saga, on the topic or in logs | `PersonalDataTests` |
| Another traveller's or partner's data is invisible | `TenancyTests` |

Tests that were run against a deliberately broken service, and failed as they should: a repeated checkout that runs
the saga again (`CheckoutTests` and `SagaBranchTests`), a late hold that gets a second release
(`RedeliveryTests.A_hold_that_arrives_late...`), an unwinding that does not void an authorisation
(`SagaBranchTests`, `RandomRunTests`), an unwinding that does not give back a committed stay (`SagaBranchTests`,
`RandomRunTests`), a deadline that never fires (`SagaBranchTests.Reaching_the_deadline_while_...`), a late
authorisation that is not voided (`RedeliveryTests`, `RandomRunTests`), an `Owns` that lets any traveller in
(`TenancyTests`), and traveller data kept after the trip is confirmed (`PersonalDataTests`).
