using Serilog.Configuration;

namespace Serilog.Sinks.Journald;

public static class JournaldSinkExtensions
{
    public static LoggerConfiguration Journald(this LoggerSinkConfiguration loggerConfiguration)
    {
        return loggerConfiguration.Sink(new SerilogJournaldSink());
    }
}