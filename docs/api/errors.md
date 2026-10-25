# Error codes

Every error answers in the envelope described in [conventions.md](conventions.md): `error.code` is the contract,
`error.message` is for people. The shared codes (`InvalidRequest`, `NotFound`, `Conflict`, ...) are listed there.
This page lists what each service adds. Each service keeps its own section self-contained.

## Booking

HTTP errors from `Viatica.Booking.Api`. A trip that is someone else's is a `404 TripNotFound`, the same as one that
does not exist, so an id says nothing about whose it is. Failures of the checkout itself are not HTTP errors: the
checkout answers `202` and the failure is on the operation (below).

| Status | Code | When | Test |
|---|---|---|---|
| 404 | `TripNotFound` | No such trip, not yours, or an anonymous trip without its `Trip-Access-Key` | `TenancyTests.Another_traveller_cannot_see_change_start_or_follow_a_trip_and_is_told_it_does_not_exist` |
| 404 | `ItemNotFound` | The item is not on this trip | `CancellationTests.What_cannot_be_cancelled_says_why` |
| 409 | `TripAlreadyExists` | `PUT /trips/{id}` for an id that belongs to someone else | `TenancyTests.An_anonymous_trip_needs_a_key_of_its_own_and_a_taken_id_is_a_conflict` |
| 409 | `TripNotEditable` | Adding or removing an item after checkout began | `ApiSurfaceTests.A_draft_can_lose_an_item_and_a_trip_that_is_checked_out_can_no_longer_be_edited` |
| 409 | `TripFull` | More than 10 items on a trip | `SagaBranchTests.The_draft_rules_hold_an_item_cap_idempotent_adds_and_no_edits_after_checkout` |
| 409 | `ItemConflict` | The item id exists on the trip with other contents | `ApiSurfaceTests.An_item_is_checked_when_it_is_added_and_adding_the_same_one_again_changes_nothing` |
| 409 | `QuoteExpired` | The quote token on an item has expired, when it is added or when the checkout starts. `target`: `items/{id}` | `ApiSurfaceTests.A_quote_that_has_expired_is_a_conflict_and_a_flight_without_an_offer_names_its_missing_fields`, `SagaBranchTests.An_expired_quote_stops_the_checkout_before_anything_is_sent` |
| 409 | `TripEmpty` | Checkout of a trip with no items | `CheckoutTests.An_empty_trip_and_a_trip_that_is_not_yours_cannot_be_checked_out` |
| 409 | `CheckoutConflict` | The trip was checked out already with a different body | `CheckoutTests.The_same_trip_with_a_different_body_is_a_conflict_and_changes_nothing` |
| 409 | `CheckoutInProgress` | Cancelling an item while the checkout is still running or unwinding | `CancellationTests.What_cannot_be_cancelled_says_why` |
| 409 | `ItemNotCancellable` | The trip is a draft or failed, the item is not confirmed, or the stay or rental has started | `CancellationTests.What_cannot_be_cancelled_says_why`, `A_stay_that_has_started_cannot_be_cancelled` |
| 400 | `QuoteInvalid` | The quote token is unreadable, for another kind of item, or not in EUR. `target`: `quoteToken` | `ApiSurfaceTests.A_quote_token_that_is_not_one_for_that_kind_of_item_is_refused` |
| 400 | `AccessKeyRequired` | An anonymous trip without a `Trip-Access-Key` of 22 to 128 characters (listed in `details`) | `TenancyTests.An_anonymous_trip_needs_a_key_of_its_own_and_a_taken_id_is_a_conflict` |

Validation details (`error.details[].code`; `target` is the request field, and a message never repeats a value):

