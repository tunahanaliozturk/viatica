# Kernel benchmarks

What the shared API conventions cost on each request. Measured 2026-10-24 with BenchmarkDotNet 0.15.8 on an Intel
Core Ultra 7 255H laptop (16 cores), Windows 11 24H2, .NET 11.0.0-rc.2, X64 RyuJIT. The raw reports are
[skiptoken.md](skiptoken.md) and [filter.md](filter.md).

```bash
./.dotnet/dotnet.exe run -c Release --project benchmarks/Viatica.Kernel.Benchmarks -- --filter '*'
```

The benchmarks run in-process: BenchmarkDotNet 0.15.8 cannot yet generate a child project for net11.0. A laptop on
battery-managed clocks is noisy, and the standard deviations (5 to 10 percent) say so; read the numbers as orders of
magnitude, not to the nanosecond.

## Skiptoken

| Step | Mean | Allocated |
|---|---:|---:|
| Normalise the query (path and 8 parameters) | 0.70 µs | 2,032 B |
| Encode the next position | 0.63 µs | 304 B |
| Decode and verify an incoming token | 0.74 µs | 176 B |
| Decode a token against the wrong query (rejected) | 0.57 µs | 0 B |

A paged request after the first does one normalisation, one decode and one encode: about 2 µs and 2.5 KB. That is
the HMAC-SHA256 and SHA-256 work plus string building; there is nothing worth caching. The rejected decode is
faster only because the benchmark's wrong query is a shorter string to hash; the MAC comparison itself is constant
time (`CryptographicOperations.FixedTimeEquals`).

## `$filter` and `$orderby`

| Input | Mean | Allocated |
|---|---:|---:|
| `city eq 'lisbon'` | 0.9 µs | 1.25 KB |
| four conditions and an ordering | 3.4 µs | 3.9 KB |
| an `in` list of five, six more conditions, two sort fields | 7.6 µs | 7.4 KB |
| one condition, an ordering and a keyset position | 2.3 µs | 3.4 KB |

Parsing builds an expression tree, which EF Core then translates (and caches by shape, since every value is a
parameter). Even the busy case is under 10 µs, well below the cost of the database round trip it precedes, so the
parser is not a target for optimisation.
