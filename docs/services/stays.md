# Stays

The Stays service owns what a partner sells and what a traveller reserves: properties, their room types, the rate
and the number of rooms for every night, and the holds placed on those nights. It answers on port 8101, keeps its
data in the Postgres database `stays`, publishes `stays.v1` and consumes Booking's stay requests from `booking.v1`.

The claim it exists to prove is the second headline of `docs/requirements.md`: for every room type and night,
`sold + held <= allotment`, however many travellers ask at once. `HoldRaceTests.Two_hundred_parallel_holds_for_the_last_ten_rooms_make_exactly_ten`
sends 200 holds for the last 10 rooms and gets 10 holds, 190 `SoldOut` answers, and every night at `held = 10`.

## How the model works

Stays counts rooms, not rooms by number. One row of `room_nights` is one room type on one night:

| Column | Meaning |
|---|---|
| `allotment` | Rooms the partner has put on sale that night |
| `sold` | Rooms of committed stays |
| `held` | Rooms reserved by holds that are neither committed nor given back |
| `rate` | Price of one room that night, in euro |

Every statement that changes `sold` or `held` carries its rule in the `WHERE` clause. A hold is

```sql
update room_nights set held = held + @quantity
 where room_type_id = @room_type and night >= @check_in and night < @check_out
   and allotment - sold - held >= @quantity
```

and the hold stands only if the number of rows it touched equals the number of nights. Otherwise the statement is
rolled back to a savepoint taken just before it, and the request is answered with `StayHoldRejected(SoldOut)`. There
is no read followed by a write anywhere: two travellers racing for the last room are ordered by the row lock, and
the second one's condition is evaluated against the first one's result. A `CHECK` constraint on the table says the
same thing again, so a statement that forgot the condition would fail instead of overselling.
(`SchemaTests.The_database_refuses_a_night_that_sells_more_than_it_has`)

Two mutations of the code were run against the tests to see them fail: a hold that reads the free rooms and then
writes without the condition in its `WHERE`, and a rejection that keeps the nights it had already reserved. The
first fails all three `HoldRaceTests` (the `CHECK` constraint fires), the second fails
`HoldTests.One_short_night_refuses_the_whole_stay_and_leaves_the_other_nights_as_they_were` and
`HoldRaceTests.Overlapping_stays_never_oversell_a_night_and_a_refused_stay_leaves_no_trace`.