| Code | Target | Rule |
|---|---|---|
| `InvalidTripId`, `InvalidItemId` | `tripId`, `itemId` | Not the empty id |
| `InvalidKind` | `kind` | `Stay`, `Car` or `Flight` |
| `QuoteTokenRequired` | `quoteToken` | A stay or a car needs one, at most 2048 characters |
| `OfferIdRequired`, `ExpectedTotalRequired` | `offerId`, `expectedTotal` | A flight needs the re-priced offer id (at most 64 characters) and a total above zero in EUR |
| `InvalidDescription` | `description` | At most 200 characters |
| `SignInRequired` | `Authorization` | Checkout needs a signed-in traveller |
| `InvalidEmail`, `InvalidPhone` | `contact.email`, `contact.phone` | An address with an `@`, at most 254; a phone of at most 32 characters |
| `InvalidPassengerCount` | `passengers` | 1 to 9 |
| `InvalidPassengerName`, `InvalidBirthDate`, `InvalidDocumentNumber` | `passengers` | Names of up to 100 characters; a birth date between 1900 and today; a document number of up to 40 characters |
| `NoAdult` | `passengers` | At least one adult |
| `PaymentMethodTokenRequired` | `paymentMethodToken` | Present, at most 200 characters |

`ApiSurfaceTests`, `CheckoutTests.A_checkout_that_is_not_valid_names_every_field_and_starts_nothing` and
`ApplicationTests` hold these.

A failed checkout or cancellation is an operation (`GET /operations/{id}`), `status: Failed`, with `error.code`:

| Code | Operation | Meaning |
|---|---|---|
| `ItemRejected` | checkout | An owner refused a hold or an order. The trip is `Failed`, nothing is held, nothing is charged |
| `PaymentDeclined` | checkout | The card was declined |
| `TicketingRefused` | checkout | The airline would not issue tickets |
| `DeadlineReached` | checkout | A hold or ticketing limit was about to lapse, or a commit lost to an owner's sweeper |
| `CancellationTimedOut` | cancellation | The owner did not confirm in time. The item is still the traveller's; ask again |

`FailureTests`, `DeadlineTests` and `CancellationTests.A_cancel_its_owner_never_answers_times_out_can_be_asked_again_and_a_late_answer_still_counts`.

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

## Search

Search is read only and anonymous, so none of these concerns identity. Codes are shared between stay and car search
where the rule is the same. The query string is read first and every problem found is listed, then the rules below
are checked. Tests are in `tests/Services/Search`: `RequestTests` (unit), `StaySearchTests`, `CarSearchTests`,
`AutocompleteTests` and `PagingTests` (integration).

| Status | Code | When | Test |
|---|---|---|---|
| 400 | `OneWayNotAllowed` | A car search names a pick-up and a drop-off location of different partners. `target`: `dropOffLocationId` | `CarSearchTests.A_one_way_rental_is_offered_between_locations_of_one_partner_only` |
| 404 | `LocationNotFound` | `pickUpLocationId` or `dropOffLocationId` names no published location | `CarSearchTests.An_unpublished_location_is_neither_offered_nor_a_valid_pick_up` |
| 400 | `InvalidSkipToken` | A `nextLink` token used with another query, or altered | `PagingTests.A_skiptoken_used_with_another_query_or_altered_is_refused` |

Validation details (`error.details[].code`, under a top-level `InvalidRequest`; `target` is the parameter):

