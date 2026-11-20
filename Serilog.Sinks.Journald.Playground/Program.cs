namespace Serilog.Sinks.Journald.Playground;

internal static class Program
{
    public static void Main(string[] args)
    {
        Console.WriteLine("Start");

        var log = new LoggerConfiguration()
            .WriteTo.Journald()
            .WriteTo.Console()
            .CreateLogger();

        log.Information("Hello World! {Test}, {Answer}", "test", 42);

        try
        {
            throw new Exception("Something went wrong");
        }
        catch (Exception ex)
        {
            log.Error(ex, "Exception thrown");
        }

        var chars = new char[200];
        chars.AsSpan().Fill('a');

        log.Information("{Message}", new string(chars));

        log.Verbose("Verbose");
        log.Debug("Debug");
        log.Information("Information");
        log.Warning("Warning");
        log.Error("Error");
        log.Fatal("Fatal");

        Console.WriteLine("End");
    }
}