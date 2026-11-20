using System.Buffers;
using System.Buffers.Binary;
using System.Net.Sockets;
using System.Text;
using DotNext.Buffers;
using DotNext.IO;
using Serilog.Core;
using Serilog.Events;
using Serilog.Formatting.Display;


namespace Serilog.Sinks.Journald;

public sealed class SerilogJournaldSink : ILogEventSink, IDisposable
{
    private const string JournalSocketPath = "/run/systemd/journal/socket";
    private const int MaxFieldNameLength = 64;
    private static readonly HashSet<string> ReservedFieldNames = ["MESSAGE", "PRIORITY"];

    private readonly IFormatProvider? _formatProvider;
    private readonly MessageTemplateTextFormatter? _formatter;
    private readonly Socket _socket;

    public SerilogJournaldSink(string? outputTemplate, IFormatProvider? formatProvider)
        : this(outputTemplate, formatProvider, JournalSocketPath)
    {
    }

    internal SerilogJournaldSink(string? outputTemplate, IFormatProvider? formatProvider, string socketPath)
    {
        _formatProvider = formatProvider;

        if (outputTemplate != null)
        {
            _formatter = new MessageTemplateTextFormatter(outputTemplate, formatProvider);
        }

        _socket = new Socket(AddressFamily.Unix, SocketType.Dgram, ProtocolType.Unspecified);
        var endpoint = new UnixDomainSocketEndPoint(socketPath);
        _socket.Connect(endpoint);
    }

    private static byte MapPriority(LogEventLevel level)
    {
        return level switch
        {
            LogEventLevel.Fatal => 0x32,
            LogEventLevel.Error => 0x33,
            LogEventLevel.Warning => 0x34,
            LogEventLevel.Information => 0x36,
            LogEventLevel.Debug => 0x37,
            LogEventLevel.Verbose => 0x37,
            _ => throw new ArgumentOutOfRangeException(nameof(level), level, "Unknown LogEventLevel")
        };
    }

    private static string? ToFieldName(string propertyName)
    {
        var builder = new StringBuilder(propertyName.Length);
        foreach (var c in propertyName)
        {
            if (char.IsAsciiLetterOrDigit(c))
            {
                builder.Append(char.ToUpperInvariant(c));
            }
            else if (builder.Length > 0 && builder[^1] != '_')
            {
                builder.Append('_');
            }
        }

        if (builder.Length > 0 && builder[^1] == '_')
        {
            builder.Length--;
        }

        if (builder.Length == 0 || char.IsAsciiDigit(builder[0]))
        {
            return null;
        }

        if (builder.Length > MaxFieldNameLength)
        {
            builder.Length = MaxFieldNameLength;
        }

        var fieldName = builder.ToString();
        return ReservedFieldNames.Contains(fieldName) ? null : fieldName;
    }

    private string RenderValue(LogEventPropertyValue value)
    {
        return value switch
        {
            ScalarValue { Value: string text } => text,
            ScalarValue { Value: IFormattable formattable } => formattable.ToString(null, _formatProvider),
            ScalarValue { Value: var other } => other?.ToString() ?? "null",
            _ => value.ToString()
        };
    }

    private void WriteField(PoolingArrayBufferWriter<byte> buffer, string name, LogEventPropertyValue value)
    {
        var payload = Encoding.UTF8.GetBytes(RenderValue(value));
        buffer.Write(Encoding.ASCII.GetBytes(name));

        if (payload.AsSpan().Contains((byte)'\n'))
        {
            buffer.Write("\n"u8);
            Span<byte> length = stackalloc byte[sizeof(ulong)];
            BinaryPrimitives.WriteUInt64LittleEndian(length, (ulong)payload.Length);
            buffer.Write(length);
            buffer.Write(payload);
        }
        else
        {
            buffer.Write("="u8);
            buffer.Write(payload);
        }

        buffer.Write("\n"u8);
    }

    public void Emit(LogEvent logEvent)
    {
        using var buffer = new PoolingArrayBufferWriter<byte>(ArrayPool<byte>.Shared);

        buffer.Write("PRIORITY="u8);
        buffer.Write([MapPriority(logEvent.Level)]);
        buffer.Write("\n"u8);
        buffer.Write("MESSAGE\n\0\0\0\0\0\0\0\0"u8);
        var msgStartPos = buffer.WrittenCount;

        using (var writer = TextWriter.Create(buffer, Encoding.UTF8, _formatProvider))
        {
            if (_formatter != null)
            {
                _formatter.Format(logEvent, writer);
            }
            else
            {
                logEvent.RenderMessage(writer, _formatProvider);
            }
        }

        var msgLength = buffer.WrittenCount - msgStartPos;

        buffer.Write("\n"u8);

        BinaryPrimitives.WriteUInt64LittleEndian(buffer.WrittenArray.AsSpan(msgStartPos - 8, 8), (ulong)msgLength);

        foreach (var property in logEvent.Properties)
        {
            var fieldName = ToFieldName(property.Key);
            if (fieldName is not null)
            {
                WriteField(buffer, fieldName, property.Value);
            }
        }

        _socket.Send(buffer.WrittenMemory.Span);
    }

    public void Dispose()
    {
        _socket.Dispose();
    }
}
