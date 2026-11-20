using System.Text;
using DotNext.Buffers;

namespace Serilog.Sinks.Journald;

internal sealed class Utf8BufferTextWriter : TextWriter
{
    private const int MaxChunkLength = 512;

    private readonly Encoder _encoder = Encoding.UTF8.GetEncoder();

    public Utf8BufferTextWriter(PoolingArrayBufferWriter<byte> output)
    {
        Output = output;
    }

    public PoolingArrayBufferWriter<byte> Output { get; }

    public override Encoding Encoding => Encoding.UTF8;

    public override void Write(char value)
    {
        ReadOnlySpan<char> chars = [value];
        WriteChars(chars);
    }

    public override void Write(string? value)
    {
        if (!string.IsNullOrEmpty(value))
        {
            WriteChars(value);
        }
    }

    public override void Write(char[]? buffer, int index, int count)
    {
        if (buffer is not null)
        {
            WriteChars(buffer.AsSpan(index, count));
        }
    }

    public override void Write(ReadOnlySpan<char> buffer)
    {
        WriteChars(buffer);
    }

    public override void Flush()
    {
        var destination = Output.GetSpan(Encoding.UTF8.GetMaxByteCount(1));
        _encoder.Convert(ReadOnlySpan<char>.Empty, destination, flush: true, out _, out var bytesUsed, out _);
        Output.Advance(bytesUsed);
    }

    public void Reset()
    {
        Output.Clear(reuseBuffer: true);
        _encoder.Reset();
    }

    private void WriteChars(ReadOnlySpan<char> chars)
    {
        while (!chars.IsEmpty)
        {
            var chunk = chars[..Math.Min(chars.Length, MaxChunkLength)];
            var destination = Output.GetSpan(Encoding.UTF8.GetMaxByteCount(chunk.Length + 1));
            _encoder.Convert(chunk, destination, flush: false, out var charsUsed, out var bytesUsed, out _);
            Output.Advance(bytesUsed);
            chars = chars[charsUsed..];
        }
    }
}
