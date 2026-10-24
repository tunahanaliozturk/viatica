# Error codes

Every error answers in the envelope described in [conventions.md](conventions.md): `error.code` is the contract,
`error.message` is for people. The shared codes (`InvalidRequest`, `NotFound`, `Conflict`, ...) are listed there.
This page lists what each service adds. Each service keeps its own section self-contained.

## Flights

HTTP errors from `Viatica.Flights.Api`. Search, paging and re-price need no token.

| Status | Code | When | Test |
|---|---|---|---|
| 400 | `InvalidRequest` | A search is wrong. One detail per broken field, with the codes below | `FlightSearchTests.A_bad_search_is_a_400_with_every_broken_field_in_the_details` |
| 400 | `InvalidSkipToken` | A `nextLink` token used on another search or another query | `FlightSearchTests.A_skiptoken_from_one_search_is_refused_on_another` |
| 404 | `SearchExpired` | The search id is unknown or its offers have expired. Search again | `FlightSearchTests.A_search_nobody_made_is_gone` |
| 409 | `OfferExpired` | Re-price: the offer expired, the cache no longer has it, or the cabin sold out. Also the answer for an offer id nobody issued | `RepriceTests.An_offer_whose_cabin_sold_out_is_gone` |
| 503 | `NoSuppliersAvailable` | Search: no airline answered. `details` names each airline and why. `Retry-After: 5` | `FlightSearchTests.When_every_airline_is_down_the_answer_is_a_503_that_names_them_all` |
| 503 | `SupplierUnavailable` | Re-price: the airline could not be asked. The offer may still be good. `Retry-After: 5` | `RepriceTests.An_airline_that_cannot_be_asked_is_a_503_and_the_offer_survives_it` |

Validation details on a search (`error.details[].code`, `target` is the request field):

| Code | Target | Rule |
|---|---|---|
| `InvalidAirport` | `origin`, `destination` | Three upper case letters |
| `OriginEqualsDestination` | `destination` | The two differ |
| `DepartureInPast` | `departureDate` | Today or later, UTC calendar day |
| `DepartureTooFar` | `departureDate`, `returnDate` | At most 330 days ahead |
| `ReturnBeforeDeparture` | `returnDate` | Not before the departure |
| `InvalidCabin` | `cabin` | `Economy` or `Business` |
| `AdultRequired` | `adults` | At least one adult |
| `TooManyInfants` | `infants` | Not more infants than adults |
| `TooManyPassengers` | `adults` | Nine travellers in all, infants included |
| `NegativePassengerCount` | `adults` | No negative counts |

A changed price is **not** an error. Re-price answers `200` with `"status": "PriceChanged"`, the new offer and the
`previousTotal`, so the traveller sees both numbers (FLT-3).

### Reasons on `FlightOrderRejected`

Order requests come over Kafka, so their failures are events, not HTTP errors. `FlightOrderRejected.Reason` is one of:

| Reason | Meaning | Test |
|---|---|---|
| `OfferExpired` | The offer is not in the cache any more, or has expired | `OrderCreationTests.An_offer_the_cache_has_forgotten_is_rejected_without_asking_the_airline` |
| `PriceChanged` | The offer's total is not the `ExpectedTotal` Booking sent | `OrderCreationTests.A_price_that_is_not_the_one_booking_expected_is_rejected_as_a_price_change` |
| `SoldOut` | The airline had no seats left in the fare class | `OrderCreationTests.A_flight_that_sold_out_since_the_reprice_is_rejected_as_sold_out` |
| `SupplierUnavailable` | The airline is down, or a timed out creation was looked up and no order exists | `OrderCreationTests.An_airline_that_is_down_is_a_rejection_and_nothing_is_looked_up`, `TimedOutCreationTests.A_creation_that_timed_out_and_was_not_made_is_rejected_with_nothing_left_at_the_airline` |
| `InvalidPassengers` | The sealed list did not authenticate, did not fit the offer, or broke a rule (FLT-5) | `OrderCreationTests.Passengers_that_do_not_fit_the_offer_are_rejected_before_the_airline_is_asked` |

`InvalidPassengers` is new in `Viatica.Contracts` with this service; the design lists it as a Flights error code.

## Stays

Where a code appears in `details[]` of a `400 InvalidRequest`, the `target` is the JSON field of the request.

| Code | Status | When |
|---|---|---|
| `PropertyNotFound` | 404 | No such property, or one the caller may not know exists: a draft seen by anyone but its partner or an admin, or a property of another partner. Never `403`. |
| `RoomTypeNotFound` | 404 | The property exists for the caller but has no such room type. |
| `PropertyNotPublished` | 409 | A quote was asked for a property that exists and is not on sale. |
| `NoRoomTypes` | 409 | Publishing a property that has no room type. |
| `AlreadyExists` | 409 | `PUT` with the id of a property or room type that exists with other values. Change it with `PATCH` and `If-Match`. |
| `SoldOut` | 409 | A quote for a stay in which at least one night has no rate or fewer free rooms than asked for. As a hold outcome the same word is `HoldRejectionReason.SoldOut`. |
| `AllotmentBelowBooked` | 409 | A calendar edit would set a night's allotment below what is already sold or held. The whole edit is refused. |
| `StayTooLong` | 400 | A stay of more than 30 nights. `target`: `checkOut`. |
| `CheckOutBeforeCheckIn` | 400 | `checkOut` is not after `checkIn`. `target`: `checkOut`. |
| `CheckInInPast` | 400 | `checkIn` is before yesterday in UTC. `target`: `checkIn`. |
| `OccupancyExceeded` | 400 | More guests than the requested rooms sleep. `target`: `guests`. |
| `InvalidDateRange` | 400 | A calendar range whose `to` is before its `from`, or, when reading the calendar, longer than 367 nights. |
| `OutsideRateWindow` | 400 | A calendar range that starts in the past or ends more than 365 days ahead. |
| `QuoteExpired` | n/a | Not an HTTP error. A hold request carried a genuine quote token past its 15 minutes; the answer is `StayHoldRejected` with this reason. |
| `QuoteInvalid` | n/a | Not an HTTP error. A hold request carried a token that is not Stays' own, was altered, or does not match the number of rooms asked for; the answer is `StayHoldRejected` with this reason. |

Two refusals use shared codes: a concurrent creation of the same id is `409 Conflict`, and a stale `If-Match` is `412
PreconditionFailed`, as the conventions describe.
