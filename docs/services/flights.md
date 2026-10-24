# Flights

Flights sells seats on airlines it does not own. It asks four airlines at once, shows the traveller what came
back inside a fixed deadline, and holds a flight for a trip the way an airline does: as an order with a ticketing
time limit that is ticketed, voided or cancelled when the trip's saga says so.

It is requirements FLT-1 to FLT-5. FLT-6, the airlines themselves, is [airline-sim](../airline-sim.md); Flights
reaches it over HTTP and does not reference it.

| | |
|---|---|
| Port | 8103 (`flights` in compose, `http/flights.http` has every request) |
| Stores | Postgres database `flights` (orders, outbox, inbox), Valkey (offer cache) |
| Publishes | `flights.v1`: `FlightOrderCreated`, `FlightOrderRejected`, `FlightTicketed`, `FlightTicketingRefused`, `FlightOrderVoided`, `FlightOrderCancelled` |
| Consumes | `booking.v1`: `FlightOrderRequested`, `FlightTicketingRequested`, `FlightOrderVoidRequested`, `FlightOrderCancelRequested` (other types on the topic are skipped) |
| Projects | `Viatica.Flights.{Domain,Application,Infrastructure,Api}`, tests in `tests/Services/Flights` |

## What it owns

- **Supplier adapters.** One HTTP client per airline, from configuration (code and base URL). Each has its own
  timeout and its own circuit breaker, and nothing outside `AirlineGateway` knows an airline's field names, status
  codes or error texts. The airlines' contract is `docs/airline-sim.md`.
- **The offer cache.** Search results live in Valkey for as long as the airline's price does and nowhere else.
- **Flight orders.** Our mirror of an order at an airline: `FlightOrder` in the `flights` database, with the
  passenger list sealed.

It does not own prices, fares or availability. The airline decides those, and it can change its mind between a
search and an order, which is why there is a re-price step and why an order request carries the total Booking
expects.

## Endpoints

All need `?api-version=2026-11-01` except health. Search, paging and re-price are open to anyone; the gateway rate
limits them.

| Method and path | Auth | Does |
|---|---|---|
| `POST /flights/searches` | none | Searches every airline. `201` with `Location`, the first page, `searchId`, `expiresAt`, `incompleteSuppliers`, `nextLink` |
| `GET /flights/searches/{searchId}` | none | A page of a search made earlier. `$top`, `$filter`, `$orderby`, `$skiptoken`. Never calls an airline |
| `POST /flights/offers/{offerId}/price` | none | Re-prices an offer with its airline. `200` `Unchanged` or `PriceChanged`, `409 OfferExpired`, `503 SupplierUnavailable` |
| `GET /flights/orders/{orderId}` | admin | An order as an operator sees it, with an `ETag`. No passenger data |
| `GET /health/live`, `GET /health/ready` | none | Ready means Postgres and Valkey answer |

Orders are not created over HTTP. Booking asks for them over Kafka, because the checkout saga is event driven and
every step of it must be repeatable by id.

### Search

```json
POST /flights/searches?api-version=2026-11-01
{ "origin": "IST", "destination": "LHR", "departureDate": "2026-12-01", "returnDate": null,
  "adults": 1, "children": 0, "infants": 0, "cabin": "Economy" }
```

```json
{
  "searchId": "01a16fcf-e3e2-722f-aaf7-802e42f0104b",
  "expiresAt": "2026-10-24T21:42:36+00:00",
  "incompleteSuppliers": [ { "airline": "YZ", "reason": "Timeout" } ],
  "value": [ { "id": "01a16fcfe3dc7e408d1047e1d58e6c6a", "airline": "ZC", "fareClass": "K", "cabin": "Economy",
               "refundable": false, "total": { "amount": "218.88", "currency": "EUR" }, "seatsLeft": 20,
               "expiresAt": "2026-10-24T21:42:37+00:00", "segments": [ { "flightNumber": "ZC241", "...": "..." } ] } ],
  "nextLink": "http://localhost:8103/flights/searches/01a16fcf-...?api-version=2026-11-01&$skiptoken=AQMA..."
}
```

