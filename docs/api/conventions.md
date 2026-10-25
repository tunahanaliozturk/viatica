# API conventions

Every Viatica service answers the same way, because the rules live once in `Viatica.Kernel` and
`Viatica.Kernel.Web` rather than in each service. This page is for two readers: people calling the API, and people
writing a service. Each rule names the test that holds it in place; the tests are in
`tests/Viatica.Kernel.UnitTests` unless another project is named.

The rules follow the [Microsoft REST API Guidelines](https://github.com/microsoft/api-guidelines) and, for
repeatable requests, the Azure guidelines that extend them.

## Versioning

Every request carries `api-version`:

```http
GET /stays/search?destination=lisbon&api-version=2026-11-01
```

Leave it out and the answer is:

```http
HTTP/1.1 400 Bad Request
Content-Type: application/json; charset=utf-8

{
  "error": {
    "code": "MissingApiVersionParameter",
    "message": "The api-version query parameter is required. Supported versions: 2026-11-01.",
    "target": "api-version"
  }
}
```

A version the service does not know, or the parameter sent twice, is `400 UnsupportedApiVersionValue`. Versions are
compared exactly; there is no default and no "latest". Health probes and the OpenAPI document are the only
endpoints that answer without a version. ADR 0003 explains why this is a middleware and not Asp.Versioning.
(`VersioningTests`)

## Errors

Every response outside 2xx has the same body, whatever produced it: application code, validation, the framework
(unknown route, wrong method, wrong content type, failed authentication), or an unhandled exception.

```json
{
  "error": {
    "code": "InvalidRequest",
    "message": "The request is invalid.",
    "target": "trip",
    "details": [
      { "code": "CheckOutBeforeCheckIn", "message": "checkOut must be after checkIn.", "target": "items[0].checkOut" }
    ],
    "innererror": { "code": "StayTooLong" }
  }
}
```

`code` is the contract: switch on it. `message` is for people and may change. `target`, `details` and `innererror`
appear only when they say something. Nothing ever answers in ProblemDetails, and nothing answers an error with an
empty body. (`ErrorEnvelopeTests`)

### Shared codes

| Status | Code | When |
|---|---|---|
| 400 | `InvalidRequest` | Validation failed (with one detail per field), a body would not bind, a header is malformed |
| 400 | `MissingApiVersionParameter`, `UnsupportedApiVersionValue` | See versioning |
| 400 | `InvalidFilter`, `InvalidOrderBy`, `InvalidSkipToken` | See collections |
| 401 | `Unauthorized` | No valid token |
| 403 | `Forbidden` | Token valid, operation not allowed |
| 404 | `NotFound` | No such resource, or one the caller may not know exists |
| 405 | `MethodNotAllowed` | The route exists, the method does not |
| 409 | `Conflict` | The request clashes with current state; services usually use a more specific code |
| 412 | `PreconditionFailed` | `If-Match` does not match the current version |
| 415 | `UnsupportedMediaType` | The body is not `application/json` |
| 428 | `PreconditionRequired` | An update without `If-Match` |
| 429 | `TooManyRequests` | Over a rate limit |
| 500 | `InternalError` | Anything unexpected. The body reveals nothing; the exception is logged with the route pattern |

Service codes, such as `SoldOut` (409) or `HoldExpired`, are listed per service in `docs/api/errors.md`. A
validation detail with no code of its own gets `InvalidValue`.

### For service authors

Throw `ApiErrorException` from a handler; it carries a category, not a status, so the handler stays free of
ASP.NET Core:

```csharp
throw ApiErrorException.Conflict("SoldOut", "No rooms left for those nights.");
throw ApiErrorException.NotFound();
throw ApiErrorException.Domain(ErrorCategory.InvalidRequest, "StayTooLong", "A stay can be at most 30 nights.", "checkOut");
```

Outcomes that are normal answers (a hold placed or not) are return values, not exceptions; see ADR 0001.

## Validation

Commands and queries are validated by OrionGuard validators before the handler runs. `ValidationBehavior` runs
every `IValidator<TRequest>` registered for the request, in order, collects every failure, and ends the request
with one `400 InvalidRequest`. Three broken fields give three details in one response, so a form can mark all of
them at once. (`ErrorEnvelopeTests.Three_invalid_fields_give_one_400_with_three_details`)

OrionGuard's `ValidationError(ParameterName, Message, ErrorCode)` maps onto `target`, `message`, `code`. The target is
put in camelCase segment by segment, so a rule named `Items[0].CheckOut` reports `items[0].checkOut`, the field the
client actually sent. (`ValidationBehaviorTests`)

```csharp
public sealed class PlaceHoldValidator : AbstractValidator<PlaceHoldCommand>
{
    public PlaceHoldValidator()
    {
        RuleFor(static c => c.Rooms is >= 1 and <= 9, "rooms must be between 1 and 9.", nameof(PlaceHoldCommand.Rooms))
            .WithErrorCode("RoomsOutOfRange");
    }
}

// In the host:
services.AddValidator<PlaceHoldCommand, PlaceHoldValidator>();
services.AddRelevox(options => options.AddOpenBehavior(typeof(ValidationBehavior<,>)));
```

Validators run one after another because an async rule may use the request's scoped `DbContext`.

## Collections

Collections are paged on the server with keyset (seek) paging, never offsets:

```http
GET /stays/search?destination=lisbon&$filter=stars ge 3&$orderby=fromPrice desc&$top=20&api-version=2026-11-01
```

```json
{
  "value": [ { "id": "...", "name": "...", "fromPrice": { "amount": "312.00", "currency": "EUR" } } ],
  "nextLink": "https://api.viatica.local/stays/search?destination=lisbon&$filter=stars%20ge%203&$orderby=fromPrice%20desc&$top=20&api-version=2026-11-01&$skiptoken=AQI..."
}
```

- `$top` is the page size: 20 when absent, at most 100. A larger value is clamped, not refused. Zero, negative or
  non-numeric is `400 InvalidRequest`. (`PagingTests.A_top_above_the_maximum_is_clamped_to_100`)
- `nextLink` is absent on the last page. Follow it exactly as returned: it keeps every parameter of the first
  request, including `api-version`. (`PagingTests.Following_next_links_returns_every_match_once_and_keeps_the_query`)
- `$top` may change between pages; nothing else may.

### The skiptoken

`$skiptoken` is opaque. Inside it is the position of the last row of the previous page and an HMAC-SHA256 under a
server key over that position and a SHA-256 hash of the normalised query: the path and every parameter except
`$skiptoken` and `$top`, keys lower cased and sorted. The hash itself is not sent; the server recomputes it from
the request and the MAC fails if anything changed. So:

- a token used with a different filter, ordering, path or api-version is `400 InvalidSkipToken`
  (`PagingTests.A_skiptoken_replayed_against_a_different_query_is_refused`,
  `SkipTokenCodecTests.A_token_replayed_against_another_query_fails`);
- a token with any bit changed is refused, so a client cannot write its own position
  (`SkipTokenCodecTests.Every_flipped_bit_is_detected`);
- the MAC is compared in constant time.
- a token is at most 2,048 characters, which the decoder enforces and the encoder now does too: a position whose
  sort values need more than about 1,500 bytes cannot be paged on, and the request that would have produced the link
  fails with `500 InternalError` instead of returning one that always answers `InvalidSkipToken`
  (`SkipTokenCodecTests.A_position_too_long_for_a_token_is_refused_instead_of_encoded`,
  `PagingTests.A_sort_value_too_long_to_page_on_fails_the_request_instead_of_emitting_a_dead_link`). Bound the length
  of any string a collection sorts on.

The position is not secret, only unforgeable: it holds values the caller already saw on the previous page.

### `$filter`

A small allowlisted grammar. Each endpoint declares which fields can be filtered and with which types; anything
else is `400 InvalidFilter` with a message saying what was wrong and where. Nothing is silently ignored.

```text
filter    := predicate ( "and" predicate )*
predicate := field ( eq | ne | gt | ge | lt | le ) literal
           | field in ( literal, literal, ... )
```

| Field type | Literal | Example |
|---|---|---|
| string | single quotes, `''` for a quote | `city eq 'O''Brien Town'` |
| whole number | `-?[0-9]+` | `stars ge 3` |
| number | `-?[0-9]+(.[0-9]+)?` | `fromPrice le 250.00` |
| boolean | `true`, `false` | `refundable eq true` |
| date | `yyyy-MM-dd` | `checkIn ge 2026-12-01` |
| instant | RFC 3339 with an offset | `updatedAt gt 2026-11-01T05:00:00Z` |
| id | quoted UUID | `partnerId eq '0b5e3c2a-4f1d-4c8e-9a3b-2d7f6e1c0a94'` |
| enumeration | quoted member name | `status eq 'Confirmed'` |

Operators and field names are case sensitive. Booleans, ids and enumerations take `eq`, `ne` and `in` only. There
is no `or`, `not`, function or grouping: each would let a caller build a query the indexes were not designed for.
Limits: 2048 characters, 16 conditions, 50 values in an `in` list. Encode a `+` in an offset as `%2B`; an unencoded
`+` in a URL is a space.

```json
{
  "error": {
    "code": "InvalidFilter",
    "message": "Expected 'and' or the end of the filter at position 17, found 'or'.",
    "target": "$filter"
  }
}
```

(`FilterTests.Refuses_anything_outside_the_grammar_with_a_precise_message`, which also holds the injection
attempts: `or 1 eq 1`, `; DROP TABLE`, comments, functions, stray parentheses.)

Values never become SQL text. Each one reaches EF Core as a parameter, which also keeps one cached query plan per
filter shape. (`EfTranslationTests.Caller_values_are_parameters_never_sql_text`)

### `$orderby`

`field [asc|desc]`, comma separated, at most four, sortable fields only, each once. The endpoint's unique key is
appended when it is not already there, so the ordering is total and paging never skips or repeats a row.
Anything else is `400 InvalidOrderBy`. (`FilterTests.Appends_the_key_so_the_ordering_is_total`,
`FilterTests.Keyset_paging_visits_every_row_exactly_once_in_order`)

### For service authors

Declare the allowlist once per endpoint. Sortable fields must be non-nullable strings, numbers, dates, instants or
ids, because keyset paging compares the last row's values.

```csharp
static readonly QueryFields<StayRow> Fields = new QueryFields<StayRow>()
    .Key("id", static s => s.Id)
    .Field("city", static s => s.City)
    .Field("stars", static s => s.Stars, sortable: true)
    .Field("fromPrice", static s => s.FromPrice, sortable: true)
    .DefaultOrderBy("fromPrice");
```

The endpoint binds the query string; the handler, in Application, applies it. `QueryFields` lives in the kernel,
not in Kernel.Web, for exactly that reason: Application may not reference ASP.NET Core.

```csharp
// Api
app.MapGet("/stays/search", async (HttpContext http, ISender sender, CancellationToken ct) =>
{
    CollectionQuery<StayRow> query = http.BindCollectionQuery(Fields);
    return http.Page(await sender.Send(new SearchStays(query), ct));
});

// Application handler
List<StayRow> rows = await query.ApplyTo(db.StayRows.AsNoTracking()).ToListAsync(ct);
return query.ToPage(rows, static row => new StaySummary(row.Id, row.Name, row.FromPrice));
```

`ApplyTo` filters, seeks past the position, orders and takes `$top + 1` rows; the extra row tells `ToPage` whether
there is a next page without a count query. The row type is an entity or a member-init projection
(`new StayRow { ... }`), which EF Core can filter on; a positional record constructor it cannot.

The translation is checked against the real Npgsql SQL generator: every operator and type, the seek predicate
`a < @p OR (a = @p AND b > @q) OR ...`, and a `Guid` key. (`EfTranslationTests`)

## Concurrency

Mutable resources return a strong `ETag` holding the row version (Postgres `xmin`):

```http
GET /rooms/7?api-version=2026-11-01

HTTP/1.1 200 OK
ETag: "4182"
```

`PUT` and `PATCH` on an existing resource need `If-Match`:

| Request | Answer |
|---|---|
| no `If-Match` | `428 PreconditionRequired` |
| `If-Match: "4182"`, still current | the update, with the new `ETag` |
| `If-Match: "4182"`, changed since | `412 PreconditionFailed` |
| `If-Match: W/"4182"` or a tag this API never issued | `412 PreconditionFailed` (weak tags never match) |
| `If-Match: *` | the update, whatever the version |
| two tags, or an unquoted value | `400 InvalidRequest` |

(`ConcurrencyAndOperationTests`)

```csharp
app.MapPut("/rooms/{id}", async (Guid id, RoomUpdate body, HttpContext http, ISender sender, CancellationToken ct) =>
{
    Room room = await sender.Send(new UpdateRoom(id, body.Name, ExpectedVersion: http.IfMatchVersion()), ct);
    http.Response.SetETag(room.Version);
    return room;
}).RequireIfMatch();

// In the handler: compare, then let EF Core's concurrency token catch a writer that slips in before SaveChanges.
if (command.ExpectedVersion is { } expected && expected != room.Version)
{
    throw ApiErrorException.PreconditionFailed();
}
```

## Repeatable requests

Resources a client creates use `PUT` with a client generated id, so a retry is naturally idempotent. Operations
without a natural id (a refund, a bulk rate update) accept two headers:

```http
POST /payments/7c1e/refunds?api-version=2026-11-01
Repeatability-Request-ID: 5b0f3a8e-1c2d-4e5f-9a6b-7c8d9e0f1a2b
Repeatability-First-Sent: Tue, 01 Dec 2026 14:00:00 GMT
```

| Situation | Answer | `Repeatability-Result` |
|---|---|---|
| first time | runs, and the response is kept | `accepted` |
| same id again, same request, after it finished | the kept response, byte for byte, without running again | `accepted` |
| same id while the first is still running | `409 Conflict` | `rejected` |
| same id, different method, path, query or body | `400 InvalidRequest` | `rejected` |
| `Repeatability-First-Sent` older than the window (24 hours by default) | `400 InvalidRequest` | `rejected` |
| no `Repeatability-Request-ID` | runs normally | none |
| `Repeatability-Request-ID` from a caller without a subject | `401 Unauthorized` | `rejected` |

Ids are scoped to the caller (the token's `sub`): one user's id never reaches another user's kept response. A caller
with no subject has no scope to put an id in, so it is refused instead of sharing one with every other anonymous caller
(`RepeatabilityTests.An_anonymous_caller_cannot_use_repeatability_so_two_of_them_never_share_a_response`).
Domain refusals (`409 RefundWindowClosed`) are kept and replayed like any answer. A 5xx is not kept, and an
unexpected exception releases the id, so the client can retry a failure. (`RepeatabilityTests`)

A running request holds its claim on a lease (`Viatica:Api:RepeatabilityLease`, 30 seconds) and renews it every third
of that until it finishes, so a handler that runs for minutes is never joined by a copy
(`RepeatabilityLeaseTests.A_handler_that_runs_past_its_lease_keeps_its_claim_so_a_copy_is_not_run_beside_it`; it fails
without the renewal). A process that dies stops renewing and its id is free after one lease. Every claim carries an
owner token, and completing, renewing or releasing is checked against it in one step, so a request that did lose its
claim cannot overwrite or delete the next one (`RepeatabilityStoreTests`, and `ValkeyRepeatabilityStoreTests` for the
Lua scripts).

The first request claims the id atomically before it runs, so of any number of identical requests arriving at
once exactly one runs. (`RepeatabilityTests.Of_many_identical_requests_arriving_together_exactly_one_runs` with
32 concurrent copies; the test fails against a check-then-set store.) The claim is `SET key value NX PX` in Valkey
(`ValkeyRepeatabilityStoreTests` in `tests/Viatica.Kernel.IntegrationTests`, against `valkey/valkey:8.1.10-alpine`,
64 concurrent claims, one winner). The in-process default store is only correct for a single instance; a service
with repeatable endpoints registers the Valkey one:

```csharp
builder.Services.AddSingleton<IConnectionMultiplexer>(_ => ConnectionMultiplexer.Connect(valkeyConnectionString));
builder.Services.AddValkeyRepeatabilityStore();

app.MapPost("/payments/{id}/refunds", RefundAsync).WithRepeatability();
```

## Long running operations

Checkout returns `202 Accepted` with an `Operation-Location` to poll, carrying the same `api-version`:

```http
HTTP/1.1 202 Accepted
Operation-Location: https://api.viatica.local/operations/op-1?api-version=2026-11-01

{ "id": "op-1", "status": "NotStarted" }
```

`GET` on that URL returns the operation; `status` is one of `NotStarted`, `Running`, `Succeeded`, `Failed`,
`Canceled`. A failed operation carries `error` in the envelope shape; a succeeded one carries `resourceLocation`.

```json
{ "id": "op-9", "status": "Failed", "error": { "code": "HoldExpired", "message": "The hold on the room expired." } }
```

```csharp
return http.Accepted(new Operation(tripId, OperationStatus.NotStarted), $"/operations/{tripId}");
```

(`ConcurrencyAndOperationTests.A_long_running_operation_answers_202_with_an_operation_location`)

## Representation

- camelCase property names, enums as strings, `null` members left out.
- A request body missing a required member, or carrying `null` for a non-nullable one, does not bind: `400`.
  (`ErrorEnvelopeTests.A_body_that_will_not_bind_is_a_400`)
- Dates as `yyyy-MM-dd`, instants as RFC 3339 in UTC with offset.
- Money as `{ "amount": "312.00", "currency": "EUR" }`. The amount is a string with exactly the currency's ISO 4217
  minor unit digits (two for EUR, none for JPY, three for KWD), so no client parses it into a float. Reading is
  strict: a number, the wrong count of decimals, an unknown or repeated property is refused. Arithmetic across
  currencies throws instead of guessing. (`MoneyTests`)

Services put their own source-generated `JsonSerializerContext` in front of the shared options and give it the same
settings, so generated code and options agree:

```csharp
[JsonSourceGenerationOptions(JsonSerializerDefaults.Web, UseStringEnumConverter = true,
    DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull)]
[JsonSerializable(typeof(PagedResponse<StaySummary>))]
internal sealed partial class StaysJsonContext : JsonSerializerContext;

builder.Services.ConfigureHttpJsonOptions(o => o.SerializerOptions.TypeInfoResolverChain.Insert(0, StaysJsonContext.Default));
```

## Wiring a host

```csharp
WebApplicationBuilder builder = WebApplication.CreateBuilder(args);
builder.AddViaticaApi();                       // reads Viatica:Api, validates it at start-up

WebApplication app = builder.Build();
app.UseViaticaApi();                           // before mapping endpoints
app.MapHealthChecks("/health/ready").WithoutApiVersion();
app.MapOpenApi().WithoutApiVersion();
```

`UseViaticaApi` orders the middleware: the error envelope outermost, then routing, authentication and
authorization when they are registered, the `api-version` check, and repeatability last because it scopes ids to
the authenticated caller. Do not call `UseRouting` or `UseAuthentication` yourself. Rate limiting and anything
endpoint specific go after it.

| Setting | Default | Meaning |
|---|---|---|
| `Viatica:Api:SupportedApiVersions` | none, required | The `api-version` values this service answers |
| `Viatica:Api:SkipTokenKey` | none, required | Base64 of at least 32 random bytes. Every instance shares it; rotating it ends paging sessions in flight |
| `Viatica:Api:RepeatabilityWindow` | `1.00:00:00` | How long a `Repeatability-Request-ID` is remembered |
| `Viatica:Api:RepeatabilityLease` | `00:00:30` | How long the claim of a running request lives between renewals |

A missing or short skiptoken key stops the host at start-up rather than failing the first paged request.
(`ErrorEnvelopeTests.The_host_refuses_to_start_without_a_skiptoken_key`)

`AddViaticaAuth` validates JWTs against Keycloak, which serves its signing keys from `Viatica:Auth:Authority`. Outside
Development that must be an `https` address and metadata is fetched over https only; an http authority stops the host
at start-up (`AuthenticationTests.An_http_authority_outside_development_is_refused_at_startup`). A stack that keeps
Keycloak on a private http network, such as `compose.yaml`, says so with `Viatica:Auth:AllowInsecureMetadata=true`
(`An_http_authority_needs_development_or_an_explicit_opt_in`). An https authority in Production always requires https
metadata (`An_https_authority_in_production_requires_https_metadata`).

## Cost

The skiptoken and filter work on each request is measured in `docs/benchmark-results/`.
