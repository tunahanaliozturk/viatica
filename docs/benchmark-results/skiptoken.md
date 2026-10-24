```

BenchmarkDotNet v0.15.8, Windows 11 (10.0.26100.9448/24H2/2024Update/HudsonValley)
Intel Core Ultra 7 255H 2.00GHz, 1 CPU, 16 logical and 16 physical cores
.NET SDK 11.0.100-rc.2.26504.105
  [Host] : .NET 11.0.0 (11.0.0-rc.2.26504.105, 11.0.26.50505), X64 RyuJIT x86-64-v3

Toolchain=InProcessEmitToolchain  

```
| Method                    | Mean     | Error    | StdDev   | Median   | Gen0   | Allocated |
|-------------------------- |---------:|---------:|---------:|---------:|-------:|----------:|
| Normalize                 | 697.7 ns | 26.09 ns | 76.92 ns | 675.1 ns | 0.1612 |    2032 B |
| Encode                    | 631.7 ns | 12.67 ns | 26.72 ns | 626.3 ns | 0.0238 |     304 B |
| Decode                    | 740.1 ns | 14.66 ns | 34.28 ns | 732.9 ns | 0.0134 |     176 B |
| DecodeAgainstAnotherQuery | 570.9 ns | 11.44 ns | 29.12 ns | 562.1 ns |      - |         - |