- Sorted by total, then departure, then id, so paging is total. `departure` is the origin airport's clock: the
  airlines send local times with no offset and the platform has no time zone table.
- `reason` is `Timeout` (still working at the deadline, or its own timeout fired), `Error` (answered badly or could
  not be reached) or `CircuitOpen` (it has been failing, so it was not asked). Every page repeats the list.
- If **no** airline answers, the answer is `503 NoSuppliersAvailable` and nothing is cached.
- Passengers: at least one adult, infants not more than adults, nine in all.

## How a search runs

```
POST /flights/searches
  validate dates and passenger mix
  ask ZC, YZ, JG, ZJ at the same time        each call: breaker -> timeout (the deadline) -> HTTP
  wait until all replied or 2.5 s (+150 ms safety net)
  normalise offers, give each an id of ours, sort
  Valkey:  flights:search:{id}   the whole sorted set, TTL = earliest offer expiry
           flights:offer:{id}    one key per offer (airline, airline's own id, passenger mix), TTL = its expiry
  answer with the first page
```

The deadline is enforced twice on purpose. Each airline client carries a timeout of the same length inside its
resilience pipeline, so a slow airline is recorded as a failure by its breaker (an airline that is always too slow
should be skipped like one that always errors). The wait in the handler is the safety net that ends the request
if an adapter ignores cancellation. A late answer is dropped, not cached: nobody saw that offer.

Resilience is `Microsoft.Extensions.Http.Resilience` with a circuit breaker per airline outermost and a timeout
inside it, and **no retry**. A search has no time for a second try, and an order must never be created blindly
twice. Defaults: the breaker opens at 50% failures over 30 s once there are 10 calls, and stays open for 30 s.
Business refusals (409, 410, 422) are not failures.

A search is stored as one value because a page is a filter and a seek over a few dozen offers in memory, using the
same keyset code as every database backed endpoint (`$filter`, `$orderby` and a signed skiptoken). A sorted set
per sort order would answer a page without reading the rest, at the price of a key per order and filters the
server cannot run; at this size one read is cheaper.

## How an order runs

Booking sends `FlightOrderRequested` with the re-priced offer id, the total it expects and the passenger list
sealed with AES-256-GCM (`docs/design.md`, "Personal data on the broker").

```
request in
  item already has an order?          yes: answer again with FlightOrderCreated (if still booked), stop
  offer in the cache and unexpired?   no:  FlightOrderRejected(OfferExpired)
  total equals the expected total?    no:  FlightOrderRejected(PriceChanged)
  passenger list opens and fits?      no:  FlightOrderRejected(InvalidPassengers)
  create the order at the airline, idempotency key = item id
     created                          save FlightOrder, FlightOrderCreated
     refused (sold out / expired)     FlightOrderRejected(SoldOut / OfferExpired)
     unavailable (503, circuit open)  FlightOrderRejected(SupplierUnavailable)      nothing was sent or done
     timed out, 5xx, dropped          look the order up by key
        found                         save FlightOrder, FlightOrderCreated
        no such order                 FlightOrderRejected(SupplierUnavailable)
        lookup failed too             throw: the consumer retries, nothing is decided
```

### Unknown outcomes

This is the part worth arguing with. An airline that times out may have done the work.

