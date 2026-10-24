using BenchmarkDotNet.Running;
using Viatica.Kernel.Benchmarks;

BenchmarkSwitcher.FromAssembly(typeof(SkipTokenBenchmarks).Assembly).Run(args);
