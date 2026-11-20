using BenchmarkDotNet.Running;

namespace Serilog.Sinks.Journald.Benchmarks;

internal static class Program
{
    public static void Main(string[] args)
    {
        BenchmarkSwitcher.FromAssembly(typeof(LogWritingBenchmarks).Assembly).Run(args);
    }
}