| Situation | What Flights does | Why |
|---|---|---|
| Create times out or fails in flight | Looks the order up by our key, then decides | A guess either leaves a seat held for nobody or sells one twice (FLT, section 6) |
| Create gets `503` or the circuit is open | Rejects with `SupplierUnavailable` without a lookup | Nothing was sent, or the airline said it did nothing |
| The lookup also fails | Throws, so the consumer retries with backoff | Same key, so the retry gets the order that exists |
| Ticketing cannot be answered | Answers `FlightTicketingRefused("SupplierUnavailable")` | The saga then voids, and a void works whether or not tickets were issued |
| Void or cancel cannot be answered | Throws and is retried; after the attempt limit the message is dead lettered | These must finish; answering with a guess would leave an order live |
| Void window closed at the airline | Cancels under the fare rules and answers `FlightOrderCancelled` with the refund | FLT-4: void inside the window, cancel with the fare's refund rules after it |
| Cancel for a booked, never ticketed order | Voids it and answers `FlightOrderVoided` | Nothing to refund |
| Void for an order this service has no record of | Answers `FlightOrderVoided` | Nothing is left to release, and the saga is waiting for the word |
| Cancel for an order this service has no record of | Logs an error, answers nothing | A cancel carries a refund, and a refund is not invented |

Everything is idempotent twice over: the consumer's inbox drops the same event id, and the order's state answers a
second request under a new event id with what the first one did. The state machine is `Booked -> Ticketed ->
Cancelled`, `Booked -> Voided`, `Ticketed -> Voided`; every other step is `NotAllowed` and answered from the
current state.

## Personal data

- On the broker: `EncryptedPassengers`, `base64url(nonce || ciphertext || tag)`, key `Viatica:Pii:PassengerKey`,
  associated data `{tripId:N}:{itemId:N}`. A list copied onto another trip's event fails to authenticate
  (`OrderCreationTests.A_passenger_list_copied_from_another_trips_event_does_not_authenticate`). The helper is
  `Viatica.Kernel.Pii` and Booking uses the same one.
- At rest: the same construction under `Viatica:Pii:StorageKey`, which only Flights holds, associated data
  `flight-order:{itemId:N}`. Two keys so that rotating one neither exposes nor strands the other
  (`OrderCreationTests.Passenger_data_is_stored_sealed_under_the_services_own_key`).
- In logs and events: never. `PassengerDetails.ToString()` prints nothing about the person, log lines carry ids,
  airline codes and counts, and a validation problem names a position and a field, never a value
  (`NoPassengerDataInLogsTests`, which searches every log entry of a full run at Trace level, with a control that
  proves the capture would see a leak; `OrderCreationTests.What_the_service_publishes_carries_no_passenger_data`).
- To the airline: names, birth date and passenger type, which is what its API asks for. The document number stays
  with us.

## Configuration

| Key | Default | Meaning |
|---|---|---|
| `ConnectionStrings:Flights` | none | Postgres, role `flights` |
| `ConnectionStrings:Valkey` | none | Valkey |
| `Kafka:BootstrapServers` | none | Brokers |
| `Kafka:BookingTopic`, `Kafka:FlightsTopic` | `booking.v1`, `flights.v1` | Only the integration tests change them |
| `Viatica:Api:SkipTokenKey` | none | 32+ bytes base64; every instance shares it |
| `Viatica:Pii:PassengerKey` | none | 32 bytes base64, shared with Booking |
| `Viatica:Pii:StorageKey` | none | 32 bytes base64, Flights only |
| `Viatica:Auth:Authority`, `ValidIssuer` | Keycloak | Token validation, audience `flights-api` |
| `Flights:SearchDeadline` | `00:00:02.500` | How long a search waits for the airlines |
| `Flights:EmptySearchLifetime` | `00:05:00` | How long a search with no offers stays cached |
| `Flights:Suppliers:Airlines:N:Code`, `BaseUrl` | four simulated airlines | One entry per airline |
| `Flights:Suppliers:RequestTimeout` | `00:00:08` | Longest any call except a search may take |
| `Flights:Suppliers:CircuitBreaker:*` | 0.5, 10, 30 s, 30 s | Failure ratio, minimum calls, sampling window, break duration |

A missing connection string or key stops the host at start-up.

## Observability

