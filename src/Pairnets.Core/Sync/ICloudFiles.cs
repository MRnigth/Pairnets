using System.Runtime.InteropServices;

namespace Pairnets.Core.Sync;

/// <summary>
/// The Pairnets folder as a Windows cloud folder (the way OneDrive works): Windows draws a tick, sync arrows or a cloud on
/// every file from what Pairnets tells it here, and with Files On-Demand a file can stay on the nest until it is opened.
/// The engine asks this whenever it would read, write or replace a file. <see cref="None"/> is a plain folder (Mac,
/// Linux, the switch off, or a drive where Windows cannot do it).
/// </summary>
public interface ICloudFiles
{
    /// <summary>A plain folder: every file is fully here and nothing is told to Windows.</summary>
    public static ICloudFiles None { get; } = new PlainFolder();

    /// <summary>True when this folder is a Windows cloud folder right now.</summary>
    bool IsActive { get; }

    /// <summary>
    /// True when the file is on the nest only (a placeholder without its content). Its content is then the version
    /// Pairnets last synced: reading it would only download it again, so the engine never does.
    /// </summary>
    bool IsCloudOnly(string fullPath);

    /// <summary>Tells Windows the file or folder is the same here and on the nest (it gets its tick).</summary>
    void MarkSynced(string fullPath);

    /// <summary>
    /// Files On-Demand: true when a file from the nest should arrive as a placeholder (listed, but downloaded only when
    /// opened) instead of being downloaded now.
    /// </summary>
    bool KeepOnNest(string fullPath);

    /// <summary>
    /// Puts a placeholder for one version of a file on the nest at <paramref name="fullPath"/> (or updates the one there):
    /// its size and time show, its content stays on the nest until opened. False when it could not.
    /// </summary>
    bool PlaceOnline(string fullPath, string hash, long size, DateTime modifiedUtc);

    private sealed class PlainFolder : ICloudFiles
    {
        public bool IsActive => false;

        public bool IsCloudOnly(string fullPath) => false;

        public void MarkSynced(string fullPath)
        {
        }

        public bool KeepOnNest(string fullPath) => false;

        public bool PlaceOnline(string fullPath, string hash, long size, DateTime modifiedUtc) => false;
    }
}

/// <summary>
/// What Windows' cloud files look like from outside: a placeholder is a reparse point of the cloud kind, and one whose
/// content is still on the nest carries the "recall on data access" flag. Plain checks of a file's details, so the
/// scanner can use them on any folder (they are false off Windows).
/// </summary>
public static class CloudPlaceholders
{
    /// <summary>FILE_ATTRIBUTE_RECALL_ON_DATA_ACCESS: reading the file's content would fetch it first.</summary>
    public const FileAttributes RecallOnDataAccess = (FileAttributes)0x00400000;

    /// <summary>FILE_ATTRIBUTE_RECALL_ON_OPEN: opening it at all would fetch it first.</summary>
    public const FileAttributes RecallOnOpen = (FileAttributes)0x00040000;

    /// <summary>FILE_ATTRIBUTE_PINNED: "Always keep on this device".</summary>
    public const FileAttributes Pinned = (FileAttributes)0x00080000;

    /// <summary>FILE_ATTRIBUTE_UNPINNED: "Free up space".</summary>
    public const FileAttributes Unpinned = (FileAttributes)0x00100000;

    /// <summary>The content is on the nest only (the file manager shows a cloud).</summary>
    public static bool IsContentOnline(FileAttributes attributes) =>
        (attributes & (RecallOnDataAccess | RecallOnOpen | FileAttributes.Offline)) != 0;

    /// <summary>
    /// True when <paramref name="fullPath"/> is a reparse point of Windows' cloud files (IO_REPARSE_TAG_CLOUD_*), which
    /// the scanner treats as an ordinary file or folder; every other reparse point (links, junctions) it skips.
    /// </summary>
    public static bool IsCloudReparsePoint(string fullPath)
    {
        if (!OperatingSystem.IsWindows())
            return false;
        var handle = FindFirstFileExW(fullPath, 1 /* FindExInfoBasic */, out var data, 0, IntPtr.Zero, 0);
        if (handle == new IntPtr(-1))
            return false;
        FindClose(handle);
        return (data.Attributes & (uint)FileAttributes.ReparsePoint) != 0 && (data.Reserved0 & 0xFFFF0FFF) == 0x9000001A;
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct FindData
    {
        public uint Attributes;
        public uint CreationLow, CreationHigh; // FILETIMEs are two 32-bit halves (no 8-byte alignment)
        public uint AccessLow, AccessHigh;
        public uint WriteLow, WriteHigh;
        public uint SizeHigh;
        public uint SizeLow;
        public uint Reserved0; // the reparse tag
        public uint Reserved1;

        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 260)]
        public string FileName;

        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 14)]
        public string AlternateName;
    }

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern IntPtr FindFirstFileExW(string name, int infoLevel, out FindData data, int searchOp, IntPtr filter, int flags);

    [DllImport("kernel32.dll")]
    private static extern bool FindClose(IntPtr handle);
}
