using System.Runtime.InteropServices;
using Microsoft.Win32;
using Pairnets.Core.Client;

namespace Pairnets.Client.Platform;

/// <summary>What Pairnets adds to File Explorer: where the folder is, and where its icons and the program are.</summary>
/// <param name="Folder">The Pairnets folder (shown as "Pairnets" in Explorer's left side).</param>
/// <param name="ExePath">Pairnets.exe, which the right-click items start.</param>
/// <param name="AppIcon">The Pairnets icon (.ico) for the left side and the right-click items.</param>
/// <param name="FolderIcon">The blue Pairnets folder icon (.ico) for the folder itself.</param>
/// <param name="Name">The nest's name ("Work nest"): a second nest's folder shows as "Pairnets – Work nest".</param>
/// <param name="Key">The app's id for the nest, which keeps a second nest's left-side entry the same from start to start.</param>
public sealed record ExplorerEntry(string Folder, string ExePath, string AppIcon, string FolderIcon, string? Name = null, string? Key = null);

/// <summary>
/// Pairnets in File Explorer, the way Microsoft describes for cloud storage apps
/// (learn.microsoft.com/windows/win32/shell/integrate-cloud-storage), all in the current user's registry part, so no
/// admin rights are needed and nothing changes for other people on the PC:
/// <list type="bullet">
/// <item>"Pairnets" with its icon in the left side, under OneDrive (a shell folder that points at the Pairnets folder);</item>
/// <item>"Open in Pairnets" and "Show sync status" when right-clicking files and folders in the Pairnets folder
/// (Windows 11 lists them under "Show more options");</item>
/// <item>the blue Pairnets folder icon on the folder (a hidden desktop.ini, which Pairnets never syncs).</item>
/// </list>
/// None of it touches the files. <see cref="Remove"/> takes it all away again (the uninstaller runs it).
/// </summary>
public static class ExplorerShell
{
    /// <summary>Pairnets' own shell folder id. Never change it: the uninstaller of every older version removes this one.</summary>
    public const string FolderClsid = "{6A3F1C52-8E0B-4D57-9C2E-5B7D1F04A9E3}";

    private const string FileSystemFolderClsid = "{0E5AAE11-A475-4c5b-AB00-C66DE400274E}";
    private const string HideOnDesktopKey = @"Microsoft\Windows\CurrentVersion\Explorer\HideDesktopIcons\NewStartPanel";

    /// <summary>The right-click items: on files (*) and on folders (Directory).</summary>
    private static readonly string[] VerbParents = [@"Classes\*\shell", @"Classes\Directory\shell"];

    private const string OpenVerb = "Pairnets.Open";
    private const string StatusVerb = "Pairnets.Status";

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
    public static bool Apply(RegistryKey software, ExplorerEntry entry, bool leftSide = true) => Apply(software, [entry], leftSide);

    /// <summary>
    /// <see cref="Apply(RegistryKey, ExplorerEntry, bool)"/> for every nest's folder (docs/DECISIONS.md, "Several nests at
    /// once"): the first is "Pairnets" in the left side, each other one "Pairnets – Work nest" just under it, the right-click
    /// items work inside all of them, and every folder gets the blue icon. Entries of nests that left go away.
    /// </summary>
    public static bool Apply(RegistryKey software, IReadOnlyList<ExplorerEntry> entries, bool leftSide = true)
    {
        if (entries.Count == 0)
        {
            Remove(software, RegisteredFolder(software));
            return true;
        }
        var folders = entries.Select(e => Path.TrimEndingDirectorySeparator(Path.GetFullPath(e.Folder))).ToList();
        var extras = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        if (leftSide)
        {
            AddLeftSide(software, entries[0], folders[0], FolderClsid, "Pairnets", 0x43);
            for (var i = 1; i < entries.Count; i++)
            {
                var clsid = ExtraClsid(entries[i].Key ?? folders[i]);
                AddLeftSide(software, entries[i], folders[i], clsid, "Pairnets – " + (entries[i].Name ?? Path.GetFileName(folders[i])), 0x43 + i);
                extras[clsid] = folders[i];
            }
        }
        else
        {
            RemoveLeftSide(software, FolderClsid);
        }
        foreach (var gone in ExtraEntries(software).Keys.Where(k => !extras.ContainsKey(k)).ToList())
            RemoveLeftSide(software, gone);
        using (var list = software.CreateSubKey(ExtrasKey))
        {
            foreach (var name in list.GetValueNames())
                list.DeleteValue(name, throwOnMissingValue: false);
            foreach (var (clsid, folder) in extras)
                list.SetValue(clsid, folder);
        }

        foreach (var parent in VerbParents)
        {
            AddVerb(software, parent + BS + OpenVerb, "Open in Pairnets", entries[0], folders, FileManagerCommand.OpenArgument);
            AddVerb(software, parent + BS + StatusVerb, "Show sync status", entries[0], folders, FileManagerCommand.StatusArgument);
        }
        var ok = true;
        for (var i = 0; i < entries.Count; i++)
            ok &= SetFolderIcon(folders[i], entries[i].FolderIcon);
        return ok;
    }

    /// <summary>The left-side entries of the second and later nests (id → folder), as kept next to the right-click items.</summary>
    private const string ExtrasKey = @"Pairnets\ExplorerNests";

    private static Dictionary<string, string> ExtraEntries(RegistryKey software)
    {
        using var list = software.OpenSubKey(ExtrasKey);
        return list?.GetValueNames().ToDictionary(n => n, n => list.GetValue(n) as string ?? "", StringComparer.OrdinalIgnoreCase)
            ?? new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
    }

