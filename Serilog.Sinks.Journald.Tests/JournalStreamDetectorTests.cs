namespace Serilog.Sinks.Journald.Tests;

public sealed class JournalStreamDetectorTests : IDisposable
{
    private readonly string _path = Path.Combine(Path.GetTempPath(), $"journal-stream-test-{Guid.NewGuid():N}");
    private readonly FileStream _stream;
    private readonly int _fileDescriptor;

    public JournalStreamDetectorTests()
    {
        _stream = new FileStream(_path, FileMode.CreateNew, FileAccess.ReadWrite);
        _fileDescriptor = _stream.SafeFileHandle.DangerousGetHandle().ToInt32();
    }

    public void Dispose()
    {
        _stream.Dispose();
        File.Delete(_path);
    }

    [Fact]
    public void ParsesDeviceAndInodeFromJournalStream()
    {
        var parsed = JournalStreamDetector.TryParseJournalStream("2049:1234567", out var device, out var inode);

        Assert.True(parsed);
        Assert.Equal(2049UL, device);
        Assert.Equal(1234567UL, inode);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("2049")]
    [InlineData("2049:")]
    [InlineData(":1234")]
    [InlineData("2049:1234:5")]
    [InlineData("-1:1234")]
    [InlineData("2049:+1234")]
    [InlineData(" 2049:1234")]
    [InlineData("abc:1234")]
    [InlineData("2049:1234\n")]
    public void RejectsMalformedJournalStream(string? value)
    {
        Assert.False(JournalStreamDetector.TryParseJournalStream(value, out _, out _));
    }

    [Fact]
    public void ReturnsFalseWhenJournalStreamIsNotSet()
    {
        Assert.False(JournalStreamDetector.IsConnectedToJournal(null, _fileDescriptor));
    }

    [Fact]
    public void ReturnsFalseWhenJournalStreamIsMalformed()
    {
        Assert.False(JournalStreamDetector.IsConnectedToJournal("not-a-stream", _fileDescriptor));
    }

    [Fact]
    public void ReadsDeviceAndInodeOfFileDescriptor()
    {
        Assert.True(JournalStreamDetector.TryGetDeviceAndInode(_fileDescriptor, out var device, out var inode));

        Assert.NotEqual(0UL, inode);
        Assert.NotEqual(0UL, device);
    }

    [Fact]
    public void ReturnsFalseForInvalidFileDescriptor()
    {
        Assert.False(JournalStreamDetector.TryGetDeviceAndInode(-1, out _, out _));
    }

    [Fact]
    public void MatchesWhenDeviceAndInodeEqualFileDescriptor()
    {
        JournalStreamDetector.TryGetDeviceAndInode(_fileDescriptor, out var device, out var inode);
        var journalStream = $"{device}:{inode}";

        Assert.True(JournalStreamDetector.IsConnectedToJournal(journalStream, _fileDescriptor));
    }

    [Fact]
    public void DoesNotMatchWhenInodeDiffers()
    {
        JournalStreamDetector.TryGetDeviceAndInode(_fileDescriptor, out var device, out var inode);
        var journalStream = $"{device}:{inode + 1}";

        Assert.False(JournalStreamDetector.IsConnectedToJournal(journalStream, _fileDescriptor));
    }

    [Fact]
    public void DoesNotMatchWhenDeviceDiffers()
    {
        JournalStreamDetector.TryGetDeviceAndInode(_fileDescriptor, out var device, out var inode);
        var journalStream = $"{device + 1}:{inode}";

        Assert.False(JournalStreamDetector.IsConnectedToJournal(journalStream, _fileDescriptor));
    }
}
