using BenchmarkDotNet.Configs;
using BenchmarkDotNet.Jobs;
using BenchmarkDotNet.Running;
using BenchmarkDotNet.Toolchains.InProcess.Emit;
using Viatica.Kernel.Benchmarks;

// BenchmarkDotNet 0.15.8 cannot generate a child project for net11.0 (its runtime moniker table stops at .NET 10),
// so the benchmarks run in the host process. Process isolation matters little for allocation-light, microsecond
// work like this; move back to the default toolchain once a release knows .NET 11.
IConfig config = DefaultConfig.Instance.AddJob(Job.Default.WithToolchain(InProcessEmitToolchain.Instance));

BenchmarkSwitcher.FromAssembly(typeof(SkipTokenBenchmarks).Assembly).Run(args, config);
