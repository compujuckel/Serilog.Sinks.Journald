using System.Net.Sockets;
using BenchmarkDotNet.Attributes;
using Serilog.Core;
using Serilog.Events;

namespace Serilog.Sinks.Journald.Benchmarks;

[MemoryDiagnoser]
public class LogWritingBenchmarks
{
    private readonly string _socketPath = Path.Combine(Path.GetTempPath(), $"journald-bench-{Guid.NewGuid():N}.sock");
    private readonly string _filePath = Path.Combine(Path.GetTempPath(), $"file-bench-{Guid.NewGuid():N}.log");
    private volatile bool _draining;
    private Socket? _receiver;
    private Thread? _drainThread;

    private SerilogJournaldSink? _journaldSink;
    private ILogger _baselineLogger = null!;
    private ILogger _journaldLogger = null!;
    private Logger _fileLogger = null!;

    [GlobalSetup]
    public void Setup()
    {
        _receiver = new Socket(AddressFamily.Unix, SocketType.Dgram, ProtocolType.Unspecified);
        _receiver.ReceiveTimeout = 100;
        _receiver.Bind(new UnixDomainSocketEndPoint(_socketPath));

        _draining = true;
        _drainThread = new Thread(() => Drain(_receiver)) { IsBackground = true };
        _drainThread.Start();

        _journaldSink = new SerilogJournaldSink(_socketPath);

        _baselineLogger = new LoggerConfiguration()
            .MinimumLevel.Information()
            .WriteTo.Sink(new NoOpSink())
            .CreateLogger();

        _journaldLogger = new LoggerConfiguration()
            .MinimumLevel.Information()
            .WriteTo.Sink(_journaldSink)
            .CreateLogger();

        _fileLogger = new LoggerConfiguration()
            .MinimumLevel.Information()
            .WriteTo.File(_filePath, fileSizeLimitBytes: null)
            .CreateLogger();
    }

    [GlobalCleanup]
    public void Cleanup()
    {
        _draining = false;
        _drainThread?.Join();

        _fileLogger?.Dispose();
        _journaldSink?.Dispose();
        _receiver?.Dispose();
        System.IO.File.Delete(_socketPath);
        System.IO.File.Delete(_filePath);
    }

    [Benchmark(Baseline = true)]
    public void LoggerPipelineWithNoOpSink()
    {
        _baselineLogger.Information("Hello, world");
    }

    [Benchmark]
    public void JournaldSimpleMessage()
    {
        _journaldLogger.Information("Hello, world");
    }

    [Benchmark]
    public void JournaldMessageWithProperties()
    {
        _journaldLogger.Information("User {UserId} logged in from {Address} after {Attempts} attempts", 42, "10.0.0.1", 3);
    }

    [Benchmark]
    public void JournaldMultilineMessage()
    {
        _journaldLogger.Information("Line one\nLine two\nLine three");
    }

    [Benchmark]
    public void FileSimpleMessage()
    {
        _fileLogger.Information("Hello, world");
    }

    [Benchmark]
    public void FileMessageWithProperties()
    {
        _fileLogger.Information("User {UserId} logged in from {Address} after {Attempts} attempts", 42, "10.0.0.1", 3);
    }

    private void Drain(Socket receiver)
    {
        var buffer = new byte[64 * 1024];
        while (_draining)
        {
            try
            {
                receiver.Receive(buffer);
            }
            catch (SocketException e) when (e.SocketErrorCode == SocketError.TimedOut)
            {
            }
        }
    }

    private sealed class NoOpSink : ILogEventSink
    {
        public void Emit(LogEvent logEvent)
        {
        }
    }
}
