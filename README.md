# compujuckel.Serilog.Sinks.Journald

[![NuGet](https://img.shields.io/nuget/v/compujuckel.Serilog.Sinks.Journald.svg)](https://www.nuget.org/packages/compujuckel.Serilog.Sinks.Journald/)

A Serilog sink that writes log events directly to the systemd journal using its [native journal protocol](https://systemd.io/JOURNAL_NATIVE_PROTOCOL/).

## Installation

Install the package from [NuGet](https://www.nuget.org/packages/compujuckel.Serilog.Sinks.Journald/) with the .NET CLI:

```shell
dotnet add package compujuckel.Serilog.Sinks.Journald
```

## Usage

```csharp
using Serilog;

Log.Logger = new LoggerConfiguration()
    .WriteTo.Journald()
    .CreateLogger();

Log.Information("Hello {Name}", "journald");
```

Each log event becomes one journal entry:

- `MESSAGE` contains the rendered message, followed by the exception if there is one.
- `PRIORITY` is the syslog severity mapped from the Serilog level.
- `EXCEPTION` and `MESSAGE_TEMPLATE` are sent when they have a value.
- Each property becomes an upper-case field, for example `ClientName` becomes `CLIENT_NAME`.

### Severity mapping

`PRIORITY` uses the syslog severity scale. The sink maps Serilog levels as follows:

| Serilog level | PRIORITY | Syslog severity |
|---------------|------------|-----------------|
| Fatal       | 2        | crit            |
| Error       | 3        | err             |
| Warning     | 4        | warning         |
| Information | 6        | info            |
| Debug       | 7        | debug           |
| Verbose     | 7        | debug           |

## Automatic protocol upgrading

When a service runs under systemd, its stderr is usually connected to the journal already. Logging to stderr loses structured metadata, so you may prefer the native protocol in that case. The systemd protocol document recommends checking the `JOURNAL_STREAM` environment variable before you upgrade.

`SerilogJournaldSink.IsStderrConnectedToJournal()` performs that check. It returns `true` only when stderr is the journal's stream, so you can add the sink conditionally:

```csharp
using Serilog;
using Serilog.Sinks.Journald;

var configuration = new LoggerConfiguration();

if (SerilogJournaldSink.IsStderrConnectedToJournal())
{
    configuration.WriteTo.Journald();
}
else
{
    configuration.WriteTo.Console();
}

Log.Logger = configuration.CreateLogger();
```

Outside systemd, or when stderr has been redirected, the check returns `false` and events go to the console sink instead.

## Requirements

- Linux with systemd-journald, which listens on `/run/systemd/journal/socket`.
- If the socket cannot be reached, the sink reports the problem through `Serilog.Debugging.SelfLog` and does not throw.

## License

MIT
