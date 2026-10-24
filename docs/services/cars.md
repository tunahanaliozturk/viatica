# Cars

Cars rents out cars the platform's partners own. A partner loads rental locations, car categories, and a daily rate
and allotment for each category at each location. A traveller gets a priced quote with a token that holds the price
for 15 minutes. Booking then holds one car for the trip, confirms it, gives it back, or cancels it, all over Kafka.

It is requirements CAR-1 to CAR-5. Stays has the same hold shape; the two services share no code, only the contract
in `docs/design.md` ("Quote tokens") and `Viatica.Contracts`.

| | |
|---|---|
| Port | 8102 (`cars` in compose; `http/cars.http` has every request) |
| Stores | Postgres database `cars` (schema `cars`), Valkey (repeatability records for the bulk endpoint) |
| Publishes | `cars.v1`: `CarLocationUpdated`, `CarLocationUnpublished`, `CarAvailabilityChanged`, `CarHoldPlaced`, `CarHoldRejected`, `CarHoldCommitted`, `CarHoldReleased`, `CarHoldExpired`, `CarBookingCancelled` |
| Consumes | `booking.v1`: `CarHoldRequested`, `CarHoldCommitRequested`, `CarHoldReleaseRequested`, `CarBookingCancelRequested` (other types on the topic are skipped) |
| Projects | `Viatica.Cars.{Domain,Application,Infrastructure,Api}`, tests in `tests/Services/Cars` |

## What it owns

- **The catalogue.** Rental locations (address, geo point, optional airport code, opening hours by weekday,
  cancellation policy) and car categories (ACRISS code, name, seats, transmission, fuel, bags, minimum driver age,
  image). Both belong to one partner, and both are `Draft`, `Published` or `Unpublished`.
- **Inventory.** One row per category, location and day: `allotment`, `sold`, `held`, `rate`. The database refuses a
  row where `sold + held > allotment`.
- **Partner pricing.** One flat fee for a one-way rental and one per-day surcharge for young drivers, per partner.
- **Holds.** One row per hold Booking asked for, keyed by Booking's `HoldId`.

It does not own the price a traveller finally pays (the hold event carries it, Booking charges it), cancellation
refunds (Booking evaluates the policy this service publishes), or search (the Search projection is fed by the events).

## Endpoints

All need `?api-version=2026-11-01` except health. Partner endpoints need a `partner` token; the partner is the
token's `partnerId` claim and never a parameter. A resource of another partner answers `404`, not `403`.

| Method and path | Does |
|---|---|
| `PUT /cars/locations/{id}` | Creates or replaces a location under an id the client chose. `If-Match` is required: `*` creates (or replaces whatever is there), an ETag replaces that version only. `201` or `200` with `ETag` |
| `GET /cars/locations/{id}`, `GET /cars/locations` | One location with its `ETag`; the partner's own locations, paged, with `$filter` on `id`, `name`, `status`, `airportCode`, `city` |
| `POST /cars/locations/{id}/publish`, `.../unpublish` | Puts the location on sale or takes it off. Repeating either is safe |
| `PUT/GET /cars/categories/{id}`, `GET /cars/categories`, `POST .../publish`, `.../unpublish` | The same for car categories |
| `GET /cars/partner/settings`, `PUT /cars/partner/settings` | The one-way fee and the young driver surcharge, as `{amount, currency}`. Zeros and no `ETag` until the first save |
| `POST /cars/locations/{l}/categories/{c}/inventory/bulk` | Sets allotment and rate for every day of a range (at most 366 days, at most 365 days ahead) in one transaction. Accepts `Repeatability-Request-ID` |
| `GET /cars/locations/{l}/categories/{c}/inventory?from=&to=` | The calendar: allotment, sold, held, available and rate per day |
| `POST /cars/quotes` | Prices a rental. Open to anyone, because a trip can be built before sign-in (BKG-1) |
| `GET /health/live`, `GET /health/ready` | Ready means the database answers |

## A rental, in numbers

- **Rental days** are the started 24-hour periods from pick-up to drop-off, with 59 minutes of grace. 24 h 59 min is
  one day; one second more is two. At most 30 days. (`Rental_days_are_started_24_hour_periods_with_59_minutes_of_grace`,
  `A_rental_of_30_days_and_one_second_past_the_grace_is_too_long`)
- **Opening hours** apply to the pick-up at the pick-up location and to the drop-off at the drop-off location. A
  window is one open and close time per weekday, in whole minutes, and both ends count: a car handed over in the
  closing minute is still handed over. (`The_opening_and_closing_minutes_are_open`,
  `Pick_up_and_drop_off_must_fall_inside_opening_hours`)
- **Inventory** is counted at the pick-up location on each calendar day the car is out: the pick-up date and the
  days after it, one for each rental day. A one-way drop-off location gives up nothing.
  (`Inventory_is_counted_from_the_pick_up_day_for_each_rental_day`)
- **One way** is allowed only to another location of the same partner (`OneWayNotAllowed` otherwise) and adds the
  partner's flat fee once. (`A_one_way_rental_to_another_location_of_the_same_partner_adds_the_partners_fee`,
  `One_way_to_a_location_of_another_partner_is_not_allowed`)
