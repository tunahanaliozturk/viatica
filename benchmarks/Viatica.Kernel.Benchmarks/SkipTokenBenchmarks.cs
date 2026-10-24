using BenchmarkDotNet.Attributes;
using Microsoft.AspNetCore.WebUtilities;
using Viatica.Kernel.Web;

namespace Viatica.Kernel.Benchmarks;

/// <summary>
/// The skiptoken work on every paged request: normalise the query, then encode the next position (every page but
/// the last) or decode the incoming one (every page but the first).
/// </summary>
[MemoryDiagnoser]
public class SkipTokenBenchmarks
{
    private readonly SkipTokenCodec codec = new(System.Security.Cryptography.RandomNumberGenerator.GetBytes(32));
    private readonly Dictionary<string, Microsoft.Extensions.Primitives.StringValues> query = QueryHelpers.ParseQuery(
        "?destination=lisbon&checkIn=2026-12-01&checkOut=2026-12-04&guests=2&$filter=stars ge 3 and refundable eq true"
        + "&$orderby=fromPrice desc&$top=20&api-version=2026-11-01");

    private readonly string[] position = ["312.50", "4f2a7c1e-8b3d-4e5f-9a6b-1c2d3e4f5a6b"];
    private string normalized = "";
    private string token = "";

    [GlobalSetup]
    public void Setup()
    {
        normalized = SkipTokenCodec.NormalizeQuery("/stays/search", query);
        token = codec.Encode(position, normalized);
    }

    [Benchmark]
    public string Normalize() => SkipTokenCodec.NormalizeQuery("/stays/search", query);

    [Benchmark]
    public string Encode() => codec.Encode(position, normalized);

    [Benchmark]
    public bool Decode() => codec.TryDecode(token, normalized, out _);

    [Benchmark]
    public bool DecodeAgainstAnotherQuery() => codec.TryDecode(token, "4:/car", out _);
}