    /// <summary>A second nest's own shell folder id, the same every time for the same nest.</summary>
    public static string ExtraClsid(string key)
    {
        var hash = System.Security.Cryptography.SHA256.HashData(System.Text.Encoding.UTF8.GetBytes("Pairnets nest folder " + key));
        hash[7] = (byte)((hash[7] & 0x0F) | 0x50); // a name-based GUID's version and variant bits
        hash[8] = (byte)((hash[8] & 0x3F) | 0x80);
        return new Guid(hash.AsSpan(0, 16)).ToString("B").ToUpperInvariant();
    }

    private const string BS = @"\";

    private static void AddLeftSide(RegistryKey software, ExplorerEntry entry, string folder, string id, string title, int order)
    {
        using (var clsid = software.CreateSubKey(@"Classes\CLSID\" + id))
        {
            clsid.SetValue(string.Empty, title);
            clsid.SetValue("InfoTip", "Your Pairnets folder: the files synced with your other computers.");
            clsid.SetValue("System.IsPinnedToNameSpaceTree", 1, RegistryValueKind.DWord);
            clsid.SetValue("SortOrderIndex", order, RegistryValueKind.DWord); // just after OneDrive (0x42)
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
        using (var nameSpace = software.CreateSubKey(NameSpaceRoot + id))
            nameSpace.SetValue(string.Empty, title);
        using (var hide = software.CreateSubKey(HideOnDesktopKey))
            hide.SetValue(id, 1, RegistryValueKind.DWord); // in Explorer's left side only, not as a desktop icon
    }

    private const string NameSpaceRoot = @"Microsoft\Windows\CurrentVersion\Explorer\Desktop\NameSpace\";

    private static void RemoveLeftSide(RegistryKey software, string id)
    {
        software.DeleteSubKeyTree(@"Classes\CLSID\" + id, throwOnMissingSubKey: false);
        software.DeleteSubKeyTree(NameSpaceRoot + id, throwOnMissingSubKey: false);
        using (var hide = software.OpenSubKey(HideOnDesktopKey, writable: true))
            hide?.DeleteValue(id, throwOnMissingValue: false);
    }

    /// <summary>
    /// One right-click item. AppliesTo limits it to paths inside the Pairnets folder, so it never shows anywhere else.
    /// </summary>
    private static void AddVerb(RegistryKey software, string key, string text, ExplorerEntry entry, IReadOnlyList<string> folders, string argument)
    {
        using var verb = software.CreateSubKey(key);
        verb.SetValue("MUIVerb", text);
        verb.SetValue("Icon", entry.AppIcon);
        verb.SetValue("AppliesTo", string.Join(" OR ", folders.Select(folder =>
        {
            var inside = folder.EndsWith(BS, StringComparison.Ordinal) ? folder : folder + BS; // a drive root ("D:\\") ends in one already
            return $"System.ItemPathDisplay:~<\"{inside}\" OR System.ItemPathDisplay:=\"{folder}\"";
        })));
        verb.SetValue("MultiSelectModel", "Single");
        verb.SetValue(FolderValue, folders[0]);
        verb.SetValue(FoldersValue, string.Join("|", folders));
        using var command = verb.CreateSubKey("command");
        command.SetValue(string.Empty, $"\"{entry.ExePath}\" {argument} \"%1\"");
    }

    /// <summary>
    /// Takes away everything <see cref="Apply"/> added. <paramref name="folder"/> (when known) loses its Pairnets
    /// folder icon, if Pairnets set it. Safe to run when nothing is there.
    /// </summary>
    public static void Remove(RegistryKey software, string? folder)
    {
        var others = RegisteredFolders(software);
        RemoveLeftSide(software, FolderClsid);
        foreach (var extra in ExtraEntries(software).Keys)
            RemoveLeftSide(software, extra);
        software.DeleteSubKeyTree(ExtrasKey, throwOnMissingSubKey: false);
        foreach (var other in others.Where(o => !string.Equals(o, folder, StringComparison.OrdinalIgnoreCase)))
            RemoveFolderIcon(other);
        foreach (var parent in VerbParents)
        {
            software.DeleteSubKeyTree(parent + @"\" + OpenVerb, throwOnMissingSubKey: false);
            software.DeleteSubKeyTree(parent + @"\" + StatusVerb, throwOnMissingSubKey: false);
        }
        if (!string.IsNullOrWhiteSpace(folder))
            RemoveFolderIcon(folder);
    }

    /// <summary>The folder Pairnets is in Explorer for (its right-click items), or null when Pairnets is not in Explorer.</summary>
    public static string? RegisteredFolder(RegistryKey software)
    {
        using var verb = software.OpenSubKey(VerbParents[0] + BS + OpenVerb);
        return verb?.GetValue(FolderValue) as string;
    }

    /// <summary>Every folder Pairnets is in Explorer for (all nests'); empty when Pairnets is not in Explorer.</summary>
    public static IReadOnlyList<string> RegisteredFolders(RegistryKey software)
    {
        using var verb = software.OpenSubKey(VerbParents[0] + BS + OpenVerb);
        if (verb?.GetValue(FoldersValue) is string all && all.Length > 0)
            return all.Split('|', StringSplitOptions.RemoveEmptyEntries);
        return verb?.GetValue(FolderValue) is string one ? [one] : [];
    }

    /// <summary>The folder the right-click items are for, kept next to them (AppliesTo is a search and hard to read back).</summary>
    private const string FolderValue = "PairnetsFolder";

    /// <summary>All the nests' folders the right-click items are for, separated by "|" (a character no Windows path has).</summary>
    private const string FoldersValue = "PairnetsFolders";

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
