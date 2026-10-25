# airline-sim

`src/Simulators/Viatica.AirlineSim` plays four airlines behind one supplier-style HTTP API. It is a test double,
not part of the product. The Flights service talks to it the way it would talk to a real airline, and the system
tests use it to make airlines slow, refuse tickets or time out on cue.

## Why it exists

The flight half of the checkout saga is only interesting when an airline misbehaves: it answers late, refuses to
ticket after the money is authorised, or times out on order creation and creates the order anyway. A real NDC
sandbox cannot be told to do any of that when a test needs it, and it does not return the same fares twice, so a
test that passed yesterday can fail today for reasons outside the code. The sim fixes both: everything it sells
comes from a seed, and every failure mode is a switch.

It does not try to look like our own APIs. It speaks PascalCase JSON, NDC flavoured names, its own error shape
and plain RPC-style paths, so the Flights adapter is written against a foreign contract, as it will be in
production.

## The four airlines

Codes, names and ticket prefixes are invented. The codes were checked against public lists of IATA designators
on 2026-10-24 and none of them belonged to a carrier there; if one turns out to be assigned, it is a coincidence
and the code changes.

| Code | Name | Hub | Behaviour by default |
|---|---|---|---|
| `ZC` | Steady Air | AMS | Fast and reliable: p50 60 ms, no injected failures. |
| `YZ` | Molasses Airways | FRA | Slow: p50 1.5 s, log-normal with sigma 0.6, so p99 is about 6 s. |
| `JG` | Fickle Air | MAD | Refuses ticketing on 20% of orders. |
| `ZJ` | Limbo Airlines | IST | Times out on 10% of order creations; half of those create the order anyway. |

Every airline has every knob (below). The four differ only in their defaults, so a test can make Steady Air go
down or Molasses Airways refuse tickets without a fifth airline.

## The network

Forty airports: 34 in Europe and Turkey, plus New York, Toronto, Sao Paulo, Dubai, Singapore and Tokyo. Each
airline flies its hub to every European airport, its hub to three long haul airports picked by the seed, about 8%
of the remaining European pairs point to point, and four core routes that all four airlines fly in both
directions: **IST-LHR, CDG-FCO, AMS-BCN and FRA-JFK**. A search on a core route always fans out to every
behaviour, which is what the flight search deadline tests need.

From the seed (`AirlineSim:Seed`, default `20261024`) the sim generates, per airline and direction: one to three
daily flights (one on long haul), departure times between 06:00 and 21:55 local, flight numbers, block times from
the great circle distance, aircraft, taxes, seats per fare class and fares. The same schedule operates every day.
Times are local standard time; daylight saving is ignored on purpose, so a schedule never shifts by an hour
between two test runs.

Same seed, same network, byte for byte: `RouteNetworkTests.The_same_seed_builds_the_same_network`, and over HTTP
`SearchAndPriceTests.The_same_seed_gives_identical_search_results`. `The_default_network_has_not_moved` pins a hash
of the default network, so a change to the generator that would move every downstream test's flight numbers and
prices fails loudly. Direct flights only; connections are not simulated.

### Fare classes and rules

| Class | Cabin | Fare | Seats per flight | Refund on cancel |
|---|---|---|---|---|
| `K` | Economy | lowest bucket | 15 to 40 | taxes only |
| `M` | Economy | 1.45 x K | 10 to 25 | taxes only |
| `Y` | Economy | 2.2 x K | 6 to 15 | total less 75 EUR per seated passenger |
| `J` | Business | 3.8 x K | 4 to 12 | total less 75 EUR per seated passenger |

The K fare grows with distance and each flight gets its own position in a band around it. Fridays cost 15% more,
Sundays 12% more, Tuesdays and Wednesdays 8% less. Nothing depends on the day a test runs. Children pay 75% of the
adult fare, infants 10% and a fifth of the taxes; infants sit on a lap and take no seat. Prices are in EUR.
Covered by `RouteNetworkTests` and, for refunds, `OrderTests` in both suites.

### Seats

Seats are counted per fare class, per flight, per date, in Postgres (`airlinesim` database, table
`seat_buckets`). A row appears on the first sale of a bucket with the seeded capacity. Taking seats is one
conditional `UPDATE ... SET remaining = remaining - n WHERE ... AND remaining >= n`: Postgres locks the row, a
concurrent buyer waits and re-checks the condition against the new count, so the last seat goes to exactly one
caller. A return order takes both legs in one transaction, in a fixed order so two return orders cannot deadlock,
or takes neither. Void and cancel give the seats back in the same transaction as the status change.

Proved by `OrderTests.Concurrent_orders_for_the_last_seats_of_a_bucket_never_oversell` (40 concurrent orders for 3
seats: exactly 3 created, 37 refused, 0 left) and `A_return_order_takes_seats_on_both_legs_or_on_neither`.

## The API

Base path `/ndc/{airline}`. Bodies are JSON; property names match case-insensitively on the way in.

### Search

