using System.Buffers;
using System.Buffers.Binary;
using System.Globalization;
using System.Net.Sockets;
using System.Text;
using DotNext.Buffers;
using Serilog.Core;
using Serilog.Debugging;
using Serilog.Events;
using Serilog.Formatting.Display;


namespace Serilog.Sinks.Journald;

/// <summary>
/// A Serilog sink that writes log events to the systemd journal using the native journal protocol.
/// </summary>
public sealed class SerilogJournaldSink : ILogEventSink, IDisposable
{
    private const string JournalSocketPath = "/run/systemd/journal/socket";
    private const int MaxFieldNameLength = 64;
    private const int MaxStackPayloadLength = 512;

    [ThreadStatic]
    private static Utf8BufferTextWriter? _cachedWriter;

    private static readonly MessageTemplateTextFormatter MessageFormatter = new("{Message:lj}", CultureInfo.InvariantCulture);

    private readonly Socket? _socket;

    /// <summary>
    /// Creates a sink that connects to the journald socket at <c>/run/systemd/journal/socket</c>.
    /// </summary>
    /// <remarks>
    /// If the socket cannot be reached, the problem is reported through <see cref="SelfLog"/> and events are discarded.
    /// </remarks>
    public SerilogJournaldSink()
        : this(JournalSocketPath)
    {
    }

    internal SerilogJournaldSink(string socketPath)
    {
        var socket = new Socket(AddressFamily.Unix, SocketType.Dgram, ProtocolType.Unspecified);

        try
        {
            var endpoint = new UnixDomainSocketEndPoint(socketPath);
            socket.Connect(endpoint);
            _socket = socket;
        }
        catch (Exception e)
        {
            SelfLog.WriteLine("Unable to connect to journald socket: {0}", e.Message);
            socket.Dispose();
        }
    }

    /// <summary>
    /// Determines whether standard error is connected to the journal stream named by the <c>JOURNAL_STREAM</c> environment variable.
    /// </summary>
    /// <returns><see langword="true"/> if stderr is connected to the journal; otherwise, <see langword="false"/>.</returns>
    public static bool IsStderrConnectedToJournal()
    {
        return JournalStreamDetector.IsStderrConnectedToJournal();
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
        for (var i = 0; i < propertyName.Length && length < destination.Length; i++)
        {
            var c = propertyName[i];
            if (char.IsAsciiLetterOrDigit(c))
            {
                if (IsWordBoundary(propertyName, i))
                {
                    length = AppendSeparator(destination, length);
                }

                if (length == destination.Length)
                {
                    break;
                }

                destination[length++] = (byte)char.ToUpperInvariant(c);
            }
            else
            {
                length = AppendSeparator(destination, length);
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

    private static bool IsWordBoundary(string name, int index)
    {
        if (index == 0 || !char.IsUpper(name[index]))
        {
            return false;
        }

        var previous = name[index - 1];
        if (char.IsLower(previous) || char.IsDigit(previous))
        {
            return true;
        }

        return char.IsUpper(previous) && index + 1 < name.Length && char.IsLower(name[index + 1]);
    }

    private static int AppendSeparator(Span<byte> destination, int length)
    {
        if (length > 0 && length < destination.Length && destination[length - 1] != (byte)'_')
        {
            destination[length++] = (byte)'_';
        }

        return length;
    }

    private static bool IsReservedFieldName(ReadOnlySpan<byte> name)
    {
        return name.SequenceEqual("MESSAGE"u8)
            || name.SequenceEqual("PRIORITY"u8)
            || name.SequenceEqual("EXCEPTION"u8)
            || name.SequenceEqual("MESSAGE_TEMPLATE"u8);
    }

    private static string RenderValue(LogEventPropertyValue value)
    {
        return value switch
        {
            ScalarValue { Value: string text } => text,
            ScalarValue { Value: IFormattable formattable } => formattable.ToString(null, CultureInfo.InvariantCulture),
            ScalarValue { Value: var other } => other?.ToString() ?? "null",
            _ => value.ToString()
        };
    }

    private static void WriteField(PoolingArrayBufferWriter<byte> buffer, ReadOnlySpan<byte> name, LogEventPropertyValue value)
    {
        Span<byte> utf8 = stackalloc byte[MaxStackPayloadLength];

        if (value is ScalarValue { Value: string text })
        {
            WriteText(buffer, name, text);
        }
        else if (value is ScalarValue { Value: IUtf8SpanFormattable formattable } && formattable.TryFormat(utf8, out var bytesWritten, default, CultureInfo.InvariantCulture))
        {
            WriteUtf8(buffer, name, utf8[..bytesWritten]);
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
        var rented = byteCount > MaxStackPayloadLength ? ArrayPool<byte>.Shared.Rent(byteCount) : null;
        var payload = rented is null ? stackalloc byte[byteCount] : rented.AsSpan(0, byteCount);

        try
        {
            Encoding.UTF8.GetBytes(text, payload);
            WriteUtf8(buffer, name, payload);
        }
        finally
        {
            if (rented is not null)
            {
                ArrayPool<byte>.Shared.Return(rented);
            }
        }
    }

    private static void WriteUtf8(PoolingArrayBufferWriter<byte> buffer, ReadOnlySpan<byte> name, ReadOnlySpan<byte> payload)
    {
        buffer.Write(name);

        if (payload.Contains((byte)'\n'))
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

    /// <summary>
    /// Sends a log event to the journal as a single datagram.
    /// </summary>
    /// <param name="logEvent">The log event to write.</param>
    public void Emit(LogEvent logEvent)
    {
        var socket = _socket;
        if (socket is null) return;

        var writer = _cachedWriter ?? new Utf8BufferTextWriter(new PoolingArrayBufferWriter<byte>());
        _cachedWriter = null;

        try
        {
            EmitCore(writer, logEvent, socket);
        }
        finally
        {
            writer.Reset();
            _cachedWriter = writer;
        }
    }

    private static void EmitCore(Utf8BufferTextWriter writer, LogEvent logEvent, Socket socket)
    {
        var buffer = writer.Output;

        buffer.Write("PRIORITY="u8);
        buffer.Write([MapPriority(logEvent.Level)]);
        buffer.Write("\n"u8);
        buffer.Write("MESSAGE\n\0\0\0\0\0\0\0\0"u8);
        var msgStartPos = buffer.WrittenCount;

        MessageFormatter.Format(logEvent, writer);

        var exception = logEvent.Exception?.ToString();
        if (exception is not null)
        {
            writer.Write('\n');
            writer.Write(exception);
        }

        writer.Flush();

        var msgLength = buffer.WrittenCount - msgStartPos;

        buffer.Write("\n"u8);

        BinaryPrimitives.WriteUInt64LittleEndian(buffer.WrittenArray.AsSpan(msgStartPos - 8, 8), (ulong)msgLength);
        
        if (exception is not null)
        {
            WriteText(buffer, "EXCEPTION"u8, exception);
        }

        WriteText(buffer, "MESSAGE_TEMPLATE"u8, logEvent.MessageTemplate.Text);

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

        try
        {
            socket.Send(buffer.WrittenMemory.Span);
        }
        catch (Exception e)
        {
            SelfLog.WriteLine("Unable to send log event to journald socket: {0}", e.Message);
        }
    }

    /// <summary>
    /// Closes the connection to the journald socket.
    /// </summary>
    public void Dispose()
    {
        _socket?.Dispose();
    }
}