- **Young drivers.** Under the category's minimum age is `DriverTooYoung`. From the minimum up to 24 the partner's
  per-day surcharge is added for every rental day. 25 and over pay none.
  (`Drivers_between_the_minimum_age_and_25_pay_the_surcharge_for_every_day`)

A quote answers the price line by line: each day's rate, the rental total, the one-way fee, the surcharge, the total.

## The quote token

`base64url(json payload) + "." + base64url(HMAC-SHA256)`, signed with `Viatica:Quotes:SigningKey`, a key only Cars
has. The payload is the Cars one from `docs/design.md`: `kind: "car"`, `locationId`, `categoryId`,
`dropOffLocationId`, `pickUpAt`, `dropOffAt`, `total`, `expiresAt`, plus `driverAge`. The age is there because the
price is for one driver: the token for a 35 year old is not a quote for a 22 year old.

The MAC is compared in constant time before anything in the payload is read. A token that is too long, not two
parts, not base64url, signed by another key, or of another kind is `QuoteInvalid`; one past its 15 minutes is
`QuoteExpired`. (`Changing_any_character_of_the_payload_or_the_signature_is_caught`,
`A_payload_edited_to_a_lower_price_does_not_verify_under_the_old_signature`,
`A_token_is_good_until_the_fifteenth_minute`, `A_stay_quote_signed_with_the_same_key_is_still_not_a_car_quote`)

## Holds

`CarHoldRequested` carries the token and the driver's age. Placing a hold:

1. If a hold with that `HoldId` exists it is answered again (still held or committed) or left alone (it has moved on).
   Booking resending a request does not take a second car. (`Asking_for_the_same_hold_twice_holds_one_car`)
2. The token is verified. An age that differs from the token's is `QuoteInvalid`.
3. The listing must still be on sale, the drop-off location must belong to the same partner, and the category must
   still be published with a minimum age the driver meets. Otherwise `NotBookable`.
4. **One statement takes the car on every day:** `UPDATE ... SET held = held + 1 WHERE <the days> AND allotment - sold
   - held >= 1`. The number of rows it touched must equal the number of rental days. If it is less, some day had no
   car or no row at all, the statement is rolled back to a savepoint, and the answer is `SoldOut`. Nothing is read
   first and written after, so two checkouts cannot both see the last car free.
5. The hold row, the availability event and the answer event commit with the consumer's inbox row in one transaction.

Commit turns `held` into `sold` on every day. Release gives the car back. Cancel gives a sold car back. Expiry is
release after 15 minutes. Each one first saves the hold row, whose Postgres `xmin` is its concurrency token, and
only then moves the inventory, so of a commit and an expiry arriving together exactly one wins and the other finds
the hold moved on. (`Of_a_hold_committed_and_expired_at_the_same_moment_exactly_one_wins_and_the_counters_agree`)

Every request is safe to repeat and every request that no longer applies changes nothing:

| Request | Hold is | Result | Answer |
|---|---|---|---|
| Commit | held | committed now | `CarHoldCommitted` |
| Commit | committed | nothing | `CarHoldCommitted` again |
| Commit | expired, released, cancelled, another trip's, unknown | nothing | none |
| Release | held | released now | `CarHoldReleased` |
| Release | released, expired, unknown | nothing | `CarHoldReleased`, because the car is not held |
| Release | committed | nothing | none |
| Cancel | committed | cancelled now | `CarBookingCancelled` |
| Cancel | cancelled | nothing | `CarBookingCancelled` again |
| Cancel | any other | nothing | none |

(`HoldLifecycleTests`, `BookingFlowTests`)

The outcome of placing a hold is a C# 15 union, `HoldOutcome = Placed | Refused | Superseded`, inside the
Application. The consumer's adapter switches over it and writes the matching event; the union never reaches the wire
(ADR 0001).

The sweeper (`HoldExpirySweeper`) looks every 10 seconds for held holds past their `ExpiresAt` and expires each in its
own transaction, publishing `CarHoldExpired`. Any number of instances can run.
(`The_sweeper_expires_a_hold_after_fifteen_minutes_and_not_before`,
`The_background_sweeper_expires_a_hold_and_tells_booking_without_being_asked`)

## Events out

Listing events are full snapshots keyed by location. `CarLocationUpdated` carries the location and every published
category of its partner, and a `ListingVersion` that goes up with each snapshot and unpublish.
`CarAvailabilityChanged` carries the new numbers for the days that changed, with an `AvailabilityVersion` per
category and location.

The availability version is taken from one row per category and location, upserted with `RETURNING`, after the
inventory change and before the days are read. The row is locked until the transaction ends, so two writers of one
stream are ordered by their commits, and version order is the order the events leave the outbox.
(`Each_change_to_a_category_at_a_location_announces_a_higher_version`,
`Holds_commits_releases_and_cancellations_announce_the_new_numbers_with_rising_versions`,
`Publishing_announces_the_whole_listing_with_a_version_and_every_change_raises_it`)

