using Serilog.Configuration;

namespace Serilog.Sinks.Journald;

public static class JournaldSinkExtensions
{
    public static LoggerConfiguration Journald(
        this LoggerSinkConfiguration loggerConfiguration,
        string? outputTemplate = null,
        IFormatProvider? formatProvider = null)
    {
        return loggerConfiguration.Sink(new SerilogJournaldSink(outputTemplate, formatProvider));
    }
}