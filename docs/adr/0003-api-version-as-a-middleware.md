# 0003: api-version as a small middleware, not Asp.Versioning

Status: accepted, 2026-10-24

## Context

Requirements section 7 says every request carries `?api-version=2026-11-01`, and a missing or unknown version is a
`400` with code `MissingApiVersionParameter` or `UnsupportedApiVersionValue` in the Microsoft error envelope. Health
probes and the OpenAPI document answer without a version. v1 ships one version per service, and every service is a
minimal API host.

Two things matter more than features here. First, the response: the envelope and those two codes, and nothing in
the platform answers in RFC 9457 ProblemDetails. Second, the default: an endpoint someone forgets to configure must
still require a version, because an unversioned endpoint that clients start calling cannot be versioned later
without breaking them.

## Decision

`Viatica.Kernel.Web` checks the parameter in `ApiVersionMiddleware`, about 60 lines, placed after routing by
`UseViaticaApi()`:

- Every `RouteEndpoint` requires `api-version` unless it carries `.WithoutApiVersion()`. Deny by default.
- The value must match one of `Viatica:Api:SupportedApiVersions` exactly (ordinal). Sent twice is refused.
- A request that matched no route stays a 404, and the router's wrong-method endpoint stays a 405, so a client is
  never told to add a version to a URL that does not exist.
- The error is written straight into the envelope with the two codes from the requirements.

Covered by `VersioningTests` and `ErrorEnvelopeTests.A_wrong_method_is_a_405_not_a_versioning_error`.

## Alternatives

**Asp.Versioning.Http.** The mature choice: version sets, per-version API explorer groups for OpenAPI documents,
sunset and deprecation headers, routing a request to a different handler per version. It lost on the two points
above. Its errors go through `IProblemDetailsService` as ProblemDetails with its own problem types, so we would
write a custom writer to translate them back into our envelope and codes, the reverse of what the kernel exists
for. And versioning is opt-in per endpoint or group through version sets: a group mapped without one is not
versioned, which is the forgotten-endpoint case. It would also be one more dependency in every host, built
against .NET 10 while we run 11 RC2.

**An endpoint filter.** Filters attach per route group, so the default is again opt-in, and they do not run for
endpoints that are not route handlers (`MapHealthChecks`, static files), which makes the exemption list implicit
rather than written down.

## Consequences

- One handler serves every supported version. When a breaking change comes, the choice is a new route or adopting
  Asp.Versioning then; its query parameter name is the same, so clients do not notice the switch.
- No per-version OpenAPI document. With one version that costs nothing.
- Versions are opaque strings. `2026-11-01` is not parsed as a date, so there is no "newest version" logic and no
  defaulting; a client always says which contract it was written against.
