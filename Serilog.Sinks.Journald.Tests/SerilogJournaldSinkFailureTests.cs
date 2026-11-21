using System.Net.Sockets;
using Serilog.Debugging;
using Serilog.Events;

namespace Serilog.Sinks.Journald.Tests;

public sealed class SerilogJournaldSinkFailureTests : IDisposable
{
    private readonly string _socketPath = Path.Combine(Path.GetTempPath(), $"journald-missing-{Guid.NewGuid():N}.sock");
    private readonly StringWriter _selfLog = new();

    public SerilogJournaldSinkFailureTests()
    {
        SelfLog.Enable(_selfLog);
    }

    public void Dispose()
    {
        SelfLog.Disable();
        _selfLog.Dispose();
    }

    [Fact]
    public void ConstructionDoesNotThrowWhenSocketDoesNotExist()
    {
        using var sink = new SerilogJournaldSink(_socketPath);

        Assert.Contains("journald", _selfLog.ToString(), StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void EmitIsNoOpWhenSocketCannotBeConnected()
    {
        using var sink = new SerilogJournaldSink(_socketPath);
        var logEvent = new LogEvent(DateTimeOffset.UtcNow, LogEventLevel.Information, null, MessageTemplate.Empty, []);

        var exception = Record.Exception(() => sink.Emit(logEvent));

        Assert.Null(exception);
    }

    [Fact]
    public void EmitIsNoOpWhenSocketBecomesUnreachable()
    {
        var serverPath = Path.Combine(Path.GetTempPath(), $"journald-gone-{Guid.NewGuid():N}.sock");
        var server = new Socket(AddressFamily.Unix, SocketType.Dgram, ProtocolType.Unspecified);
        server.Bind(new UnixDomainSocketEndPoint(serverPath));

        try
        {
            using var sink = new SerilogJournaldSink(serverPath);
            server.Dispose();

            var logEvent = new LogEvent(DateTimeOffset.UtcNow, LogEventLevel.Information, null, MessageTemplate.Empty, []);
            var exception = Record.Exception(() => sink.Emit(logEvent));

            Assert.Null(exception);
        }
        finally
        {
            server.Dispose();
            File.Delete(serverPath);
        }
    }
}