| Code | Rule | Test |
|---|---|---|
| `UnknownParameter` | The endpoint has no such parameter; a misspelt filter is refused, not ignored | `StaySearchTests.Unknown_parameters_and_malformed_values_are_refused_with_every_problem_listed` |
| `ParameterRequired` | `checkIn`, `checkOut`, `pickUpAt` or `dropOffAt` is missing | same |
| `InvalidValue` | A value does not parse (date, number, enumeration), or a parameter is sent twice | same |
| `LocationRequired` | Stays: neither `destination` nor `bbox`. Cars: neither `pickUpLocationId` nor `lat` and `lon` | `RequestTests.A_destination_or_a_box_is_required`, `CarSearchTests.Cars_page_by_keyset_and_a_request_must_name_a_place_and_a_sensible_period` |
| `AmbiguousLocation` | Cars: both `pickUpLocationId` and a point | `RequestTests.A_car_search_needs_a_place_and_a_sensible_period` |
| `DestinationTooLong` | `destination` over 100 characters | `RequestTests` |
| `InvalidBoundingBox` | `bbox` is not `west,south,east,north` in range, or south is not below north | `RequestTests.A_destination_or_a_box_is_required` |
| `InvalidPoint` | `lat` or `lon` out of range, or only one given | `RequestTests` |
| `InvalidRadius` | `radiusKm` zero, negative, or without a point | `RequestTests.A_car_search_needs_a_place_and_a_sensible_period` |
| `CheckInInPast` | `checkIn` before today (one day of grace for time zones ahead of UTC) | `RequestTests.Check_out_must_follow_check_in_and_check_in_cannot_be_in_the_past` |
| `CheckOutBeforeCheckIn` | `checkOut` not after `checkIn`; the same code as Stays | same |
| `StayTooLong` | More than 30 nights; the same code as Stays | `RequestTests.A_stay_is_at_most_thirty_nights`, `StaySearchTests.A_thirty_night_stay_works_and_thirty_one_is_refused` |
| `GuestsOutOfRange` | `guests` not between 1 and 40 | `RequestTests.Guests_rooms_prices_stars_and_sorting_are_checked` |
| `RoomsOutOfRange` | `rooms` not between 1 and 9 | same |
| `InvalidPriceRange` | A negative price, or `minPrice` above `maxPrice` | same |
| `InvalidStars` | `stars` holds a value outside 1 to 5 | same |
| `InvalidAmenities` | More than 20 amenities, or one over 50 characters | `RequestTests` |
| `DistanceNeedsPoint` | `sort=distance` with neither a point nor a `bbox` | `StaySearchTests.Sorting_by_distance_without_a_point_is_a_400_and_a_box_supplies_its_centre` |
| `PickUpInPast` | `pickUpAt` more than a day ago | `RequestTests` |
| `DropOffBeforePickUp` | `dropOffAt` not after `pickUpAt` | `RequestTests.A_car_search_needs_a_place_and_a_sensible_period` |
| `RentalTooLong` | More than 30 rental days | same |
| `InvalidSeats` | `minSeats` not between 1 and 20 | `RequestTests` |
| `InvalidCategory` | `category` is not one to four letters | `RequestTests.A_car_search_needs_a_place_and_a_sensible_period` |
| `QueryTooShort` | Autocomplete `q` under two characters | `AutocompleteTests.Short_text_unknown_kinds_and_unknown_parameters_are_refused` |
| `QueryTooLong` | Autocomplete `q` over 60 characters | `RequestTests.Autocomplete_needs_two_characters_and_a_kind` |
| `InvalidTypes` | Autocomplete `types` holds something other than `city`, `airport`, `property` | same |

## Payments

HTTP errors from `Viatica.Payments.Api`. Tokenising needs a traveller token and reading a payment needs an admin token;
the shared `401` and `403` apply. No answer here ever repeats a card number, a security code or a token
(`NoCardDataStoredOrLoggedTests`).

| Status | Code | When | Test |
|---|---|---|---|
| 400 | `InvalidRequest` | The card is wrong. One detail per broken field, with the codes below. `target` is the request field | `TokenisationTests.A_bad_card_is_a_400_with_every_broken_field_and_without_the_values_in_the_answer` |
| 400 | `PaymentMethodInvalid` | The checks passed and the provider will not take the card, such as a number that is not one of its test cards. `innererror.code` is the provider's code (`incorrect_number`) | `TokenisationTests.A_card_that_passes_our_checks_but_is_not_a_test_card_is_refused_by_the_provider_as_PaymentMethodInvalid` |
| 404 | `PaymentNotFound` | `GET /payments/{paymentId}`: no such payment | `OperationTests.An_unknown_payment_is_a_404_PaymentNotFound_and_only_an_admin_may_look` |
| 503 | `ProviderUnavailable` | Tokenising while the provider cannot be asked. The card is not at fault; try again. `Retry-After: 5` | `TokenisationTests.When_the_provider_is_down_the_answer_is_a_503_that_says_to_try_again_and_no_token_is_made` |

Validation details (`error.details[].code`, `target` is the request field):

