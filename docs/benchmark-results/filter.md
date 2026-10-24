```

BenchmarkDotNet v0.15.8, Windows 11 (10.0.26100.9448/24H2/2024Update/HudsonValley)
Intel Core Ultra 7 255H 2.00GHz, 1 CPU, 16 logical and 16 physical cores
.NET SDK 11.0.100-rc.2.26504.105
  [Host] : .NET 11.0.0 (11.0.0-rc.2.26504.105, 11.0.26.50505), X64 RyuJIT x86-64-v3

Toolchain=InProcessEmitToolchain  

```
| Method             | Mean       | Error     | StdDev   | Ratio | RatioSD | Gen0   | Allocated | Alloc Ratio |
|------------------- |-----------:|----------:|---------:|------:|--------:|-------:|----------:|------------:|
| Simple             |   900.9 ns |  37.92 ns | 111.8 ns |  1.01 |    0.17 | 0.0992 |   1.25 KB |        1.00 |
| Typical            | 3,387.2 ns |  66.88 ns | 157.6 ns |  3.82 |    0.49 | 0.3166 |   3.88 KB |        3.11 |
| Busy               | 7,586.5 ns | 217.15 ns | 623.1 ns |  8.55 |    1.23 | 0.6027 |   7.44 KB |        5.95 |
| WithKeysetPosition | 2,333.5 ns |  44.72 ns | 100.9 ns |  2.63 |    0.33 | 0.2785 |   3.43 KB |        2.74 |