```http
POST /ndc/ZC/search
{
  "Origin": "IST", "Destination": "LHR",
  "DepartureDate": "2026-11-14", "ReturnDate": null,
  "Passengers": { "Adults": 1, "Children": 0, "Infants": 0 },
  "Cabin": "Economy"
}
```

```json
{
  "Airline": "ZC",
  "Offers": [
    {
      "OfferId": "eyJhIjoiWkMiLCJjIjoiSyIsInAiOlsxLDAsMF0sImwiOlsiWkMyMzk6MjAyNi0xMS0xNCJdLCJiIjoyMTYuMDAsIngiOjQyLjg4LCJlIjoxNzkyODY1NDkwfQ",
      "ExpiresAt": "2026-10-24T18:11:30+00:00",
      "FareClass": "K", "Cabin": "Economy", "Refundable": false,
      "Price": { "Base": 216.00, "Taxes": 42.88, "Total": 258.88, "Currency": "EUR" },
      "SeatsLeft": 25,
      "Segments": [
        { "FlightNumber": "ZC239", "Origin": "IST", "Destination": "LHR",
          "DepartureLocal": "2026-11-14T14:55:00", "ArrivalLocal": "2026-11-14T15:45:00",
          "DurationMinutes": 230, "Aircraft": "Airbus A321neo" }
      ]
    }
  ]
}
```

One offer per itinerary and fare class with enough seats for the seated passengers, cheapest first. Offers live 15
minutes. Flights leaving within two hours are not sold, and a same day return must leave at least 90 minutes after
the outbound lands. An offer id carries everything the offer promised (airline, class, passenger mix, legs, price,
expiry), so the sim keeps no state per search: search is the hot path of the deadline test, and storing offers
would make the sim's own database the bottleneck.

### Re-price

```http
POST /ndc/ZC/offers/{offerId}/price
```

```json
{ "Status": "Unchanged", "PreviousTotal": 258.88, "Offer": { "OfferId": "...", "Price": { "Total": 258.88 } } }
```

The answer is a new offer with a fresh 15 minutes. `Status` is `Unchanged` or `Changed`. A price changes when the
offered class has sold out and the next class up in the cabin still has seats (the offer comes back in that class,
dearer), or when the `PriceChangeRate` knob fires (5 to 15% dearer). Gone means `410 OFFER_EXPIRED` or
`409 NO_AVAILABILITY` when nothing is left in the cabin.

### Orders

```http
POST /ndc/ZC/orders
{
  "OfferId": "eyJhIjoiWkMi...",
  "IdempotencyKey": "trip-42-item-1",
  "Passengers": [ { "Type": "ADT", "GivenName": "Ayse", "Surname": "Yilmaz", "BirthDate": "1988-04-02" } ]
}
```

`201 Created` with the order:

```json
{
  "BookingReference": "PPT4VZ", "IdempotencyKey": "trip-42-item-1", "Status": "Booked", "FareClass": "K",
  "Passengers": { "Adults": 1, "Children": 0, "Infants": 0 },
  "Price": { "Base": 216.00, "Taxes": 42.88, "Total": 258.88, "Currency": "EUR" },
  "CreatedAt": "2026-10-24T17:56:33Z", "TicketingTimeLimit": "2026-10-25T17:56:33Z",
  "TicketedAt": null, "TicketNumbers": [], "VoidedAt": null, "CancelledAt": null, "RefundAmount": null,
  "Segments": [ { "FlightNumber": "ZC239", "...": "..." } ]
}
```

- Passengers must match the offer's mix, with types that fit their age on the day of travel: `ADT` 12 and over,
  `CHD` 2 to 11, `INF` under 2. The sim validates names and birth dates and then keeps only the counts. Error texts
  and logs never repeat a name or a date.
- **Idempotency.** A second creation with the same key returns the same order with `200`, even after the offer has
  expired. The same key with a different offer is `409 IDEMPOTENCY_KEY_REUSED`. Twelve concurrent creations with
  one key make one order (`Concurrent_creations_with_one_key_make_one_order`).
- The ticketing time limit is 24 hours after creation, or one hour before the first departure if that is sooner.

| Call | Does |
|---|---|
| `GET /ndc/{airline}/orders/{bookingReference}` | Reads an order. |
| `GET /ndc/{airline}/orders?idempotencyKey=...` | Finds an order by our key. This is how Flights learns whether a timed out creation happened. |
| `POST /ndc/{airline}/orders/{ref}/ticket` | Issues one ticket per passenger, 13 digits with the airline's prefix. Refused with `422 TICKETING_REFUSED` by the refusal knob; the decision is made once per order and repeated on retry. `409 TICKETING_TIME_LIMIT_EXPIRED` after the limit. |
| `POST /ndc/{airline}/orders/{ref}/void` | Voids a booked order at any time, or a ticketed one up to 24 hours after ticketing; `409 VOID_WINDOW_CLOSED` after that. Seats go back. |
| `POST /ndc/{airline}/orders/{ref}/cancel` | Cancels a ticketed order and sets `RefundAmount` by the fare rules. A booked order is voided instead (`409 ORDER_NOT_TICKETED`). Seats go back. |

