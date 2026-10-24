# Changelog

All notable changes to this project are documented here. The format follows
[Keep a Changelog](https://keepachangelog.com/en/1.1.0/), and the project uses
[semantic versioning](https://semver.org/spec/v2.0.0.html).

## [Unreleased]

### Added

- `compose.yaml` with the local infrastructure: PostgreSQL 18 with one database and one least-privilege role per
  service, Kafka 4.4 in KRaft mode with the five service topics and their dead letter twins, OpenSearch 3.9 with
  a 1 GB heap, Valkey 8.1, Keycloak 26.8 with the `viatica` realm, and the Aspire dashboard as the OTLP sink.
  Every long-running container but the distroless Aspire dashboard has a health check, so
  `docker compose up -d --wait` returns once the stack is usable.
- `deploy/check-stack.sh`, run by a new `compose` CI job, which signs in to each database as its service, checks
  the topics, and decodes real Keycloak tokens for their audiences and the `partnerId` claim.
- `docs/operations.md` with the local stack, how to reset it, and its known limitations.
- `CONTRIBUTING.md`, `SECURITY.md`, and Dependabot for NuGet, GitHub Actions, Docker and npm.
