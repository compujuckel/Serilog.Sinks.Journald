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
    private const int MaxStackPayloadLength = 512;
    private const int MaxStackCharLength = 128;

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

    private static int NormalizeFieldName(string propertyName, Span<byte> destination)
    {
        var length = 0;
        foreach (var c in propertyName)
        {
            if (length == destination.Length)
            {
                break;
            }

            if (char.IsAsciiLetterOrDigit(c))
            {
                destination[length++] = (byte)char.ToUpperInvariant(c);
            }
            else if (length > 0 && destination[length - 1] != (byte)'_')
            {
                destination[length++] = (byte)'_';
            }
        }

        if (length == destination.Length)
        {
            length = MaxFieldNameLength;
        }
        else if (length > 0 && destination[length - 1] == (byte)'_')
        {
            length--;
        }

        if (length == 0 || char.IsAsciiDigit((char)destination[0]))
        {
            return 0;
        }

        return length;
    }

    private static bool IsReservedFieldName(ReadOnlySpan<byte> name)
    {
        return name.SequenceEqual("MESSAGE"u8) || name.SequenceEqual("PRIORITY"u8);
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

    private void WriteField(PoolingArrayBufferWriter<byte> buffer, ReadOnlySpan<byte> name, LogEventPropertyValue value)
    {
        Span<char> chars = stackalloc char[MaxStackCharLength];

        if (value is ScalarValue { Value: string text })
        {
            WriteText(buffer, name, text);
        }
        else if (value is ScalarValue { Value: ISpanFormattable formattable } && formattable.TryFormat(chars, out var charsWritten, default, _formatProvider))
        {
            WriteText(buffer, name, chars[..charsWritten]);
        }
        else
        {
            var rendered = RenderValue(value);
            WriteText(buffer, name, rendered);
            GC.KeepAlive(rendered);
        }
    }

    private static void WriteText(PoolingArrayBufferWriter<byte> buffer, ReadOnlySpan<byte> name, ReadOnlySpan<char> text)
    {
        var byteCount = Encoding.UTF8.GetByteCount(text);
        Span<byte> stackPayload = stackalloc byte[MaxStackPayloadLength];
        var rented = byteCount > stackPayload.Length ? ArrayPool<byte>.Shared.Rent(byteCount) : null;
        var payload = rented is null ? stackPayload[..byteCount] : rented.AsSpan(0, byteCount);

        try
        {
            Encoding.UTF8.GetBytes(text, payload);
            buffer.Write(name);

            if (text.Contains('\n'))
            {
                buffer.Write("\n"u8);
                Span<byte> length = stackalloc byte[sizeof(ulong)];
                BinaryPrimitives.WriteUInt64LittleEndian(length, (ulong)payload.Length);
                buffer.Write(length);
            }
            else
            {
                buffer.Write("="u8);
            }

            buffer.Write(payload);
            buffer.Write("\n"u8);
        }
        finally
        {
            if (rented is not null)
            {
                ArrayPool<byte>.Shared.Return(rented);
            }
        }
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

        Span<byte> fieldName = stackalloc byte[MaxFieldNameLength + 1];
        foreach (var property in logEvent.Properties)
        {
            var length = NormalizeFieldName(property.Key, fieldName);
            var name = fieldName[..length];
            if (length > 0 && !IsReservedFieldName(name))
            {
                WriteField(buffer, name, property.Value);
            }
        }

        _socket.Send(buffer.WrittenMemory.Span);
    }

    public void Dispose()
    {
        _socket.Dispose();
    }
}
