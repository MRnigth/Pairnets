using System.Runtime.InteropServices;
using Microsoft.Win32;
using Pairnets.Core.Client;

namespace Pairnets.Client.Platform;

/// <summary>What Pairnets adds to File Explorer: where the folder is, and where its icons and the program are.</summary>
/// <param name="Folder">The Pairnets folder (shown as "Pairnets" in Explorer's left side).</param>
/// <param name="ExePath">Pairnets.exe, which the right-click items start.</param>
/// <param name="AppIcon">The Pairnets icon (.ico) for the left side and the right-click items.</param>
/// <param name="FolderIcon">The blue Pairnets folder icon (.ico) for the folder itself.</param>
public sealed record ExplorerEntry(string Folder, string ExePath, string AppIcon, string FolderIcon);

/// <summary>
/// Pairnets in File Explorer, the way Microsoft describes for cloud storage apps
/// (learn.microsoft.com/windows/win32/shell/integrate-cloud-storage), all in the current user's registry part, so no
/// admin rights are needed and nothing changes for other people on the PC:
/// <list type="bullet">
/// <item>"Pairnets" with its icon in the left side, under OneDrive (a shell folder that points at the Pairnets folder);</item>
/// <item>"Open in Pairnets" and "Show sync status" when right-clicking files and folders in the Pairnets folder, and
/// "Where this folder lives…" on its folders (Windows 11 lists them under "Show more options");</item>
/// <item>the blue Pairnets folder icon on the folder (a hidden desktop.ini, which Pairnets never syncs).</item>
/// </list>
/// None of it touches the files. <see cref="Remove"/> takes it all away again (the uninstaller runs it).
/// </summary>
public static class ExplorerShell
{
    /// <summary>Pairnets' own shell folder id. Never change it: the uninstaller of every older version removes this one.</summary>
    public const string FolderClsid = "{6A3F1C52-8E0B-4D57-9C2E-5B7D1F04A9E3}";

    private const string FileSystemFolderClsid = "{0E5AAE11-A475-4c5b-AB00-C66DE400274E}";
    private const string ClsidKey = @"Classes\CLSID\" + FolderClsid;
    private const string NameSpaceKey = @"Microsoft\Windows\CurrentVersion\Explorer\Desktop\NameSpace\" + FolderClsid;
    private const string HideOnDesktopKey = @"Microsoft\Windows\CurrentVersion\Explorer\HideDesktopIcons\NewStartPanel";

    /// <summary>The right-click items: on files (*) and on folders (Directory).</summary>
    private static readonly string[] VerbParents = [@"Classes\*\shell", @"Classes\Directory\shell"];

    private const string OpenVerb = "Pairnets.Open";
    private const string StatusVerb = "Pairnets.Status";
    private const string WhereVerb = "Pairnets.Where";

    /// <summary>"Where this folder lives…": on folders only.</summary>
    private const string FolderVerbParent = @"Classes\Directory\shell";

    /// <summary>First line of the desktop.ini Pairnets writes, so it never takes away one it did not write.</summary>
    public const string DesktopIniMark = "; Pairnets folder icon. Removed again when Pairnets leaves Explorer.";

    /// <summary>
    /// Adds (or brings up to date) everything, below <paramref name="software"/> (HKEY_CURRENT_USER\Software in the app;
    /// a throwaway key in tests). False when the folder icon could not be set; the rest is in place either way.
    /// </summary>
    /// <param name="leftSide">
    /// False while the folder is a Windows cloud folder: Windows then shows it in the left side by itself, and a second
    /// "Pairnets" there would be one too many.
    /// </param>
    /// <param name="rightClick">False: no right-click items (the person turned them off at install or in Settings).</param>
    /// <param name="folderIcon">False: the folder keeps its plain icon.</param>
    public static bool Apply(RegistryKey software, ExplorerEntry entry, bool leftSide = true, bool rightClick = true, bool folderIcon = true)
    {
        var folder = Path.TrimEndingDirectorySeparator(Path.GetFullPath(entry.Folder));
        if (leftSide)
            AddLeftSide(software, entry, folder);
        else
            RemoveLeftSide(software);

        using (var own = software.CreateSubKey(OwnKey))
            own.SetValue(FolderValue, folder);
        if (rightClick)
        {
            foreach (var parent in VerbParents)
            {
                AddVerb(software, parent + BS + OpenVerb, "Open in Pairnets", entry, folder, FileManagerCommand.OpenArgument);
                AddVerb(software, parent + BS + StatusVerb, "Show sync status", entry, folder, FileManagerCommand.StatusArgument);
            }
            AddVerb(software, FolderVerbParent + BS + WhereVerb, "Where this folder lives…", entry, folder, FileManagerCommand.WhereArgument);
        }
        else
        {
            RemoveVerbs(software);
        }
        if (!folderIcon)
        {
            RemoveFolderIcon(folder);
            return true;
        }
        return SetFolderIcon(folder, entry.FolderIcon);
    }

