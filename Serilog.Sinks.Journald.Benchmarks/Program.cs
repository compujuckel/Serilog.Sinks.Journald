using BenchmarkDotNet.Configs;
using BenchmarkDotNet.Exporters;
using BenchmarkDotNet.Exporters.Json;
using BenchmarkDotNet.Loggers;
using BenchmarkDotNet.Running;

namespace Serilog.Sinks.Journald.Benchmarks;

internal static class Program
{
    private const string ResultsDirectory = "BenchmarkDotNet.Artifacts/results";

    public static void Main(string[] args)
    {
        Directory.CreateDirectory(ResultsDirectory);
        var logPath = Path.Combine(ResultsDirectory, $"benchmark-{DateTime.Now:yyyyMMdd-HHmmss}-log.txt");
        using var logWriter = new StreamWriter(logPath) { AutoFlush = true };

        var config = ManualConfig.Create(DefaultConfig.Instance)
            .AddLogger(ConsoleLogger.Default, new StreamLogger(logWriter))
            .AddExporter(MarkdownExporter.GitHub, JsonExporter.Full);

        BenchmarkSwitcher.FromAssembly(typeof(LogWritingBenchmarks).Assembly).RunAllJoined(config);
    }
}
