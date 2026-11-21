using Serilog.Configuration;

namespace Serilog.Sinks.Journald;

/// <summary>
/// Extension methods for adding the journald sink to a logger configuration.
/// </summary>
public static class JournaldSinkExtensions
{
    /// <summary>
    /// Writes log events to the systemd journal.
    /// </summary>
    /// <param name="loggerConfiguration">The sink configuration to add the journald sink to.</param>
    /// <returns>A configuration object that allows further configuration of the logger.</returns>
    public static LoggerConfiguration Journald(this LoggerSinkConfiguration loggerConfiguration)
    {
        return loggerConfiguration.Sink(new SerilogJournaldSink());
    }
}