    private const string BS = @"\";

    private static void AddLeftSide(RegistryKey software, ExplorerEntry entry, string folder)
    {
        using (var clsid = software.CreateSubKey(ClsidKey))
        {
            clsid.SetValue(string.Empty, "Pairnets");
            clsid.SetValue("InfoTip", "Your Pairnets folder: the files synced with your other computers.");
            clsid.SetValue("System.IsPinnedToNameSpaceTree", 1, RegistryValueKind.DWord);
            clsid.SetValue("SortOrderIndex", 0x43, RegistryValueKind.DWord); // just after OneDrive (0x42)
            using (var icon = clsid.CreateSubKey("DefaultIcon"))
                icon.SetValue(string.Empty, entry.AppIcon + ",0");
            using (var server = clsid.CreateSubKey("InProcServer32"))
                server.SetValue(string.Empty, @"%SystemRoot%\system32\shell32.dll", RegistryValueKind.ExpandString);
            using (var instance = clsid.CreateSubKey("Instance"))
                instance.SetValue("CLSID", FileSystemFolderClsid);
            using (var bag = clsid.CreateSubKey(@"Instance\InitPropertyBag"))
            {
                bag.SetValue("Attributes", 0x11, RegistryValueKind.DWord); // a folder that is read-only as a shortcut, not its files
                // Microsoft's example keeps the path expandable; a '%' in a real folder name must then stay as it is.
                bag.SetValue("TargetFolderPath", folder, folder.Contains('%') ? RegistryValueKind.String : RegistryValueKind.ExpandString);
            }
            using (var shellFolder = clsid.CreateSubKey("ShellFolder"))
            {
                shellFolder.SetValue("FolderValueFlags", 0x28, RegistryValueKind.DWord);
                shellFolder.SetValue("Attributes", unchecked((int)0xF080004D), RegistryValueKind.DWord);
            }
        }
        using (var nameSpace = software.CreateSubKey(NameSpaceKey))
            nameSpace.SetValue(string.Empty, "Pairnets");
        using (var hide = software.CreateSubKey(HideOnDesktopKey))
            hide.SetValue(FolderClsid, 1, RegistryValueKind.DWord); // in Explorer's left side only, not as a desktop icon
    }

    private static void RemoveLeftSide(RegistryKey software)
    {
        software.DeleteSubKeyTree(ClsidKey, throwOnMissingSubKey: false);
        software.DeleteSubKeyTree(NameSpaceKey, throwOnMissingSubKey: false);
        using (var hide = software.OpenSubKey(HideOnDesktopKey, writable: true))
            hide?.DeleteValue(FolderClsid, throwOnMissingValue: false);
    }

    /// <summary>
    /// One right-click item. AppliesTo limits it to paths inside the Pairnets folder, so it never shows anywhere else.
    /// </summary>
    private static void AddVerb(RegistryKey software, string key, string text, ExplorerEntry entry, string folder, string argument)
    {
        using var verb = software.CreateSubKey(key);
        verb.SetValue("MUIVerb", text);
        verb.SetValue("Icon", entry.AppIcon);
        var inside = folder.EndsWith(BS, StringComparison.Ordinal) ? folder : folder + BS; // a drive root ("D:\\") ends in one already
        verb.SetValue("AppliesTo", $"System.ItemPathDisplay:~<\"{inside}\" OR System.ItemPathDisplay:=\"{folder}\"");
        verb.SetValue("MultiSelectModel", "Single");
        verb.SetValue(FolderValue, folder);
        using var command = verb.CreateSubKey("command");
        command.SetValue(string.Empty, $"\"{entry.ExePath}\" {argument} \"%1\"");
    }

