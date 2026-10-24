# Error codes

Every error answers in the envelope described in [conventions.md](conventions.md): `error.code` is the contract,
`error.message` is for people. The shared codes (`InvalidRequest`, `NotFound`, `Conflict`, ...) are listed there.
This page lists what each service adds. Each service keeps its own section self-contained.

## Cars

HTTP errors from `Viatica.Cars.Api`. Quotes need no token; everything else needs a partner token, and a resource of
another partner is a `404` with the code of what was asked for, so ids do not leak.

| Status | Code | When | Test |
|---|---|---|---|
| 404 | `LocationNotFound` | No such location, not yours, or (for a quote) not published | `CatalogueTests.Another_partner_sees_404_and_changes_nothing_on_a_location_that_is_not_theirs`, `QuoteTests.Listings_that_are_not_published_cannot_be_quoted` |
| 404 | `CategoryNotFound` | No such category, not yours, not published, or not the partner's of that location | `CatalogueTests.Another_partner_sees_404_on_a_category_and_cannot_touch_its_inventory`, `QuoteTests.A_category_of_another_partner_is_not_found_at_this_location` |
| 400 | `OneWayNotAllowed` | The drop-off location belongs to another partner. `target`: `dropOffLocationId` | `QuoteTests.One_way_to_a_location_of_another_partner_is_not_allowed` |
| 400 | `DriverTooYoung` | The driver is under the category's minimum age. `target`: `driverAge` | `QuoteTests.A_driver_under_the_category_minimum_is_too_young` |
| 400 | `LocationClosed` | The pick-up or drop-off is outside opening hours. `target`: `pickUpAt` or `dropOffAt` | `QuoteTests.Pick_up_and_drop_off_must_fall_inside_opening_hours` |
| 400 | `DropOffBeforePickUp` | The drop-off is not after the pick-up | `QuoteTests.A_drop_off_before_the_pick_up_and_a_pick_up_in_the_past_are_refused` |
| 400 | `RentalTooLong` | More than 30 rental days | `QuoteTests.A_rental_longer_than_thirty_days_is_refused` |
| 400 | `PickUpInThePast` | The pick-up is before now | `QuoteTests.A_drop_off_before_the_pick_up_and_a_pick_up_in_the_past_are_refused` |
| 409 | `SoldOut` | Quote: some day of the rental has no car left, or no inventory row | `QuoteTests.A_day_with_no_car_left_makes_the_whole_rental_sold_out`, `QuoteTests.Days_without_any_inventory_row_are_sold_out` |
| 409 | `NotPublishable` | Publishing a location with no opening hours | `CatalogueTests.A_location_without_opening_hours_cannot_be_published` |
| 409 | `AllotmentBelowCommitments` | A bulk update would leave a day with fewer cars than are sold or held. Nothing in the range changes | `InventoryTests.An_allotment_below_what_is_sold_or_held_is_refused_and_no_day_of_the_range_changes` |
| 409 | `Conflict` | Two writers created the same id, or changed the same days, at once. Read and retry | `CatalogueTests.The_same_id_created_by_many_requests_at_once_is_created_once_and_never_a_500` |
| 412, 428 | `PreconditionFailed`, `PreconditionRequired` | `PUT` with a stale `ETag`, or without `If-Match` | `CatalogueTests.Putting_the_same_location_again_is_a_replace_and_a_stale_etag_is_refused`, `CatalogueTests.A_put_without_if_match_is_428_and_an_etag_for_a_resource_that_does_not_exist_is_412` |

Validation details (`error.details[].code`, `target` is the request field):

| Code | Target | Rule |
|---|---|---|
| `InvalidName` | `name` | Not blank, at most 120 characters |
| `InvalidAddress`, `InvalidCountryCode` | `address`, `address.countryCode` | Street, city and postal code present; two letter country |
| `InvalidPosition` | `position` | Latitude -90 to 90, longitude -180 to 180 |
| `InvalidAirportCode` | `airportCode` | Three letters |
| `InvalidOpeningHours` | `openingHours` | At most one window per weekday, closing after opening, whole minutes |
| `InvalidCancellationPolicy` | `cancellationPolicy` | 0 to 8760 free hours, a known penalty |
| `InvalidAcriss` | `acriss` | Four letters |
| `InvalidSeats`, `InvalidBags`, `InvalidMinimumDriverAge` | `seats`, `bags`, `minimumDriverAge` | 1 to 20, 0 to 20, 18 to 99 |
| `InvalidTransmission`, `InvalidFuel`, `InvalidImageUrl` | the field | Known value; image is an absolute http or https URL |
| `InvalidFee` | `oneWayFee`, `youngDriverSurchargePerDay` | EUR, 0 to 10000 |
| `InvalidDateRange`, `DateInThePast`, `DateTooFarAhead` | `to`, `from` | Range of 1 to 366 days, starting no earlier than yesterday, ending no later than 365 days ahead |
| `InvalidAllotment`, `InvalidRate` | `allotment`, `rate` | 0 to 1000; EUR 0 to 10000 |
| `InvalidDriverAge` | `driverAge` | 18 to 99 |
| `InvalidLocationId`, `InvalidCategoryId` | the field | Not an empty id |

### Reasons on `CarHoldRejected`

Hold requests come over Kafka, so their failures are events, not HTTP errors. `CarHoldRejected.Reason` is one of:

| Reason | Meaning | Test |
|---|---|---|
| `SoldOut` | Some day of the rental had no car left; nothing is held | `HoldConcurrencyTests.Sixty_checkouts_racing_for_the_last_five_cars_of_a_day_produce_exactly_five_holds`, `HoldConcurrencyTests.A_missing_day_is_sold_out_not_an_error_and_leaves_the_other_days_alone` |
| `NotBookable` | The location or category is no longer on sale, the drop-off is no longer the same partner's, or the minimum age was raised | `HoldLifecycleTests.A_listing_taken_down_after_the_quote_is_not_bookable`, `HoldLifecycleTests.A_minimum_age_raised_after_the_quote_is_not_bookable` |
| `QuoteExpired` | The token is older than 15 minutes | `HoldLifecycleTests.An_expired_token_is_refused_as_expired` |
| `QuoteInvalid` | The token is not ours, was altered, is of another kind, or was issued for a different driver age | `HoldLifecycleTests.A_tampered_token_is_refused_as_invalid`, `HoldLifecycleTests.A_token_is_only_good_for_the_driver_it_was_quoted_for` |

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
