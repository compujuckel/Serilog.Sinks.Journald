using System.Buffers.Binary;
using System.Net.Sockets;
using System.Text;
using Serilog.Core;
using Serilog.Events;

namespace Serilog.Sinks.Journald.Tests;

public sealed class JournaldProtocolTests : IDisposable
{
    private readonly string _socketPath = Path.Combine(Path.GetTempPath(), $"journald-test-{Guid.NewGuid():N}.sock");
    private readonly Socket _server;

    public JournaldProtocolTests()
    {
        _server = new Socket(AddressFamily.Unix, SocketType.Dgram, ProtocolType.Unspecified);
        _server.ReceiveTimeout = 5000;
        _server.Bind(new UnixDomainSocketEndPoint(_socketPath));
    }

    public void Dispose()
    {
        _server.Dispose();
        File.Delete(_socketPath);
    }

    [Theory]
    [InlineData(LogEventLevel.Fatal, "2")]
    [InlineData(LogEventLevel.Error, "3")]
    [InlineData(LogEventLevel.Warning, "4")]
    [InlineData(LogEventLevel.Information, "6")]
    [InlineData(LogEventLevel.Debug, "7")]
    [InlineData(LogEventLevel.Verbose, "7")]
    public void PriorityIsSentAsSyslogSeverityDigit(LogEventLevel level, string expectedPriority)
    {
        using var sink = CreateSink();
        var logger = CreateLogger(sink);

        logger.Write(level, "message");

        var fields = ParseDatagram(Receive());
        Assert.Equal(expectedPriority, GetSingleValue(fields, "PRIORITY"));
    }

    [Fact]
    public void MessageIsSentAsLengthPrefixedFieldWithExactBytes()
    {
        using var sink = CreateSink();
        var logger = CreateLogger(sink);

        logger.Information("Hello");

        var length = new byte[8];
        BinaryPrimitives.WriteUInt64LittleEndian(length, 5);
        byte[] expected = [.. Encoding.ASCII.GetBytes("PRIORITY=6\nMESSAGE\n"), .. length, .. "Hello\n"u8.ToArray()];
        Assert.Equal(expected, Receive());
    }

    [Fact]
    public void MessageWithEmbeddedNewlinesIsPreservedIntact()
    {
        using var sink = CreateSink();
        var logger = CreateLogger(sink);

        logger.Information("line1\nline2\nline3");

        var fields = ParseDatagram(Receive());
        var message = fields.Single(f => f.Name == "MESSAGE");
        Assert.True(message.Binary);
        Assert.Equal("line1\nline2\nline3", Encoding.UTF8.GetString(message.Value));
    }

    [Fact]
    public void FieldWithoutNewlineUsesAssignmentSerialization()
    {
        using var sink = CreateSink();
        var logger = CreateLogger(sink);

        logger.ForContext("greeting", "hello").Information("message");

        var raw = Receive();
        AssertContainsBytes(raw, "GREETING=hello\n"u8.ToArray());

        var field = ParseDatagram(raw).Single(f => f.Name == "GREETING");
        Assert.False(field.Binary);
        Assert.Equal("hello", Encoding.UTF8.GetString(field.Value));
    }

    [Fact]
    public void FieldWithNewlineUsesLengthPrefixedSerialization()
    {
        using var sink = CreateSink();
        var logger = CreateLogger(sink);

        logger.ForContext("stack", "frame1\nframe2").Information("message");

        var raw = Receive();
        var length = new byte[8];
        BinaryPrimitives.WriteUInt64LittleEndian(length, 13);
        byte[] expectedField = [.. Encoding.ASCII.GetBytes("STACK\n"), .. length, .. "frame1\nframe2\n"u8.ToArray()];
        AssertContainsBytes(raw, expectedField);

        var field = ParseDatagram(raw).Single(f => f.Name == "STACK");
        Assert.True(field.Binary);
        Assert.Equal("frame1\nframe2", Encoding.UTF8.GetString(field.Value));
    }

    [Fact]
    public void EmptyValueIsSentAsAssignmentWithNoValue()
    {
        using var sink = CreateSink();
        var logger = CreateLogger(sink);

        logger.ForContext("empty", "").Information("message");

        var raw = Receive();
        AssertContainsBytes(raw, "EMPTY=\n"u8.ToArray());
        Assert.Empty(GetSingleField(ParseDatagram(raw), "EMPTY").Value);
    }

