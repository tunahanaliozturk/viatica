# Viatica

A travel search and booking platform built as microservices: stays, flights and rental cars in one
trip, paid once. *Viatica* is the plural of the Latin *viaticum*, what a traveller was given for the
road.

Work in progress. What is being built, and the claims it has to prove, are in
[docs/requirements.md](docs/requirements.md). Milestones and tasks are tracked as GitHub issues.

The short version:

- **All or nothing trips.** A flight, a hotel and a car are confirmed together and charged once, or
  nothing is confirmed and nothing is charged, even when an airline refuses to ticket halfway through.
- **No overselling.** Travellers racing for the last room get exactly as many bookings as there are
  rooms.
- **Supplier search with a deadline.** Flight search answers on time with the slow airlines named,
  instead of waiting for them.
- **A read model that keeps up.** Stays and cars are searched on their own store, and how stale it
  gets is measured.

Built on .NET 11 and C# 15, EF Core, Kafka, OpenSearch, PostgreSQL, Keycloak and Vue, with
[Relevox](https://github.com/tunahanaliozturk/relevox) for commands and queries and
[OrionGuard](https://github.com/tunahanaliozturk/OrionGuard) for validation.

## License

MIT
