using BenchmarkDotNet.Running;

BenchmarkSwitcher.FromAssembly(typeof(Serilog.Sinks.Journald.Benchmarks.LogWritingBenchmarks).Assembly).Run(args);