    [Fact]
    public void NonAsciiValueIsEncodedAsUtf8()
    {
        using var sink = CreateSink();
        var logger = CreateLogger(sink);

        logger.ForContext("city", "Zürich").Information("message");

        var field = GetSingleField(ParseDatagram(Receive()), "CITY");
        Assert.Equal(Encoding.UTF8.GetBytes("Zürich"), field.Value);
    }

    [Fact]
    public void NumericValueIsRenderedAsText()
    {
        using var sink = CreateSink();
        var logger = CreateLogger(sink);

        logger.ForContext("answer", 42).Information("message");

        Assert.Equal("42", GetSingleValue(ParseDatagram(Receive()), "ANSWER"));
    }

    [Theory]
    [InlineData("answer", "ANSWER")]
    [InlineData("user-id", "USER_ID")]
    [InlineData("user.name", "USER_NAME")]
    [InlineData("  spaced key ", "SPACED_KEY")]
    [InlineData("__private", "PRIVATE")]
    [InlineData("_x", "X")]
    [InlineData("a__b", "A_B")]
    [InlineData("trailing-", "TRAILING")]
    [InlineData("naïve", "NA_VE")]
    public void PropertyNamesAreNormalizedToUpperCaseFieldNames(string propertyName, string expectedFieldName)
    {
        using var sink = CreateSink();
        var logger = CreateLogger(sink);

        logger.ForContext(propertyName, "value").Information("message");

        var fields = ParseDatagram(Receive());
        Assert.Equal("value", GetSingleValue(fields, expectedFieldName));
    }

    [Theory]
    [InlineData("123abc")]
    [InlineData("---")]
    [InlineData("_")]
    [InlineData("é")]
    public void PropertyNamesWithoutValidFieldNameAreDropped(string propertyName)
    {
        using var sink = CreateSink();
        var logger = CreateLogger(sink);

        logger.ForContext(propertyName, "value").Information("message");

        var fields = ParseDatagram(Receive());
        Assert.Equal(new[] { "PRIORITY", "MESSAGE" }, fields.Select(f => f.Name).ToArray());
    }

    [Fact]
    public void LongPropertyNameIsTruncatedTo64Characters()
    {
        using var sink = CreateSink();
        var logger = CreateLogger(sink);

        logger.ForContext(new string('a', 80), "value").Information("message");

        var field = ParseDatagram(Receive()).Single(f => f.Name.Length == 64);
        Assert.Equal(new string('A', 64), field.Name);
    }

    [Fact]
    public void SentFieldNamesOnlyContainUppercaseLettersDigitsAndUnderscores()
    {
        using var sink = CreateSink();
        var logger = CreateLogger(sink);

        logger
            .ForContext("user-id", 1)
            .ForContext("ünïcode key", 2)
            .ForContext("with space", 3)
            .ForContext("_protected", 4)
            .ForContext("key123", 5)
            .Information("message");

        foreach (var field in ParseDatagram(Receive()))
        {
            Assert.Matches("^[A-Z][A-Z0-9_]*$", field.Name);
        }
    }

    [Fact]
    public void ClientNeverSendsTrustedUnderscorePrefixedFields()
    {
        using var sink = CreateSink();
        var logger = CreateLogger(sink);

        logger.ForContext("_PID", 1234).ForContext("__COMM", "evil").Information("message");

        var fields = ParseDatagram(Receive());
        Assert.All(fields, f => Assert.False(f.Name.StartsWith('_')));
    }

    [Fact]
    public void ReservedMessageAndPriorityPropertiesDoNotDuplicateCoreFields()
    {
        using var sink = CreateSink();
        var logger = CreateLogger(sink);

        logger.ForContext("Message", "overridden").ForContext("priority", 1).Information("message");

        var fields = ParseDatagram(Receive());
        Assert.Equal(new[] { "PRIORITY", "MESSAGE" }, fields.Select(f => f.Name).ToArray());
        Assert.Equal("message", GetSingleValue(fields, "MESSAGE"));
        Assert.Equal("6", GetSingleValue(fields, "PRIORITY"));
    }

    [Fact]
    public void ExceptionIsAppendedToMessageField()
    {
        using var sink = CreateSink();
        var logger = CreateLogger(sink);
        var exception = new InvalidOperationException("boom");

        logger.Information(exception, "Hello");

        Assert.Equal($"Hello\n{exception}", GetSingleValue(ParseDatagram(Receive()), "MESSAGE"));
    }