The three layers follow `docs/design.md`: `Viatica.Stays.Domain` (properties, room types, nights, holds, quote
tokens; kernel and OrionGuard only), `Viatica.Stays.Application` (one folder per use case, `IStaysDb`, the handlers
for Booking's requests), `Viatica.Stays.Infrastructure` (the DbContext, migrations, Kafka wiring, the sweeper) and
`Viatica.Stays.Api` (endpoints, the only project that references Relevox).

## Endpoints

All requests carry `?api-version=2026-11-01`. Health probes are the only exception. Updates need `If-Match` with the
ETag from the last read: missing is `428`, stale is `412`. The ETag is Postgres `xmin`.

| Method and path | Who | Notes |
|---|---|---|
| `PUT /properties/{id}` | partner | Creates a draft with a client-chosen id. Sent again with the same body it answers `200`; with a different body `409 AlreadyExists`. |
| `PATCH /properties/{id}` | owner | Partial update, `If-Match`. |
| `GET /properties/{id}` | anyone | Published properties; a draft is visible to its partner and to an admin, `404` for everyone else. |
| `GET /properties`, `GET /partner/properties` | anyone, partner | Keyset paged. The partner list shows all of that partner's properties. `$filter` and `$orderby` on `name`, `city`, `countryCode`, `starRating`, `status`, `updatedAt`. |
| `POST /properties/{id}/publish`, `.../unpublish` | owner, admin may unpublish | `If-Match`. Publishing needs a room type (`NoRoomTypes`). |
| `PUT /properties/{id}/room-types/{roomTypeId}` | owner | Creates; same retry rule as the property. |
| `PATCH`, `GET` the same path | owner, anyone | A room type has its own ETag. |
| `POST .../room-types/{id}/inventory:set` | owner | Bulk rate and allotment over `from`..`to` inclusive, up to a year ahead, one transaction. Takes `Repeatability-Request-ID`. |
| `GET .../room-types/{id}/inventory?from=&to=` | owner | The calendar: allotment, sold, held, available, rate. |
| `POST .../room-types/{id}/quotes` | anyone | Nightly breakdown, total and a quote token. |
| `GET /openapi/v1.json` | anyone | The OpenAPI document, like the health probes without an `api-version`. |

Someone else's property answers `404 PropertyNotFound` on every one of these, never `403`, so an id does not tell a
rival that the property exists. (`CatalogueTests.Another_partner_cannot_touch_a_property_and_is_told_it_does_not_exist`
tries nine requests with a rival's token and checks that nothing changed.)

### Quote tokens

`base64url(json) + "." + base64url(HMAC-SHA256)` under `Stays:QuoteKey`, as `docs/design.md` specifies. The payload
pins the property, room type, dates, number of rooms, the total and an expiry 15 minutes ahead. The MAC is checked
first and in constant time, so a forged token is never parsed and cannot learn whether it would have expired. Only a
genuine token past its expiry is `QuoteExpired`. (`QuoteTokensTests`: every single changed character is refused, a
token from another key, another kind or another version is refused, the boundary instant is expired.)

The price is the one in the token. If the partner changes the rate between the quote and the hold, the hold still
carries the quoted total, because that is what "pins the price" means and what Booking will ask Payments to
authorise. (`HoldTests.The_quoted_price_holds_even_if_the_rates_change_before_the_hold`)

## The conversation with Booking

Booking sends its instructions on `booking.v1`, keyed by trip. Stays handles each inside the consumer's transaction,
which commits the state change, the room counts, the outbox rows and the inbox row together or not at all.

| In (`booking.v1`) | Out (`stays.v1`) |
|---|---|
| `StayHoldRequested` | `StayHoldPlaced`, or `StayHoldRejected` with `SoldOut`, `NotBookable`, `QuoteExpired` or `QuoteInvalid`; plus `StayAvailabilityChanged` when placed |
| `StayHoldCommitRequested` | `StayHoldCommitted` |
| `StayHoldReleaseRequested` | `StayHoldReleased`, plus `StayAvailabilityChanged` |
| `StayBookingCancelRequested` | `StayBookingCancelled`, plus `StayAvailabilityChanged` |
| (the sweeper, 15 minutes after a hold was placed) | `StayHoldExpired`, plus `StayAvailabilityChanged` |

Catalogue events are keyed by property: `StayListingUpdated` (a full snapshot, `ListingVersion` up by one per change)
on publish and on every change of a published property, `StayUnpublished` on unpublish, `StayAvailabilityChanged`
(`AvailabilityVersion` up by one per room type) after every calendar edit and every change of the rooms available.

What the tests pin down:

- **Redelivery.** A request whose `HoldId` already has a hold does nothing, whatever its event id. A second hold
  request under one hold id, sent after the first, holds once. (`HoldTests.A_request_sent_twice_under_one_hold_id_holds_once`)
  The same event id arriving twice is the inbox's job and is covered by the messaging tests.
- **Commit and release are idempotent**, and a commit does not turn a released hold into a sale or the other way
  round. (`HoldTests.Commit_turns_held_rooms_into_sold_rooms_once`, `Release_gives_the_rooms_back_once_and_announces_it`)
- **A refused stay leaves nothing behind**: when one night is short, the nights that had enough are put back.
  (`HoldTests.One_short_night_refuses_the_whole_stay_and_leaves_the_other_nights_as_they_were`)
- **Every request is answered once**, placed or rejected. (`HoldRaceTests`, which reads all 200 answers from the topic.)
- **Versions** only go up, with no gaps, in the order the changes committed. (`HoldRaceTests`, `RatesAndQuotesTests.Availability_versions_rise_by_one_per_change_and_each_is_announced`)
- **A hold expires after 15 minutes.** The sweeper gives the rooms back and says so; a hold one second short of its
  expiry is untouched. (`ExpiryTests.A_hold_nobody_commits_is_expired_by_the_sweeper_and_its_rooms_come_back`)

## Errors

The codes are in `docs/api/errors.md` under "Stays". Validation uses OrionGuard validators and answers one `400
InvalidRequest` that lists every broken field, each with the code from the design document where it has one
(`StayTooLong`, `CheckOutBeforeCheckIn`; `OccupancyExceeded` needs the room type, so it is raised by the handler).

## Configuration

| Setting | Meaning |
|---|---|
| `ConnectionStrings:Stays` | Postgres, database `stays` |
| `ConnectionStrings:Valkey` | Where `Repeatability-Request-ID` answers are kept, so a retry landing on another instance finds them |
| `Kafka:BootstrapServers` | Brokers |
| `Kafka:Topics:Stays`, `Kafka:Topics:Booking` | Default `stays.v1` and `booking.v1`; tests give each host topics of its own |
| `Stays:QuoteKey` | Base64, at least 32 bytes, signs quote tokens. Stays only; Cars has its own. A missing key stops the host at start-up |
| `Stays:HoldSweepInterval` | How often the sweeper looks for due holds, default 5 seconds |
| `Viatica:Api:*`, `Viatica:Auth:*` | The shared conventions and token validation (audience `stays-api`) |

`dotnet Viatica.Stays.Api.dll migrate` applies the migrations and exits; it never runs on start-up. Compose does it
with the one-shot `stays-migrate`.

Metrics on the meter `Viatica.Stays`: `stays.holds.placed`, `stays.holds.rejected` (tagged with the reason),
`stays.holds.committed`, `stays.holds.released`, `stays.holds.expired`, `stays.bookings.cancelled` and
`stays.quotes.issued`. The outbox age and consumer lag come from `Viatica.Messaging`.

## Decisions worth arguing with

**One counter row per room type orders the versions, and makes writers of that room type queue.** A projection can only
drop stale updates if `AvailabilityVersion` goes up in the order the changes committed. The version is taken last
in a transaction, with `UPDATE room_type_inventory SET availability_version = availability_version + 1`; the row
lock it takes is held until commit, so the next writer's version is higher and its outbox rows come after. The price
is that holds on one room type are serialised on that row, while holds on different room types are not. The
alternatives were a timestamp (clock skew between instances makes it non-monotonic) and a Postgres sequence (it is
not transactional, so a rolled-back hold would leave a gap and a commit-order inversion would be possible). It lives
in a table of its own, not on `room_types`, because every hold would otherwise change the room type's `xmin` and
fail the ETag of a partner who is editing it at that moment.
(`CatalogueTests.Selling_nights_does_not_disturb_a_room_types_etag`)

**Hold states change with compare-and-set statements, not methods on the entity.** The saga's commit and the
sweeper race on the same row. `UPDATE holds SET status = 'Committed' WHERE id = @id AND status = 'Held' AND
expires_at > @now` says who won in the row count, with no retry loop and no concurrency exception. The cost is that
`Hold` carries no behaviour; the catalogue entities do. (`ManualExpiryTests.Of_many_sweepers_reaching_one_hold_exactly_one_expires_it`:
twelve sweepers, one expiry, the rooms returned once.)

**A hold is committed whenever it is still held, even past `ExpiresAt`.** The sweeper is the only authority on
expiry, and the compare-and-set on the status decides who reached the hold first. The rooms are still reserved
until the sweeper runs, so refusing a late commit would protect nobody and would make Booking unwind a trip whose
flights are already ticketed. Booking's deadline is two minutes inside the expiry, so this edge only matters when
messages are delayed. Cars follows the same rule. (`ExpiryTests.A_commit_past_the_expiry_wins_if_it_reaches_the_hold_before_the_sweeper`)

**Commit sends no `StayAvailabilityChanged`.** The event's own comment says "after any rate edit, hold, commit, release
or expiry", but moving a room from held to sold leaves the number available unchanged, and the projection would
receive the same data under a new version for every confirmed stay. The version does not move on commit.

**A release of a hold this service never placed is answered with `StayHoldReleased`.** Booking is waiting for it, and
"nothing is held under this id" is true. It happens when the hold request itself went to the dead letter topic. The
weakness: if that request is replayed later, the hold is placed and nobody releases it until it expires, which is
15 minutes. (`HoldTests.Releasing_a_hold_that_was_never_placed_still_answers_released`)

**Update is `PATCH`, not `PUT`.** `docs/api/conventions.md` makes `If-Match` mandatory on `PUT` and `PATCH`. Creation is
also a `PUT`, with an id the client chose, and the creating request has no ETag yet. Giving creation `PUT` and
updates `PATCH` keeps both rules without a special case. Cost: a patch cannot set a field back to `null`; none of
them may be.

**Every change to a property or its room types moves the property's ETag.** `ListingVersion` must go up with each
snapshot, and the concurrency token on the property row is what stops two writers from claiming the same number. So
editing a room type changes the property's ETag as well as its own. A partner who adds a room type with the
property's ETag in hand gets `412` on the property's next `PATCH` and must read again.

**A quote for a property that exists but is not published says so (`409 PropertyNotPublished`) instead of `404`.** A
traveller holding a stale link deserves the reason. The cost is that anyone with an id can learn that a draft exists;
the draft's content stays hidden, and ids are not guessable.

**No deletes.** Holds and bookings refer to room types and nights, so removing one would orphan history. A property
leaves sale by being unpublished. A room type can be given zero rooms.

**Prices are euro, `numeric(12,2)`.** Money is a `decimal` in the entities and `Money` at the edges.
That is the v1 scope of `docs/requirements.md`; a second currency means a currency column on the night.

## Known limits

- The sweeper runs in every instance. They cannot double-expire a hold, but each wastes a short query every few
  seconds. A leader lock would save the queries and add a failure mode.
- Holds that are rejected leave no row, so a request redelivered after a rejection is evaluated again and may be
  placed if rooms have been freed in between. Booking treats a late hold as one to release, so the books stay right.
- `UTC today minus one day` is the earliest check-in and the earliest date a rate can be set for, so no time zone is
  told that its own today is in the past. Properties have no time zone yet.
- Holds serialise per room type on the version counter. No throughput claim is made for a single room type yet; the
  race test proves correctness, not speed.

## Tests

`dotnet run --project tests/Services/Stays/Viatica.Stays.UnitTests` (47 tests, no containers) and
`dotnet run --project tests/Services/Stays/Viatica.Stays.IntegrationTests` (68 tests; needs Docker for Postgres 18,
Kafka and Valkey). The integration suite runs the real host against the real migration, with a stand-in for Booking
that writes to `booking.v1` through an outbox. Classes run one at a time, because each starts a whole service and
the Postgres container allows 100 connections.
