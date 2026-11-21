using System.Diagnostics.CodeAnalysis;
using System.Globalization;
using System.Runtime.InteropServices;

namespace Serilog.Sinks.Journald;

internal static partial class JournalStreamDetector
{
    private const int StandardErrorFileDescriptor = 2;
    private const string JournalStreamVariable = "JOURNAL_STREAM";

    private const int StatBufferSize = 256;

    public static bool IsStderrConnectedToJournal()
    {
        return IsConnectedToJournal(Environment.GetEnvironmentVariable(JournalStreamVariable), StandardErrorFileDescriptor);
    }

    internal static bool IsConnectedToJournal(string? journalStream, int fileDescriptor)
    {
        if (!TryParseJournalStream(journalStream, out var device, out var inode))
        {
            return false;
        }

        if (!TryGetDeviceAndInode(fileDescriptor, out var actualDevice, out var actualInode))
        {
            return false;
        }

        return device == actualDevice && inode == actualInode;
    }

    internal static bool TryParseJournalStream(string? value, out ulong device, out ulong inode)
    {
        device = 0;
        inode = 0;

        if (string.IsNullOrEmpty(value))
        {
            return false;
        }

        var parts = value.Split(':');
        return parts.Length == 2
               && ulong.TryParse(parts[0], NumberStyles.None, CultureInfo.InvariantCulture, out device)
               && ulong.TryParse(parts[1], NumberStyles.None, CultureInfo.InvariantCulture, out inode);
    }

    internal static unsafe bool TryGetDeviceAndInode(int fileDescriptor, out ulong device, out ulong inode)
    {
        device = 0;
        inode = 0;

        if (!OperatingSystem.IsLinux())
        {
            return false;
        }

        var buffer = stackalloc byte[StatBufferSize];

        try
        {
            if (fstat(fileDescriptor, buffer) != 0)
            {
                return false;
            }
        }
        catch (DllNotFoundException)
        {
            return false;
        }
        catch (EntryPointNotFoundException)
        {
            return false;
        }

        var stat = new ReadOnlySpan<byte>(buffer, StatBufferSize);
        device = BitConverter.ToUInt64(stat);
        inode = BitConverter.ToUInt64(stat[8..]);
        return true;
    }

    [LibraryImport("libc", EntryPoint = "fstat", SetLastError = true)]
    [SuppressMessage("ReSharper", "InconsistentNaming")]
    private static unsafe partial int fstat(int fileDescriptor, byte* buffer);
}