Ticket, void and cancel are idempotent: repeating one on an order already in that state returns the order.

### Errors

Every error is `{"Errors":[{"Code":"...","DescText":"..."}]}`.

| Status | Code | When |
|---|---|---|
| 400 | `INVALID_REQUEST` | A field is missing or out of range. |
| 404 | `UNKNOWN_AIRLINE`, `OFFER_NOT_FOUND`, `ORDER_NOT_FOUND` | |
| 409 | `NO_AVAILABILITY` | Not enough seats left in the class (orders) or the cabin (re-price). |
| 409 | `IDEMPOTENCY_KEY_REUSED`, `ORDER_CLOSED`, `ORDER_NOT_TICKETED`, `VOID_WINDOW_CLOSED`, `TICKETING_TIME_LIMIT_EXPIRED` | |
| 410 | `OFFER_EXPIRED` | The offer's 15 minutes are over. |
| 422 | `TICKETING_REFUSED` | The refusal knob said no. |
| 503 | `SERVICE_UNAVAILABLE` | The airline's outage knob is on. |
| 504 | `UPSTREAM_TIMEOUT` | An injected order creation timeout. The order may still exist: look it up by key. |

## Knobs

| Knob | Effect |
|---|---|
| `LatencyMedianMs`, `LatencySigma` | Log-normal delay added to every call of the airline. A trace shows it as an `airlinesim.injected_latency` span. |
| `Outage` | Every call answers `503` at once. |
| `TicketingRefusalRate` | Share of orders whose ticketing is refused. |
| `OrderTimeoutRate` | Share of order creations that hang for `TimeoutHangMs` and end in `504`. |
| `CreateAnywayRate` | Share of those timeouts that create the order before hanging. |
| `TimeoutHangMs` | How long a timed out creation hangs. Default 30 s, longer than any sane client timeout. |
| `PriceChangeRate` | Share of re-prices that come back 5 to 15% dearer. |

```http
GET   /admin/airlines                     every airline with its live knobs and defaults
PATCH /admin/airlines/JG/knobs            { "TicketingRefusalRate": 1.0 }   fields left out keep their value
POST  /admin/knobs/reset                  every airline back to its defaults and the start of its random stream
```

Each airline draws its random decisions from its own stream seeded from the sim's seed, so the same calls in the
same order see the same delays, refusals and timeouts on every run, and a reset repeats a run from the start.
Concurrent callers interleave their draws; the rates still hold.

Each knob is shown to change behaviour in `BehaviourKnobTests`: the slow airline's median search time against the
fast one's, latency following the knob, outage on and off, the refusal rate over 300 orders (0.3 set, inside 0.22
to 0.38 required), timeout and created-anyway rates over 200 creations, and the two timeout cases by idempotency
key: created anyway and found, or not created, not found and safe to retry.

The admin API is unauthenticated. The sim runs only inside a test stack and must never be exposed outside one.
Knobs live in process memory: run one instance.

## Running it

```bash
docker build -f src/Simulators/Viatica.AirlineSim/Dockerfile -t viatica/airline-sim .
docker run --rm -e ConnectionStrings__AirlineSim="Host=postgres;Database=airlinesim;Username=...;Password=..." viatica/airline-sim migrate
docker run -p 8201:8080 -e ConnectionStrings__AirlineSim="..." viatica/airline-sim
```

`migrate` creates the schema with EF Core's `EnsureCreated` and exits. There are no migrations on purpose: the
sim's data is disposable test state, so a schema change means dropping the database. Locally, `dotnet run --project
src/Simulators/Viatica.AirlineSim` listens on port 8201 with `ConnectionStrings__AirlineSim` set.

Health: `/health/live` and `/health/ready` (ready checks the database). OpenTelemetry: `Viatica.AirlineSim`
ActivitySource and Meter with `airlinesim.injected_latency`, `airlinesim.faults` (by airline and kind) and
`airlinesim.order.transitions`; exported over OTLP when `OTEL_EXPORTER_OTLP_ENDPOINT` is set.

## Limits, known and accepted

- Offer ids are not signed. A caller that edits the price inside one gets the price it wrote. The sim's callers are
  our own services and tests; sign them if that ever changes.
  Unsigned is not unchecked: `OfferToken.Decode` validates the whole token (leg entries, a timestamp
  `DateTimeOffset` can hold, passenger counts a booking allows, non-negative amounts in cents), and anything else is
  `OFFER_NOT_FOUND`, not a 500 (`MalformedOfferTokenTests`).
- A booked order past its ticketing time limit keeps its seats until someone voids it. Real airlines cancel such
  orders on their own; the Flights service voids on every failure path, so the sim does not.
- Daylight saving is ignored and there are no connecting itineraries.
- One instance only, because knobs are in memory.
