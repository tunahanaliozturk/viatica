# 0006: Settle a provider timeout by lookup, and resend only under the same key

Status: accepted, 2026-10-25

## Context

A payment provider can take the money and then fail to say so. A capture that times out may have captured. PAY-3 says
such a timeout is resolved by asking the provider for the operation's outcome, never by retrying blindly, and the
requirements add that every request gets exactly one outcome event per operation id, whatever happens in between.

Four ways to handle a call that got no answer were on the table.

| Option | What goes wrong |
|---|---|
| Treat a timeout as a failure and answer "declined" or "refused" | The money may have moved. A capture reported as failed leaves a confirmed trip unpaid, or a cancelled trip refunded twice when Booking tries again |
| Retry the call with the same key, in the transport | It is safe at the provider, but the decision to repeat is hidden in a resilience pipeline, a retry on a 500 that never reached the provider looks the same as one that did, and nothing records that a doubt was settled |
| Look the operation up and decide on the answer; if the lookup finds nothing, say it did not happen | The first copy may still be on its way and land after the lookup, so "nothing recorded" is not yet "will never be recorded" |
| Look it up; if nothing is recorded, send it again under the same key; if that cannot be answered either, decide nothing | Needs a provider that deduplicates by key and can be asked by key |

## Decision

The last one, in `ProviderSettlement` (`docs/services/payments.md`, "Unknown outcomes").

1. A call that was certainly not sent (an open circuit, a `503`) is not in doubt and is not looked up.
2. A call that may have been carried out (a timeout, a dropped connection, any other `5xx`) is looked up by its
   idempotency key. A recorded answer is the outcome, whether it was a yes or a no.
3. If nothing is recorded, the call is sent once more under the same key. With one key the provider treats a late
   first copy and the second as the same operation, so the pair cannot take the money twice.
4. If the lookup fails, or the second call fails, the handler throws. Nothing is decided and no event is published. The
   consumer tries the whole request again, which starts at the same key.

The key is `{tripId}:{kind}:{operationId}` and has no other input, so a redelivery, a retry after a timeout and a
restart between the provider's answer and our commit all reach the same operation. That last case needs no code of its
own: a request with no record here calls the provider again under the same key and receives the answer already
recorded.

An open circuit is not in doubt, but what to do about it differs by operation. An authorisation is declined at once
with `ProviderUnavailable`: nothing is held, and the saga is waiting with holds that expire. A capture, a void and a
refund throw and are tried again, because there is no true thing to tell Booking until the provider is back, and
"not captured" would be a claim nobody checked. The consumer's attempts are raised from five to eight with a longer
backoff (about a minute) so that a break of the circuit does not park the message.

A repeat of a concluded request produces no second event. The outcome is committed with the operation record, so the
first one cannot have been lost, and the requirement is one outcome per operation id. A refusal by a money rule or by
the provider is an outcome too, so `PaymentOperationRefused` was added to `Viatica.Contracts`.

## Consequences

- The provider must deduplicate by key and be askable by key. Stripe does the first and has no lookup, so the adapter
  would find the operation another way (a list filtered by our own reference, kept in the metadata). The simulator has
  the lookup (`docs/psp-sim.md`). Only `PspGateway` knows.
- A request the provider cannot settle for more than about a minute is parked on `booking.v1.dlq`. A replay is safe,
  and there is no replay tool yet (`docs/messaging.md`).
- An attempt that cannot settle holds the consumer thread for up to three provider timeouts (the call, the lookup and
  the resend). A provider that is slow rather than down can hold the consumer, and every trip behind it, for a while.
  A second instance of the service takes half of the partitions, which narrows it.

## Evidence

`TimeoutTests` and `SettlementTests` cover each branch above and count the calls that reached the provider.
`IdempotencyTests` cover redelivery and the crash. Each was shown to fail when the code it protects is broken on
purpose: the lookup ignored, a failed lookup read as "nothing recorded", a key with a random part, the operation record
not checked. The list is in the pull request.
