# 0001: C# 15 union types for outcomes, not for the wire

Status: accepted, 2026-10-24

## Context

Viatica targets .NET 11 and C# 15, which add `union` types and `closed` class hierarchies with exhaustive
`switch` checking. Several places in the design return one of a few outcomes that are not errors: a hold is
placed or the room is sold out; an airline tickets or refuses. Exceptions are wrong for those, and a result object
with a status enum and nullable fields lets a caller read the wrong field.

A spike on SDK `11.0.100-rc.2.26504.105` checked three things: does a union compile and switch exhaustively, does
it work as a Relevox response type, and how does `System.Text.Json` treat it.

## What the spike found

- `public union HoldOutcome(HoldPlaced, SoldOut);` compiles, and a handler returning
  `Task<HoldOutcome>` dispatches through Relevox 1.3.0's generated code like any other response type.
- The compiler demands a `null` arm in a `switch` over a union: a union value can be `default`, holding no case.
  Without the arm the build fails with CS8655 under warnings as errors.
- `JsonSerializer.Serialize(outcome)` writes the case value as it is (`{"holdId":"..."}` or `{"available":10}`),
  with nothing saying which case it was. It cannot be read back into the union without guessing.
- A `closed` class hierarchy switches exhaustively with no default arm and no `null` arm.

## Decision

- Use unions for **in-process outcomes**: handler results and domain method results where every case is a normal
  answer. Callers `switch` over them; the `null` arm throws `UnreachableException`, which documents that a default
  union is a bug.
- Never put a union in an integration event, an HTTP response body, or anything else serialised. The Api layer
  maps each case to a status code and a response record explicitly. Events are plain records (ADR 0002).
- Use a `closed` hierarchy where a state machine's states carry different data. Where states carry no data, an
  enum plus a transition table is simpler and wins.

## Consequences

- Handlers that used to throw for expected outcomes return a union, and the endpoint `switch` is checked by the
  compiler when a case is added.
- Every union `switch` carries one extra arm. That is the price of the feature on this compiler version.
- If a later System.Text.Json adds union discriminators, this ADR is revisited; the wire format would still need
  an explicit decision, not a default.

## Alternatives considered

- **A `OneOf`-style library.** Works today on any C# version, but adds a dependency for something the language now
  does, and its exhaustiveness is a convention rather than a compiler check.
- **Exceptions for sold out and refused.** They are expected outcomes on the hot path of the contention test;
  exceptions there cost time and hide control flow.
- **Result object with an enum.** No compiler help when a case is added, and nullable fields that are only valid
  for some statuses.
