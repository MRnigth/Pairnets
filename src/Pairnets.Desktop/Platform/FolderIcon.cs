using System.Runtime.InteropServices;
using Pairnets.Core.Settings;

namespace Pairnets.Desktop.Platform;

/// <summary>
/// The blue Pairnets folder icon for the Mac and Linux file managers, and a note of which folder Pairnets last put in
/// the file manager, so a moved folder gets its plain look back.
/// </summary>
internal static class FolderIcon
{
    private static string Dir => Path.Combine(PairnetsPaths.LocalDir, "FileManager");

    private static string Note => Path.Combine(Dir, "folder.txt");

    /// <summary>The icon as a PNG file next to the sync notes (written out of the app when missing or changed).</summary>
    public static string Png
    {
        get
        {
            var path = Path.Combine(Dir, "pairnets-folder.png");
            using var source = typeof(FolderIcon).Assembly.GetManifestResourceStream("Pairnets.FolderIcon.png")
                ?? throw new IOException("The folder icon is missing from Pairnets.");
            using var copy = new MemoryStream();
            source.CopyTo(copy);
            var bytes = copy.ToArray();
            Directory.CreateDirectory(Dir);
            if (!File.Exists(path) || !File.ReadAllBytes(path).AsSpan().SequenceEqual(bytes))
                File.WriteAllBytes(path, bytes);
            return path;
        }
    }

    /// <summary>
    /// Runs <paramref name="apply"/> with the folder Pairnets was in the file manager for before (or null) and the one it
    /// should be for now (null: none), then notes the new one. False when it failed.
    /// </summary>
    public static bool Follow(string? folder, Func<string?, string?, bool> apply)
    {
        try
        {
            var before = File.Exists(Note) ? File.ReadAllText(Note).Trim() : null;
            var ok = apply(before is { Length: > 0 } ? before : null, folder);
            if (folder is null)
                File.Delete(Note);
            else
            {
                Directory.CreateDirectory(Dir);
                File.WriteAllText(Note, folder);
            }
            return ok;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return false;
        }
    }
}

/// <summary>A folder's own icon in Finder: [[NSWorkspace sharedWorkspace] setIcon:forFile:options:] (nil puts the plain one back).</summary>
internal static class MacFolderIcon
{
    private const string ObjC = "/usr/lib/libobjc.A.dylib";

    [DllImport(ObjC)]
    private static extern IntPtr objc_getClass(string name);

    [DllImport(ObjC)]
    private static extern IntPtr sel_registerName(string name);

    [DllImport(ObjC, EntryPoint = "objc_msgSend")]
    private static extern IntPtr Send(IntPtr receiver, IntPtr selector);

    [DllImport(ObjC, EntryPoint = "objc_msgSend")]
    private static extern IntPtr Send(IntPtr receiver, IntPtr selector, IntPtr arg);

    [DllImport(ObjC, EntryPoint = "objc_msgSend")]
    private static extern IntPtr SendString(IntPtr receiver, IntPtr selector, [MarshalAs(UnmanagedType.LPUTF8Str)] string arg);

    [DllImport(ObjC, EntryPoint = "objc_msgSend")]
    [return: MarshalAs(UnmanagedType.I1)]
    private static extern bool SendSetIcon(IntPtr receiver, IntPtr selector, IntPtr image, IntPtr path, nuint options);

    /// <summary>Gives <paramref name="folder"/> the icon in <paramref name="png"/>, or its plain icon back for null.</summary>
    public static bool Set(string folder, string? png)
    {
        try
        {
            var pool = Send(Send(objc_getClass("NSAutoreleasePool"), sel_registerName("alloc")), sel_registerName("init"));
            try
            {
                var image = IntPtr.Zero;
                if (png is not null)
                {
                    var nsPng = SendString(objc_getClass("NSString"), sel_registerName("stringWithUTF8String:"), png);
                    image = Send(Send(Send(objc_getClass("NSImage"), sel_registerName("alloc")), sel_registerName("initWithContentsOfFile:"), nsPng),
                        sel_registerName("autorelease"));
                    if (image == IntPtr.Zero)
                        return false;
                }
                var nsFolder = SendString(objc_getClass("NSString"), sel_registerName("stringWithUTF8String:"), folder);
                var workspace = Send(objc_getClass("NSWorkspace"), sel_registerName("sharedWorkspace"));
                return workspace != IntPtr.Zero && SendSetIcon(workspace, sel_registerName("setIcon:forFile:options:"), image, nsFolder, 0);
            }
            finally
            {
                Send(pool, sel_registerName("drain"));
            }
        }
        catch (Exception ex) when (ex is DllNotFoundException or EntryPointNotFoundException or MarshalDirectiveException)
        {
            return false;
        }
    }
}