One `ActivitySource` and one `Meter`, both `Viatica.Flights`. `flights.supplier.calls` (airline, operation, outcome)
and `flights.supplier.duration` are the per-airline error rate and latency; `flights.searches` and
`flights.search.duration` show how often a search was complete; `flights.search.incomplete` counts airlines left out
by reason; `flights.offers.repriced` by result; `flights.orders` and `flights.order.operations` by outcome.
Traces cross Kafka in the envelope, so one trace shows a request from the saga to the airline call.

## What proves it

Unit tests (141, no containers) and integration tests (62, real Postgres, Kafka and Valkey, the real simulator in
process). Run them with `dotnet run --project tests/Services/Flights/Viatica.Flights.UnitTests` and
`...IntegrationTests` (Docker must be running).

| Claim | Test |
|---|---|
| One airline down and one slow: the answer comes inside the deadline and names both | `FlightSearchTests.One_airline_down_and_one_slow_the_answer_comes_inside_the_deadline_naming_both` |
| The same on a fake clock, with the exact deadline arithmetic | `SearchFanOutTests` |
| A failing airline is skipped without a call once its circuit is open | `CircuitBreakerSearchTests.A_failing_airline_is_skipped_without_a_call_once_its_circuit_is_open`, `AirlineGatewayTests.A_circuit_opens_after_repeated_failures_and_the_airline_is_skipped_without_a_call` |
| A slow airline opens its circuit too; a refusal does not; nothing is retried | `AirlineGatewayTests.Too_slow_counts_as_failing_so_a_slow_airline_opens_its_circuit_too`, `An_airline_saying_no_is_not_an_airline_failing`, `A_failing_call_is_not_retried` |
| Paging never calls the airlines again | `FlightSearchTests.Paging_never_calls_the_airlines_again` (counts the calls that leave) |
| The cache lives as long as the earliest offer | `FlightSearchTests.The_search_and_its_offers_live_exactly_as_long_as_the_earliest_offer` |
| Re-price: unchanged, changed, gone | `RepriceTests` |
| Timeout, created anyway: exactly one order and `FlightOrderCreated` | `TimedOutCreationTests.A_creation_that_timed_out_and_was_made_anyway_ends_with_exactly_one_order_and_FlightOrderCreated` |
| Timeout, not created: rejected, nothing left at the airline, never retried blindly | `TimedOutCreationTests.A_creation_that_timed_out_and_was_not_made_is_rejected_with_nothing_left_at_the_airline` |
| A lookup that fails is not read as "no order" | `TimedOutCreationTests.When_the_lookup_cannot_be_answered_either_nothing_is_decided_and_the_request_is_tried_again` |
| A redelivered request does not order twice | `OrderCreationTests.The_same_event_delivered_twice_and_the_same_request_under_a_new_id_make_one_order` |
| Ticketing, and the airline's refusal | `OrderLifecycleTests.Ticketing_issues_one_ticket_number_per_passenger_and_is_answered_again_when_asked_again`, `An_airline_that_refuses_to_ticket_is_reported_and_the_order_stays_booked_for_the_void` |
| Void, void past the window, cancel | `OrderLifecycleTests` |
| No passenger data in captured logs | `NoPassengerDataInLogsTests` |
| Sealing round trip, tamper and wrong associated data | `Viatica.Kernel.UnitTests/PiiSealerTests` |

## Known limits

- An order the airline made after a lookup said "no such order" is not found again. The simulator never does that;
  a real airline's late creation would leave an order that its own ticketing time limit ends.
- A void or cancel that keeps failing is dead lettered after five attempts and there is no replay tool yet
  (`docs/messaging.md`). Booking's deadline unwinds the trip meanwhile.
- Return trips are priced as one offer and ordered as one order; there are no connecting itineraries because the
  simulator has none.
- `departure` sorts by the origin airport's local clock. That is exact among offers from one origin, which is all a
  search compares.
- Offer ids are ours and unguessable, but anyone can re-price any offer id they hold. Re-price is rate limited at
  the gateway, not here.
- Search state is shared by every Flights instance through Valkey; a Valkey restart drops searches (a client gets
  `SearchExpired` and searches again), by design.