    /// <summary>
    /// Takes away everything <see cref="Apply"/> added. <paramref name="folder"/> (when known) loses its Pairnets
    /// folder icon, if Pairnets set it. Safe to run when nothing is there.
    /// </summary>
    public static void Remove(RegistryKey software, string? folder)
    {
        RemoveLeftSide(software);
        RemoveVerbs(software);
        software.DeleteSubKeyTree(OwnKey, throwOnMissingSubKey: false);
        if (!string.IsNullOrWhiteSpace(folder))
            RemoveFolderIcon(folder);
    }

    private static void RemoveVerbs(RegistryKey software)
    {
        foreach (var parent in VerbParents)
        {
            software.DeleteSubKeyTree(parent + BS + OpenVerb, throwOnMissingSubKey: false);
            software.DeleteSubKeyTree(parent + BS + StatusVerb, throwOnMissingSubKey: false);
        }
        software.DeleteSubKeyTree(FolderVerbParent + BS + WhereVerb, throwOnMissingSubKey: false);
    }

    /// <summary>The folder Pairnets is in Explorer for, or null when Pairnets is not in Explorer.</summary>
    public static string? RegisteredFolder(RegistryKey software)
    {
        using (var own = software.OpenSubKey(OwnKey))
        {
            if (own?.GetValue(FolderValue) is string folder)
                return folder;
        }
        using var verb = software.OpenSubKey(VerbParents[0] + BS + OpenVerb); // put there by versions before 1.0.60
        return verb?.GetValue(FolderValue) as string;
    }

    /// <summary>Pairnets' own key, which remembers the folder also when there are no right-click items.</summary>
    private const string OwnKey = @"Pairnets\Explorer";

    /// <summary>The folder the right-click items are for, kept next to them (AppliesTo is a search and hard to read back).</summary>
    private const string FolderValue = "PairnetsFolder";

    /// <summary>
    /// The blue folder icon: a hidden desktop.ini in the folder (Pairnets never syncs desktop.ini) and the folder's
    /// read-only flag, which is how Windows knows to look for it. A desktop.ini Pairnets did not write is left alone.
    /// </summary>
    public static bool SetFolderIcon(string folder, string icon)
    {
        var ini = Path.Combine(folder, "desktop.ini");
        try
        {
            if (!Directory.Exists(folder))
                return false;
            if (File.Exists(ini) && !IsOurs(ini))
                return false;
            var text = DesktopIniMark + "\r\n[.ShellClassInfo]\r\nIconResource=" + icon + ",0\r\n";
            if (!File.Exists(ini) || File.ReadAllText(ini) != text)
            {
                if (File.Exists(ini))
                    File.SetAttributes(ini, FileAttributes.Normal);
                File.WriteAllText(ini, text, System.Text.Encoding.Unicode);
            }
            File.SetAttributes(ini, FileAttributes.Hidden | FileAttributes.System);
            var dir = new DirectoryInfo(folder);
            if ((dir.Attributes & FileAttributes.ReadOnly) == 0)
                dir.Attributes |= FileAttributes.ReadOnly;
            return true;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return false;
        }
    }

    /// <summary>Takes the Pairnets folder icon away again (only the desktop.ini Pairnets wrote).</summary>
    public static void RemoveFolderIcon(string folder)
    {
        var ini = Path.Combine(folder, "desktop.ini");
        try
        {
            if (!File.Exists(ini) || !IsOurs(ini))
                return;
            File.SetAttributes(ini, FileAttributes.Normal);
            File.Delete(ini);
            var dir = new DirectoryInfo(folder);
            dir.Attributes &= ~FileAttributes.ReadOnly;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // The icon stays; nothing else depends on it.
        }
    }

    private static bool IsOurs(string ini)
    {
        try
        {
            using var reader = new StreamReader(ini, detectEncodingFromByteOrderMarks: true);
            return reader.ReadLine() == DesktopIniMark;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return false;
        }
    }

    /// <summary>Asks Explorer to redraw, so the left side and icons change at once instead of after a sign-out.</summary>
    public static void TellExplorer()
    {
        try
        {
            SHChangeNotify(0x08000000 /* SHCNE_ASSOCCHANGED */, 0x0000 /* SHCNF_IDLIST */, IntPtr.Zero, IntPtr.Zero);
        }
        catch (Exception ex) when (ex is EntryPointNotFoundException or DllNotFoundException)
        {
            // Explorer picks the change up the next time it starts.
        }
    }

    [DllImport("shell32.dll")]
    private static extern void SHChangeNotify(int eventId, int flags, IntPtr item1, IntPtr item2);
}
