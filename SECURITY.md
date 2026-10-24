# Security policy

## Reporting a vulnerability

Please report it privately, through GitHub's
[private vulnerability reporting](https://github.com/tunahanaliozturk/viatica/security/advisories/new) for this
repository, not in a public issue. Say what you found, how to reproduce it, and what an attacker gains. You will
get an answer within a week, and a fix or a stated decision not to fix within thirty days.

Viatica has no released version yet. Reports are taken against `main`.

## What is in scope

The rules in `docs/requirements.md` are the security claims, and a way around any of them is a vulnerability:

- **Reaching a service without a valid token, or with the wrong audience.** The gateway validates the JWT and
  each service validates it again, accepting only tokens that name its own audience.
- **Seeing or changing someone else's data.** A traveller sees only their own trips. A partner edits only their
  own hotels or rental locations, checked against the resource, not only the role. Cross-tenant requests answer
  `404`, so a different status that reveals an id exists is in scope too.
- **Overselling or double charging.** Holds never exceed allotment under any concurrency, and a trip is charged
  once or not at all.
- **Card data outside Payments, or personal data where it should not be.** Passenger details are encrypted at
  rest in Flights and Booking. A token, card number, passport detail or email address in a log line, a trace or
  an event is in scope.
- **One service reaching another's database.** Each service's Postgres role owns only its own database.
- **A dependency with a licence or provenance nobody checked.** The licence audit in CI reads every package at
  every depth.

## What is not

**The development credentials.** Every password, key and client in `compose.yaml`, `deploy/` and the Keycloak
realm export is a public value for a stack bound to localhost: the Postgres role passwords, the Keycloak admin
and user passwords, the password-grant client `viatica-cli`. They are not secrets, and reporting that they are
committed is not a finding. Using one of them anywhere but a developer machine would be.

**The local stack's missing transport security.** OpenSearch runs with its security plugin disabled, the Aspire
dashboard has no sign-in, and nothing uses TLS. `docs/operations.md` lists this under known limitations.

**Denial of service by volume.** Rate limits per user and per IP are a fairness control, not a defence against a
distributed flood.