    [Fact]
    public void MessageIsWrittenWithoutExceptionWhenNoneIsPresent()
    {
        using var sink = CreateSink();
        var logger = CreateLogger(sink);

        logger.Information("Hello");

        Assert.Equal("Hello", GetSingleValue(ParseDatagram(Receive()), "MESSAGE"));
    }

    [Fact]
    public void EachLogEventIsSentAsSeparateDatagram()
    {
        using var sink = CreateSink();
        var logger = CreateLogger(sink);

        logger.Information("first");
        logger.Error("second");

        var first = ParseDatagram(Receive());
        var second = ParseDatagram(Receive());

        Assert.Equal("first", GetSingleValue(first, "MESSAGE"));
        Assert.Equal("3", GetSingleValue(second, "PRIORITY"));
        Assert.Equal("second", GetSingleValue(second, "MESSAGE"));
    }

    [Fact]
    public void EveryDatagramStartsWithPriorityThenMessage()
    {
        using var sink = CreateSink();
        var logger = CreateLogger(sink);

        logger.ForContext("answer", 42).Information("Hello\nWorld");

        var raw = Receive();
        Assert.Equal(Encoding.ASCII.GetBytes("PRIORITY=6\nMESSAGE\n"), raw.AsSpan(0, 19).ToArray());
        Assert.Equal((byte)'\n', raw[^1]);
    }

    private SerilogJournaldSink CreateSink()
    {
        return new SerilogJournaldSink(_socketPath);
    }

    private static ILogger CreateLogger(ILogEventSink sink)
    {
        return new LoggerConfiguration().MinimumLevel.Verbose().WriteTo.Sink(sink).CreateLogger();
    }

    private byte[] Receive()
    {
        var buffer = new byte[64 * 1024];
        var length = _server.Receive(buffer);
        return buffer.AsSpan(0, length).ToArray();
    }

    private static string GetSingleValue(List<JournalField> fields, string name)
    {
        return Encoding.UTF8.GetString(GetSingleField(fields, name).Value);
    }

    private static JournalField GetSingleField(List<JournalField> fields, string name)
    {
        return fields.Single(f => f.Name == name);
    }

    private static List<JournalField> ParseDatagram(byte[] data)
    {
        var fields = new List<JournalField>();
        var pos = 0;

        while (pos < data.Length)
        {
            var nameEnd = pos;
            while (nameEnd < data.Length && data[nameEnd] != (byte)'=' && data[nameEnd] != (byte)'\n')
            {
                nameEnd++;
            }

            if (nameEnd >= data.Length)
            {
                throw new InvalidDataException("Field name is not terminated");
            }

            var name = Encoding.ASCII.GetString(data, pos, nameEnd - pos);

            if (data[nameEnd] == (byte)'=')
            {
                pos = nameEnd + 1;
                var valueEnd = Array.IndexOf(data, (byte)'\n', pos);
                if (valueEnd < 0)
                {
                    throw new InvalidDataException($"Value of {name} is not terminated");
                }

                fields.Add(new JournalField(name, data[pos..valueEnd], false));
                pos = valueEnd + 1;
            }
            else
            {
                pos = nameEnd + 1;
                if (pos + sizeof(ulong) > data.Length)
                {
                    throw new InvalidDataException($"Size of {name} is truncated");
                }

                var size = BinaryPrimitives.ReadUInt64LittleEndian(data.AsSpan(pos, sizeof(ulong)));
                pos += sizeof(ulong);
                if ((ulong)(data.Length - pos) < size + 1)
                {
                    throw new InvalidDataException($"Value of {name} is truncated");
                }

                var valueLength = (int)size;
                fields.Add(new JournalField(name, data[pos..(pos + valueLength)], true));
                pos += valueLength;

                if (data[pos] != (byte)'\n')
                {
                    throw new InvalidDataException($"Value of {name} is not newline-terminated");
                }

                pos++;
            }
        }

        return fields;
    }

    private sealed record JournalField(string Name, byte[] Value, bool Binary);

    private static void AssertContainsBytes(byte[] data, byte[] expected)
    {
        Assert.True(data.AsSpan().IndexOf(expected) >= 0, $"Expected datagram to contain {Encoding.ASCII.GetString(expected)}");
    }
}