A commit announces no availability change: held becomes sold and the number of free cars stays the same.

## Errors

`docs/api/errors.md` lists every code, status and the test that holds it.

## Decisions worth arguing with

- **A hold is committed whenever it is still held, even after `ExpiresAt`.** The sweeper is the only authority on
  expiry, and a commit the sweeper has not yet beaten is allowed. The alternative, a clock check in the commit, fails
  a trip at the edge of a few seconds' skew after the payment was authorised. Booking's deadline already keeps two
  minutes clear of the expiry.
- **A refusal is not remembered.** A hold request refused with `SoldOut` and sent again under a new event id is judged
  again, and may now succeed. Booking treats a hold that arrives for a trip that has failed as late and releases it
  (`docs/design.md`, "The checkout saga"). Storing refusals would need a hold row without a rental, or a second table.
- **Times are wall clock in the offset the client sends.** Locations have no time zone. The client sends the pick-up
  and drop-off in the location's own offset, and opening hours and the inventory day are read from that wall clock.
  Elapsed time is absolute. The cost is that the service trusts the offset; the benefit is no time zone database,
  which needs ICU, which this repository switches off (`InvariantGlobalization`).
  (`Opening_hours_are_the_wall_clock_in_the_offset_the_client_sends`, `The_pick_up_day_is_the_locations_calendar_day_not_the_utc_day`)
- **The token pins the driver's age.** A different age on the hold request is `QuoteInvalid`, not a recomputed price.
  The price Booking showed is the price Booking charges.
- **Opening hours live in the location's row as jsonb.** A table of their own would let the hours change without the
  location's `xmin` moving, and the ETag would lie.
- **`PUT` creates with `If-Match: *`.** The kernel requires `If-Match` on every `PUT`; `*` is "whatever is there",
  which for a missing resource means create. A retried create is a replace with the same body.
- **Quotes are anonymous and checked for availability, not reserved.** Reserving is what a hold is for.
- **Admin unpublish is not here.** Requirements section 3 lets an admin unpublish any listing; that waits for the
  gateway and authorisation milestone.

## Configuration

| Key | Meaning |
|---|---|
| `ConnectionStrings:Cars`, `ConnectionStrings:Valkey`, `Kafka:BootstrapServers` | Where the service finds its stores |
| `Viatica:Api:SkipTokenKey` | Base64 of 32 or more bytes, shared by all instances (paging tokens) |
| `Viatica:Quotes:SigningKey` | Base64 of 32 or more bytes, Cars only (quote tokens) |
| `Viatica:Auth:Authority`, `Viatica:Auth:ValidIssuer` | Keycloak; the audience is `cars-api` |
| `Cars:HoldSweeper:Interval`, `Cars:HoldSweeper:BatchSize` | Sweeper timing, default 10 s and 100 |
| `Kafka:Topics:Cars`, `Kafka:Topics:Booking`, `Kafka:ConsumerGroup` | Default `cars.v1`, `booking.v1`, `cars`. Only the tests change them |

`dotnet Viatica.Cars.Api.dll migrate` applies the EF Core migrations and exits; compose runs it as `cars-migrate`.

## Known limits

- Readiness checks the database, not Valkey. With Valkey down the service answers everything but the bulk
  endpoint, which needs it.
- A rejected hold request leaves no row (see above).
- Rate limits belong to the gateway.

## Proof

Unit tests (`tests/Services/Cars/Viatica.Cars.UnitTests`): rental days and the grace period, opening hours, pricing,
age rules, the token. Integration tests (`tests/Services/Cars/Viatica.Cars.IntegrationTests`) run the real host on
Postgres 18, Kafka and Valkey in containers:

| Claim | Test |
|---|---|
| 60 contenders for the last 5 cars give exactly 5 holds | `HoldConcurrencyTests.Sixty_checkouts_racing_for_the_last_five_cars_of_a_day_produce_exactly_five_holds` |
| 300 contenders for 10 cars give exactly 10 holds, 290 refusals, all answered | `HoldConcurrencyTests.Three_hundred_checkouts_racing_for_the_last_ten_cars_of_a_day_produce_exactly_ten_holds` |
| The last car goes to one of 20, 20 times over | `HoldConcurrencyTests.The_last_car_of_a_day_goes_to_exactly_one_of_twenty_contenders_again_and_again` |
| A rental holds all its days or none | `HoldConcurrencyTests.A_rental_holds_every_one_of_its_days_or_none_so_the_scarcest_day_sets_the_winners` |
| `sold + held <= allotment` and sold matches the committed holds after every race | `HoldSupport.AssertInventoryIsConsistentAsync`, called by the tests above |
| A redelivered message is applied once | `BookingFlowTests.The_same_message_delivered_twice_is_applied_once_and_answered_once` |
| Another partner gets 404 and changes nothing | `CatalogueTests.Another_partner_sees_404_and_changes_nothing_on_a_location_that_is_not_theirs` |
| Events carry rising versions | `InventoryTests.Each_change_to_a_category_at_a_location_announces_a_higher_version` |
