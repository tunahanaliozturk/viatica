# 0006: The checkout saga holds, authorises, tickets, commits, then captures

Status: accepted, 2026-10-25

## Context

A trip can hold a stay, a car and a flight, and the traveller pays once. The headline claim is that a trip is either
fully confirmed and charged once, or nothing is confirmed and nothing is charged (`docs/requirements.md`, section 2).
Three kinds of step stand between a click and that end state, and they differ in how they can fail.

| Step | Owner | Can it be refused? |
|---|---|---|
| Hold a stay or a car | Stays, Cars | Yes, when the last unit is gone. Once placed, no: the hold is ours until it expires |
| Order a flight | Flights, the airline | Yes: sold out, price changed, passenger details refused |
| Authorise the card | Payments | Yes: declined. Reversible for free with a void |
| Ticket the order | Flights, the airline | **Yes, at any time before the ticketing time limit**, and an unanswered request is a refusal |
| Commit a stay or a car | Stays, Cars | No, while the hold stands. The one exception is a hold the owner's sweeper took first |
| Capture the card | Payments | No: an authorisation that stands can be captured |

What a failure costs depends on how far the trip got. A hold is released for free. An authorisation is voided for free.
A ticket is voided for free inside the airline's window and costs the fare rules' penalty after it. A committed stay
is a sale that was announced to the partner and the search index; taking it back is a cancellation with a refund. A
captured payment is taken back with a refund, which is slow and shows on the traveller's statement.

## Decision

The saga runs in this order, and the order puts the steps that can still be refused ahead of the steps that cannot:

1. **Hold every item in parallel** (`StayHoldRequested`, `CarHoldRequested`, `FlightOrderRequested`). Any refusal
   ends the checkout; what was held is released.
2. **Authorise the sum of the authoritative totals.** A decline ends the checkout; there is nothing to void.
3. **Ticket the flights.** A refusal ends the checkout; the orders are voided, the holds released, the authorisation voided.
4. **Commit the stays and cars.** This cannot be refused while the hold stands. If an owner's sweeper took a hold
   first, the checkout unwinds, and the flights that were ticketed are voided.
5. **Capture.** Last, so a trip is charged only once everything in it is confirmed.

The state lives on the `Trip` aggregate and its items, as the requirements say. There is no saga engine. The aggregate
is a function from (its state, one cause) to (its new state, the instructions to send next), which Application turns
into outbox rows in the same transaction. Every transition has exactly one cause and replaying that cause is a no-op.

Three rules make that safe under at-least-once, unordered delivery:

- **An answer is taken only if the trip is waiting for it.** Each item records the one request that has no answer
  (`ItemRequest`). A hold placed for an item that is `Held` already, or a commit confirmed for one that was never
  asked to commit, changes nothing.
- **An answer the trip no longer wants is given back, once.** A hold that arrives after the trip gave up on the item
  gets a release; an order, a void; an authorisation, a void. The same late answer again gets nothing, because the
  item already says a release is on its way.
- **Unwinding waits for each owner, and sends again what nobody answered.** A failed trip is `Unwinding` until every
  item is released, voided or cancelled and the payment is voided or was never granted. The scheduled check resends
  an unanswered release, void or cancel after 30 seconds.

The deadline is the shortest hold expiry or ticketing time limit the owners have reported, less two minutes (an item
that has not reported yet counts as expiring 15 minutes after the checkout began, the earliest a hold can lapse). It
is checked by a background service that reads one indexed column, so it fires with no event arriving. It applies
before `Committing`; after that the flights are ticketed and the commits are in flight, so unwinding on a clock would
only race them.

## Consequences

- A refused ticket never has to undo a committed stay or a captured payment. A refused hold never has to undo a ticket.
  `FailureTests.A_refused_ticket_voids_the_order_releases_the_rooms_and_voids_the_authorisation_without_committing_anything`
  asserts that no commit and no capture request exists for such a trip.
- The one path that does undo a commit is a hold lost to the owner's sweeper between the ticket and the commit.
  Stays and Cars commit a hold that is still `Held` even past its expiry, so this needs a commit delayed beyond the
  expiry, which the two minute margin makes rare, not impossible
  (`FailureTests.A_commit_lost_to_the_owners_sweeper_after_the_flight_was_ticketed_unwinds_the_whole_trip`: the stay is
  cancelled, the flight voided, the authorisation voided).
- Holds are taken before the card is touched, so a sold out trip never shows a pending authorisation on a statement.
  The cost is the opposite case: a declined card has already held rooms for a few seconds. They are released at once
  and nobody else could have booked them in that time (`FailureTests.A_declined_card_releases_everything_and_there_is_no_authorisation_to_void`).
- The authorisation can sit on the card for a while: if ticketing takes minutes, the traveller's available balance is
  reduced meanwhile. A void releases it, and the checkout never captures without all the tickets.
- A flight whose order is not known yet cannot be voided by id, because Flights decides the order id. An unwinding trip
  waits for that answer (or for `FlightOrderRejected`) and voids a `FlightOrderCreated` that arrives late. If the airline
  never answers, the trip stays `Unwinding`; the runbook "Stuck checkout saga" says how to see and settle it.
- Capture has no failure event in the contracts, so a capture Payments never answers leaves the trip `Capturing`. The
  deadline does not apply there on purpose: the tickets are issued and the rooms are sold.
- The property that the saga ends legally is checked as a property, not by examples:
  `RandomRunTests` plays 20,000 runs in each of two delivery modes (each owner's answers in order, and any order) over
  owner models that decline, refuse, answer twice and answer late, and asks the owners' books at the end whether
  anything of a failed trip is still held, ordered or authorised, or anything of a confirmed one is not. The check
  stops the clock before an owner's sweeper could clean up after the trip, so a leak shows.

## Alternatives that lost

- **Capture first, book after.** Simplest to reason about, and the worst failure: every refusal after the charge is a
  refund. Refunds cost provider fees, take days, and a traveller sees a charge for a trip they do not have.
- **Commit the stays and cars first, ticket last.** Our holds cannot be refused, so it looks safe, but the airline can
  refuse, and then the committed stays and cars must be cancelled: sales announced to partners and to search, taken
  back, with refunds under each partner's policy. Doing the refusable step first removes that whole compensation path.
- **Ticket and commit in parallel.** Faster by a round trip and no better: a refusal after the commits is the previous
  case, and the compensation graph has one more interleaving per pair of items to test.
- **Authorise after ticketing.** Then a declined card follows issued tickets, and voiding them is only free inside the
  airline's window. An authorisation is free to void at any time, so it goes before the step that costs.
- **A saga engine (MassTransit state machines, Temporal).** Another moving part to run, version and explain, for a
  state machine of ten states that already lives next to the data it changes. State on the aggregate means the decision
  is a pure function that 40,000 random runs exercise in a few seconds, and the transition and its outbox rows commit together.
- **Choreography, no orchestrator.** Each service reacting to the others' events spreads the invariant over four
  codebases and makes "nothing is left held" something nobody can point at. The orchestrator is the one place that
  knows what was asked of whom.
