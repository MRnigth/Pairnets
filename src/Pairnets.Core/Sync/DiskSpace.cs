using Microsoft.Win32.SafeHandles;
using System.Runtime.InteropServices;

namespace Pairnets.Core.Sync;

/// <summary>Free and total bytes on a drive.</summary>
public readonly record struct DiskSpaceInfo(long Free, long Total);

/// <summary>
/// Keeps a computer from being filled to the last byte by a download: a full system drive stops a Mac from swapping,
/// and the whole Mac then freezes or restarts.
/// </summary>
public static class DiskSpace
{
    /// <summary>Always leave this much free after a download (on a small drive 5% of it, if that is less).</summary>
    public const long ReserveBytes = 2L * 1024 * 1024 * 1024;

    /// <summary>Free space on the drive holding <paramref name="folder"/>, or null when it cannot be told.</summary>
    public static DiskSpaceInfo? Of(string folder)
    {
        try
        {
            var root = Path.GetPathRoot(Path.GetFullPath(folder));
            if (string.IsNullOrEmpty(root))
                return null;
            // The drive of a folder, not of "/": a Mac's data is on a volume of its own.
            var drive = new DriveInfo(OperatingSystem.IsWindows() ? root : folder);
            return new DiskSpaceInfo(drive.AvailableFreeSpace, drive.TotalSize);
        }
        catch (Exception ex) when (ex is IOException or ArgumentException or UnauthorizedAccessException or NotSupportedException)
        {
            return null;
        }
    }

    /// <summary>How much must stay free on a drive of <paramref name="total"/> bytes.</summary>
    public static long Reserve(long total) => Math.Min(ReserveBytes, total / 20);

    /// <summary>True when <paramref name="need"/> more bytes still leave the reserve free. Unknown space allows everything.</summary>
    public static bool Allows(DiskSpaceInfo? space, long need) =>
        space is not { } s || s.Free - Math.Max(need, 0) >= Reserve(s.Total);
}

/// <summary>A plain fsync on a Mac (what other apps do) instead of the full drive-cache flush .NET asks for there.</summary>
internal static class LightSync
{
    [DllImport("libc", EntryPoint = "fsync")]
    private static extern int Fsync(SafeFileHandle handle);

    /// <summary>
    /// Makes the file's data reach the drive. Windows and Linux: the usual flush to disk. A Mac: .NET's flush to disk is
    /// a full flush of the drive's own cache (F_FULLFSYNC), which stalls every program's disk use; with four to eight
    /// downloads at once and thousands of small files that is a storm. fsync keeps the data safe from an app crash.
    /// </summary>
    public static void Flush(FileStream fs)
    {
        if (!OperatingSystem.IsMacOS())
        {
            fs.Flush(flushToDisk: true);
            return;
        }
        fs.Flush(flushToDisk: false);
        try
        {
            _ = Fsync(fs.SafeFileHandle);
        }
        catch (Exception ex) when (ex is DllNotFoundException or EntryPointNotFoundException)
        {
            fs.Flush(flushToDisk: true);
        }
    }
}
