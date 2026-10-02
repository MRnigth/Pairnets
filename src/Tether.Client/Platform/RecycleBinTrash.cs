using System.Runtime.InteropServices;
using Microsoft.Extensions.Logging;
using Tether.Core.Sync;

namespace Tether.Client.Platform;

/// <summary>
/// Moves files deleted on the other PC to the Recycle Bin (silently). Falls back to a permanent
/// delete when that is impossible; the server still keeps the content in its history.
/// </summary>
public sealed class RecycleBinTrash(ILogger log) : ILocalTrash
{
    private const uint FO_DELETE = 0x0003;
    private const ushort FOF_SILENT = 0x0004;
    private const ushort FOF_NOCONFIRMATION = 0x0010;
    private const ushort FOF_ALLOWUNDO = 0x0040;
    private const ushort FOF_NOERRORUI = 0x0400;

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct SHFILEOPSTRUCT
    {
        public IntPtr hwnd;
        public uint wFunc;
        public string pFrom;
        public string? pTo;
        public ushort fFlags;
        [MarshalAs(UnmanagedType.Bool)] public bool fAnyOperationsAborted;
        public IntPtr hNameMappings;
        public string? lpszProgressTitle;
    }

    [DllImport("shell32.dll", CharSet = CharSet.Unicode)]
    private static extern int SHFileOperation(ref SHFILEOPSTRUCT lpFileOp);

    public void Delete(string fullPath)
    {
        if (fullPath.Length < 260)
        {
            var op = new SHFILEOPSTRUCT
            {
                wFunc = FO_DELETE,
                pFrom = fullPath + "\0\0",
                fFlags = (ushort)(FOF_ALLOWUNDO | FOF_NOCONFIRMATION | FOF_NOERRORUI | FOF_SILENT),
            };
            var rc = SHFileOperation(ref op);
            if (rc == 0 && !op.fAnyOperationsAborted && !File.Exists(fullPath))
                return;
            log.LogWarning("Recycle Bin move failed for {Path} (code {Code}); deleting permanently (the server keeps history)", fullPath, rc);
        }
        File.Delete(fullPath);
    }
}