| Code | Target | Rule | Test |
|---|---|---|---|
| `InvalidCardNumber` | `number` | 12 to 19 digits, spaces and hyphens allowed, and the Luhn check | `CardTests.A_number_that_is_not_a_card_number_is_refused` |
| `InvalidExpiry` | `expMonth`, `expYear` | Month 1 to 12; year from 2000 to 20 years ahead | `CardTests.Every_problem_is_reported_at_once` |
| `CardExpired` | `expYear` | The card is good through the last day of its expiry month | `CardTests.A_card_that_expired_last_month_is_expired_and_one_that_expires_this_month_is_not`, `TokenisationTests.An_expired_card_is_refused_with_its_own_code` |
| `InvalidCvc` | `cvc` | Three or four digits, when given | `CardTests.A_security_code_is_three_or_four_digits` |

### Reasons on `PaymentDeclined`

An authorisation is answered on `payments.v1`, so a refusal is an event and not an HTTP error. `PaymentDeclined.Reason` is
one of:

| Reason | Meaning | Test |
|---|---|---|
| a provider decline code, such as `generic_decline`, `insufficient_funds` | The provider declined the card. The decline code is used when there is one, the error code when there is not | `OperationTests.A_declining_card_is_answered_with_PaymentDeclined_and_the_provider_code_and_nothing_can_follow` |
| `PaymentMethodInvalid` | The token is not one this service issued. The provider was not asked | `OperationTests.A_token_this_service_never_issued_is_a_decline_and_the_provider_is_not_asked` |
| `InvalidAmount` | The amount is zero or negative, or the currency is not three capital letters | `OperationTests.An_amount_that_is_not_money_is_a_decline_and_the_provider_is_not_asked`, `MoneyConversionTests` |
| `PaymentAlreadyExistsForTrip` | The trip already has a payment under another payment id | `OperationTests.One_trip_has_one_payment_a_second_payment_id_is_declined_and_the_first_is_untouched` |
| `ProviderUnavailable` | The provider could not be reached and nothing was sent: the circuit was open, or it said it was down. Nothing is held | `CircuitBreakerTests.With_the_circuit_open_an_authorisation_is_declined_at_once_without_a_call_and_a_capture_waits_and_happens_once` |

### Reasons on `PaymentOperationRefused`

A capture, void or refund that was not carried out. `Operation` says which, `OperationId` is the payment id for a capture
and a void and the refund id for a refund. A money rule refused it before the provider was asked, or the provider
refused it. Nothing moved.

| Reason | Meaning | Test |
|---|---|---|
| `PaymentNotFound` | No payment with that id on that trip | `OperationTests.Requests_about_a_payment_this_service_has_no_record_of_are_refused_not_lost` |
| `ExceedsAuthorization` | A capture above the authorised amount | `OperationTests.A_capture_above_the_authorisation_is_refused_by_us_and_never_reaches_the_provider` |
| `AlreadyCaptured` | A second capture, or a void of a captured payment (refund it instead) | `OperationTests.A_captured_payment_cannot_be_voided_and_the_provider_is_not_asked` |
| `PaymentVoided` | A capture or a void after a void | `OperationTests.A_void_releases_the_hold_and_a_capture_after_it_is_refused` |
| `NotAuthorized` | The payment was declined, so there is nothing to capture or void | `OperationTests.A_declining_card_is_answered_with_PaymentDeclined_and_the_provider_code_and_nothing_can_follow` |
| `NotCaptured` | A refund before a capture or after a void | `OperationTests.A_refund_before_any_capture_is_refused` |
| `ExceedsCapture` | Refunds would add up to more than was captured | `OperationTests.Refunds_in_parts_add_up_to_the_capture_and_one_more_cent_is_refused_without_a_provider_call` |
| `InvalidAmount`, `CurrencyMismatch` | The amount is not positive or not money, or is in another currency than the payment | `PaymentRulesTests.An_amount_that_is_not_positive_is_refused`, `PaymentRulesTests.Money_in_another_currency_is_refused_not_added_to_euros` |
| a provider error code, such as `payment_intent_unexpected_state` | The provider refused what our rules allowed | `PspGatewayTests.A_request_the_provider_rejects_is_a_refusal` |

`PaymentOperationRefused` is new in `Viatica.Contracts` with this service; the design had an answer for a declined
authorisation and none for these three operations.
