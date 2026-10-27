using System.Security;
using Pairnets.Core.Paths;

namespace Pairnets.Core.Settings;

/// <summary>
/// Whether a folder can be a nest's folder on this computer at all: not a folder the system keeps for itself
/// (C:\Program Files, C:\Windows, /usr...), where only programs with admin rights may save files, and a folder
/// Pairnets can actually save files in. Both sign-in screens, Settings and "Choose another folder" ask it.
/// </summary>
public static class SyncFolderCheck
{
    /// <summary>The system's own folders on this computer: Program Files (both), Windows; on Linux and Mac the system folders.</summary>
    public static IReadOnlyList<string> SystemFolders()
    {
        if (!OperatingSystem.IsWindows())
            return ["/usr", "/bin", "/sbin", "/etc", "/lib", "/lib64", "/boot", "/proc", "/sys", "/dev", "/System"];
        var list = new List<string>();
        foreach (var path in new[]
                 {
                     Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles),
                     Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86),
                     Environment.GetEnvironmentVariable("ProgramW6432"),
                     Environment.GetEnvironmentVariable("ProgramFiles(x86)"),
                     Environment.GetFolderPath(Environment.SpecialFolder.Windows),
                 })
        {
            if (!string.IsNullOrWhiteSpace(path) && !list.Contains(path, StringComparer.OrdinalIgnoreCase))
                list.Add(path);
        }
        return list;
    }

    /// <summary>The system folder <paramref name="folder"/> is (or is inside), or null.</summary>
    public static string? SystemFolderOf(string folder, IEnumerable<string>? systemFolders = null)
    {
        string mine;
        try
        {
            mine = Normal(folder);
        }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException)
        {
            return null;
        }
        foreach (var system in systemFolders ?? SystemFolders())
        {
            var theirs = Normal(system);
            var withSlash = Path.EndsInDirectorySeparator(theirs) ? theirs : theirs + Path.DirectorySeparatorChar;
            if (string.Equals(mine, theirs, Cmp) || mine.StartsWith(withSlash, Cmp))
                return theirs;
        }
        return null;
    }

    /// <summary>
    /// True when Pairnets can save a file in <paramref name="folder"/> (it writes and removes a small test file). A folder
    /// that isn't there yet is tested by making it in the nearest folder above that is; a drive that isn't there at all
    /// counts as writable here (the folder-missing card handles that).
    /// </summary>
    public static bool CanWrite(string folder)
    {
        try
        {
            var full = Path.GetFullPath(folder);
            if (Directory.Exists(full))
            {
                var probe = Path.Combine(full, PathRules.ProbeFileName);
                using (new FileStream(probe, FileMode.Create, FileAccess.Write, FileShare.None, 1, FileOptions.DeleteOnClose))
                {
                }
                return true;
            }
            var parent = Path.GetDirectoryName(Path.TrimEndingDirectorySeparator(full));
            while (parent is not null && !Directory.Exists(parent))
                parent = Path.GetDirectoryName(parent);
            if (parent is null)
                return true;
            var test = Path.Combine(parent, PathRules.ProbeFileName + "-" + Guid.NewGuid().ToString("N")[..8]);
            Directory.CreateDirectory(test);
            Directory.Delete(test);
            return true;
        }
        catch (Exception ex) when (ex is UnauthorizedAccessException or SecurityException or IOException)
        {
            return false;
        }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException)
        {
            return true; // not a path at all: the screens say so themselves
        }
    }

    /// <summary>
    /// Why <paramref name="folder"/> can't be a nest's folder on this computer, in words for a person, or null when it can.
    /// <paramref name="suggested"/> is the folder to offer instead (by default Pairnets in the person's home folder).
    /// </summary>
    public static string? Problem(string folder, string? suggested = null, IEnumerable<string>? systemFolders = null)
    {
        suggested ??= DefaultFolder;
        if (SystemFolderOf(folder, systemFolders) is { } system)
            return OperatingSystem.IsWindows()
                ? $"Windows protects {system}: only programs with admin rights can save files there, so Pairnets can't sync it. Choose a folder of your own, for example {suggested}."
                : $"{system} is a system folder: only an administrator can save files there, so Pairnets can't sync it. Choose a folder of your own, for example {suggested}.";
        if (TooBroad(folder) is not null)
            return $"{Path.TrimEndingDirectorySeparator(folder.Trim())} is too big a folder to sync: it holds your whole account or Pairnets' own files, and syncing it would try to copy everything in it. Choose a folder of your own, for example {suggested}.";
        if (!CanWrite(folder))
            return $"This computer doesn't let Pairnets save files in {Path.TrimEndingDirectorySeparator(folder.Trim())}. Choose a folder of your own, for example {suggested}.";
        return null;
    }

    /// <summary>
    /// Folders that hold more than a person's own files: a drive's root, the folder of all accounts, the home folder, the
    /// Mac's Library and Applications, and the folders Pairnets keeps its own settings, state and logs in. Syncing one would
    /// copy everything in it, and (home folder) feed Pairnets' own writes back into its own watcher.
    /// </summary>
    public static IReadOnlyList<string> BroadFolders()
    {
        var list = new List<string>();
        var home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        if (!string.IsNullOrWhiteSpace(home))
        {
            list.Add(home);
            if (OperatingSystem.IsMacOS())
                list.Add(Path.Combine(home, "Library"));
            var accounts = Path.GetDirectoryName(Path.TrimEndingDirectorySeparator(home));
            if (!string.IsNullOrEmpty(accounts))
                list.Add(accounts); // /Users, /home, C:\Users
        }
        if (OperatingSystem.IsMacOS())
            list.AddRange(["/Volumes", "/Library", "/Applications", "/Users", "/private", "/opt", "/var", "/tmp"]);
        else if (OperatingSystem.IsLinux())
            list.AddRange(["/home", "/mnt", "/media", "/var", "/opt", "/tmp", "/root"]);
        return list;
    }

    /// <summary>
    /// Why <paramref name="folder"/> is too broad (see <see cref="BroadFolders"/>), or null. A folder is too broad when it
    /// is one of those folders, a drive's root, inside the Mac's Library, or holds (or is inside) one of
    /// <paramref name="ownFolders"/> (default: Pairnets' settings and state folders).
    /// </summary>
    public static string? TooBroad(string folder, IEnumerable<string>? broad = null, IEnumerable<string>? ownFolders = null)
    {
        string mine;
        try
        {
            mine = Normal(folder);
        }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException)
        {
            return null;
        }
        var root = Path.GetPathRoot(mine);
        if (!string.IsNullOrEmpty(root) && string.Equals(Path.TrimEndingDirectorySeparator(root), Path.TrimEndingDirectorySeparator(mine), Cmp))
            return "a drive's root";
        foreach (var b in broad ?? BroadFolders())
        {
            var theirs = Normal(b);
            if (string.Equals(mine, theirs, Cmp))
                return theirs;
            // Inside the Mac's own Library folder (its Application Support holds Pairnets' state).
            if (OperatingSystem.IsMacOS() && broad is null && Path.GetFileName(theirs) == "Library" && IsInside(mine, theirs))
                return theirs;
        }
        foreach (var own in ownFolders ?? [PairnetsPaths.LocalDir, PairnetsPaths.RoamingDir])
        {
            var theirs = Normal(own);
            if (IsInside(mine, theirs) || IsInside(theirs, mine))
                return theirs;
        }
        return null;
    }

    private static bool IsInside(string path, string parent)
    {
        var withSlash = Path.EndsInDirectorySeparator(parent) ? parent : parent + Path.DirectorySeparatorChar;
        return path.StartsWith(withSlash, Cmp);
    }

    /// <summary>The folder to offer instead: Pairnets in the person's home folder.</summary>
    public static string DefaultFolder => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), PairnetsPaths.FolderName);

    private static string Normal(string path) => Path.TrimEndingDirectorySeparator(Path.GetFullPath(path.Trim()));

    private static StringComparison Cmp => OperatingSystem.IsWindows() || OperatingSystem.IsMacOS() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;
}
