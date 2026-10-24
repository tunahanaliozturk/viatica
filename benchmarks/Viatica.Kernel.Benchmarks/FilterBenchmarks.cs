using BenchmarkDotNet.Attributes;
using Viatica.Kernel.Querying;

namespace Viatica.Kernel.Benchmarks;

/// <summary>Parsing <c>$filter</c> and <c>$orderby</c> into an expression tree, from a short filter to a busy one.</summary>
[MemoryDiagnoser]
public class FilterBenchmarks
{
    private static readonly QueryFields<Stay> Fields = new QueryFields<Stay>()
        .Key("id", static s => s.Id)
        .Field("city", static s => s.City, sortable: true)
        .Field("fromPrice", static s => s.FromPrice, sortable: true)
        .Field("stars", static s => s.Stars, sortable: true)
        .Field("refundable", static s => s.Refundable)
        .Field("availableFrom", static s => s.AvailableFrom, sortable: true);

    [Benchmark(Baseline = true)]
    public object Simple() => Fields.Parse("city eq 'lisbon'", null, 20);

    [Benchmark]
    public object Typical() =>
        Fields.Parse("city eq 'lisbon' and stars ge 3 and fromPrice le 250.00 and refundable eq true", "fromPrice desc", 20);

    [Benchmark]
    public object Busy() => Fields.Parse(
        "city in ('lisbon', 'porto', 'faro', 'braga', 'coimbra') and stars ge 3 and fromPrice ge 50 and fromPrice le 250.00 "
        + "and refundable eq true and availableFrom ge 2026-12-01 and availableFrom lt 2027-01-01",
        "stars desc, fromPrice",
        20);

    [Benchmark]
    public object WithKeysetPosition() => Fields.Parse("city eq 'lisbon'", "fromPrice desc", 20, ["312.50", "4711"]);

    public sealed class Stay
    {
        public int Id { get; set; }

        public string City { get; set; } = "";

        public decimal FromPrice { get; set; }

        public int Stars { get; set; }

        public bool Refundable { get; set; }

        public DateOnly AvailableFrom { get; set; }
    }
}
