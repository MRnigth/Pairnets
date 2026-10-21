using System.Text;
using System.Text.Json;
using Pairnets.Core.Api;
using Pairnets.Core.Settings;

namespace Pairnets.Core.Client;

/// <summary>
/// "Program folder" in the Linux app's Settings: moves the app's own folder (normally ~/.local/opt/pairnets, see
/// deploy/linux-desktop/install-desktop.sh) to &lt;chosen folder&gt;/pairnets. It copies the folder, checks the copy,
/// points the pairnets command (~/.local/bin/pairnets), the app menu entry and the start-at-login entry at the copy, and
/// writes a note (<see cref="MarkerFile"/>). The app then starts the copy and quits; the copy removes the old folder at its
/// next start (<see cref="CleanUpOldCopy"/>), only when the note names it and it still holds the Pairnets program.
/// Like <see cref="SelfReplace"/>, anything that fails leaves the app where it was.
/// </summary>
public sealed class LinuxAppPlace
{
    /// <summary>The folder made inside the chosen one.</summary>
    public const string FolderName = "pairnets";

    /// <summary>The note in the app's local folder that lets the new copy remove the old one.</summary>
    public const string MarkerFile = "program-moved.json";

    /// <summary>Room left on the drive beyond the copy itself.</summary>
    public const long SpareBytes = 64L * 1024 * 1024;

    private readonly string _home;
    private readonly string _localDir;
    private readonly string _dataHome;
    private readonly string _configHome;

    /// <param name="appFile">The running app's own file (<see cref="ClientEnvironment.AppFile"/>).</param>
    /// <param name="home">The user's home folder.</param>
    /// <param name="localDir">The app's local folder (<see cref="PairnetsPaths.LocalDir"/>), where the note goes.</param>
    /// <param name="dataHome">XDG_DATA_HOME (default ~/.local/share): the app menu entry is in its applications folder.</param>
    /// <param name="configHome">XDG_CONFIG_HOME (default ~/.config): the start-at-login entry is in its autostart folder.</param>
    public LinuxAppPlace(string appFile, string home, string localDir, string? dataHome = null, string? configHome = null)
    {
        AppFile = Path.GetFullPath(appFile);
        AppDir = Path.GetDirectoryName(AppFile)!;
        _home = Path.TrimEndingDirectorySeparator(home);
        _localDir = localDir;
        _dataHome = string.IsNullOrWhiteSpace(dataHome) ? Path.Combine(_home, ".local", "share") : dataHome;
        _configHome = string.IsNullOrWhiteSpace(configHome) ? Path.Combine(_home, ".config") : configHome;
    }

    /// <summary>The running app's place, from the real environment.</summary>
    public static LinuxAppPlace? ForThisApp(ClientEnvironment env) =>
        env.AppFile is { Length: > 0 } app
            ? new LinuxAppPlace(app, Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), env.LocalDir,
                Environment.GetEnvironmentVariable("XDG_DATA_HOME"), Environment.GetEnvironmentVariable("XDG_CONFIG_HOME"))
            : null;

    public string AppFile { get; }

    public string AppDir { get; }

    /// <summary>"~/.local/opt/pairnets": the folder as the Settings row shows it.</summary>
    public string DisplayDir => Inside(AppDir, _home) ? "~" + AppDir[_home.Length..] : AppDir;

    /// <summary>~/.local/bin/pairnets, the command install-desktop.sh made.</summary>
    public string CommandLink => Path.Combine(_home, ".local", "bin", "pairnets");

    public string MenuEntry => Path.Combine(_dataHome, "applications", "pairnets.desktop");

    public string AutostartEntry => Path.Combine(_configHome, "autostart", "pairnets.desktop");

    /// <summary>The folder a move to <paramref name="chosen"/> makes.</summary>
    public static string TargetFor(string chosen) => Path.Combine(Path.TrimEndingDirectorySeparator(chosen), FolderName);

    /// <summary>
    /// Why the program can't go to <paramref name="chosen"/> (a plain sentence), or null when it can.
    /// <paramref name="mountInfo"/>: /proc/self/mountinfo (read here when null, on Linux).
    /// </summary>
    public string? Check(string chosen, string? mountInfo = null)
    {
        if (string.IsNullOrWhiteSpace(chosen) || !Path.IsPathFullyQualified(chosen))
            return "Choose a full folder path.";
        var target = TargetFor(Path.GetFullPath(chosen));
        if (Same(target, AppDir))
            return "Pairnets is already in that folder.";
        if (Inside(target, AppDir) || Inside(AppDir, target))
            return "Choose a folder outside Pairnets' own folder.";
        if (!Directory.Exists(chosen))
            return $"{chosen} does not exist.";
        if (File.Exists(target))
            return $"{target} is a file. Choose another folder.";
        if (Directory.Exists(target) && Directory.EnumerateFileSystemEntries(target).Any())
            return $"{target} is not empty. Choose another folder, or empty that one first.";
        if (!Writable(chosen))
            return $"Pairnets can't write in {chosen}.";
        mountInfo ??= OperatingSystem.IsLinux() && File.Exists("/proc/self/mountinfo") ? SafeRead("/proc/self/mountinfo") : null;
        if (mountInfo is not null && NoExecMount(mountInfo, Path.GetFullPath(chosen)) is { } mount)
            return $"Programs can't run from the drive at {mount} (it is mounted noexec). Choose a folder on another drive.";
        var need = Size(AppDir) + SpareBytes;
        if (FreeSpace(chosen) is { } free && free < need)
            return $"That drive has {Format.Bytes(free)} free; Pairnets needs {Format.Bytes(need)}.";
        return null;
    }

    /// <summary>
    /// Copies the app to &lt;<paramref name="chosen"/>&gt;/pairnets, checks the copy and points the command, the app menu and
    /// start at login at it. Returns the copy's app file, to start. Throws <see cref="PlainMessageException"/> with the
    /// reason when it can't; then nothing was changed (a half copy is removed again).
    /// </summary>
    public string Move(string chosen, string? mountInfo = null)
    {
        if (Check(chosen, mountInfo) is { } problem)
            throw new PlainMessageException(problem);
        var target = TargetFor(Path.GetFullPath(chosen));
        var made = !Directory.Exists(target);
        var newApp = Path.Combine(target, Path.GetRelativePath(AppDir, AppFile));
        var undo = new Stack<Action>();
        try
        {
            Directory.CreateDirectory(target);
            Copy(AppDir, target);
            Verify(AppDir, target);
            undo.Push(() => File.Delete(MarkerPath(_localDir)));
            WriteMarker(new Marker(AppDir, target, Path.GetFileName(AppFile), DateTimeOffset.UtcNow));
            Relink(newApp, undo);
            foreach (var entry in new[] { MenuEntry, AutostartEntry })
                RewriteExec(entry, newApp, undo);
            return newApp;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or PlainMessageException)
        {
            while (undo.TryPop(out var step))
            {
                try
                {
                    step();
                }
                catch (Exception undoError) when (undoError is IOException or UnauthorizedAccessException)
                {
                }
            }
            try
            {
                if (made && Directory.Exists(target))
                    Directory.Delete(target, recursive: true);
                else if (Directory.Exists(target))
                    foreach (var entry in Directory.EnumerateFileSystemEntries(target).ToList())
                        if (Directory.Exists(entry)) Directory.Delete(entry, true); else File.Delete(entry);
            }
            catch (Exception cleanError) when (cleanError is IOException or UnauthorizedAccessException)
            {
            }
            throw ex as PlainMessageException ?? new PlainMessageException("Pairnets could not be moved: " + ex.Message + " Nothing was changed.", ex);
        }
    }

    /// <summary>
    /// At start: when the app runs from the folder a move made, removes the folder it came from (only if the note names it
    /// and it still holds the Pairnets program), then the note. Never throws; true when an old folder was removed.
    /// </summary>
    public static bool CleanUpOldCopy(string localDir, string? appFile)
    {
        try
        {
            var path = MarkerPath(localDir);
            if (appFile is null || !File.Exists(path))
                return false;
            var marker = JsonSerializer.Deserialize<Marker>(File.ReadAllText(path), PairnetsJson.Options);
            var running = Path.GetDirectoryName(Path.GetFullPath(appFile))!;
            if (marker is null || !Same(marker.NewDir, running))
                return false; // still the old one (the copy did not start): keep both, and the note for the copy
            var removed = false;
            if (!Same(marker.OldDir, marker.NewDir) && !Inside(running, marker.OldDir) && !string.IsNullOrEmpty(marker.Binary)
                && File.Exists(Path.Combine(marker.OldDir, Path.GetFileName(marker.Binary))) && Directory.Exists(marker.OldDir))
            {
                Directory.Delete(marker.OldDir, recursive: true);
                removed = true;
            }
            File.Delete(path);
            return removed;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException)
        {
            return false; // the next start tries again
        }
    }

    /// <summary>The note a move leaves: the old folder, the new one, the program's file name, and when.</summary>
    public sealed record Marker(string OldDir, string NewDir, string Binary, DateTimeOffset At);

    public static string MarkerPath(string localDir) => Path.Combine(localDir, MarkerFile);

    /// <summary>
    /// The mount point of the drive holding <paramref name="path"/> when it is mounted noexec, else null. Reads
    /// /proc/self/mountinfo lines ("36 35 98:0 / /mnt/data rw,noexec,relatime shared:1 - ext4 /dev/sdb1 rw").
    /// </summary>
    public static string? NoExecMount(string mountInfo, string path)
    {
        string? best = null;
        var noexec = false;
        foreach (var line in mountInfo.Split('\n'))
        {
            var fields = line.Split(' ');
            if (fields.Length < 6)
                continue;
            var mount = Unescape(fields[4]);
            if (!Same(mount, path) && !Inside(path, mount))
                continue;
            if (best is not null && mount.Length < best.Length)
                continue;
            best = mount;
            noexec = fields[5].Split(',').Contains("noexec");
        }
        return noexec ? best : null;
    }

    private static string Unescape(string field) =>
        field.Replace("\\040", " ", StringComparison.Ordinal).Replace("\\011", "\t", StringComparison.Ordinal)
            .Replace("\\012", "\n", StringComparison.Ordinal).Replace("\\134", "\\", StringComparison.Ordinal);

    private void WriteMarker(Marker marker)
    {
        Directory.CreateDirectory(_localDir);
        File.WriteAllText(MarkerPath(_localDir), JsonSerializer.Serialize(marker, PairnetsJson.Options));
    }

    /// <summary>Points ~/.local/bin/pairnets at the copy, when it is a link to this app (anything else is left alone).</summary>
    private void Relink(string newApp, Stack<Action> undo)
    {
        var link = new FileInfo(CommandLink);
        if (link.LinkTarget is not { } to)
            return;
        var resolved = Path.GetFullPath(Path.IsPathRooted(to) ? to : Path.Combine(Path.GetDirectoryName(CommandLink)!, to));
        if (!Same(resolved, AppFile))
            return;
        File.Delete(CommandLink);
        undo.Push(() =>
        {
            if (File.Exists(CommandLink) || new FileInfo(CommandLink).LinkTarget is not null)
                File.Delete(CommandLink);
            File.CreateSymbolicLink(CommandLink, to);
        });
        File.CreateSymbolicLink(CommandLink, newApp);
    }

    /// <summary>Exec= (and TryExec=) in a .desktop entry: the old app's path becomes the copy's.</summary>
    private void RewriteExec(string entry, string newApp, Stack<Action> undo)
    {
        if (!File.Exists(entry))
            return;
        var before = File.ReadAllText(entry);
        var lines = before.Split('\n');
        var changed = false;
        for (var i = 0; i < lines.Length; i++)
        {
            if ((lines[i].StartsWith("Exec=", StringComparison.Ordinal) || lines[i].StartsWith("TryExec=", StringComparison.Ordinal))
                && lines[i].Contains(AppFile, StringComparison.Ordinal))
            {
                lines[i] = lines[i].Replace(AppFile, newApp, StringComparison.Ordinal);
                changed = true;
            }
        }
        if (!changed)
            return;
        var temp = entry + ".pairnets-new";
        File.WriteAllText(temp, string.Join('\n', lines), new UTF8Encoding(false));
        File.Move(temp, entry, overwrite: true);
        undo.Push(() => File.WriteAllText(entry, before, new UTF8Encoding(false)));
    }

    private static void Copy(string from, string to)
    {
        foreach (var dir in Directory.EnumerateDirectories(from, "*", SearchOption.AllDirectories))
            Directory.CreateDirectory(Path.Combine(to, Path.GetRelativePath(from, dir)));
        foreach (var file in Files(from))
        {
            var dest = Path.Combine(to, Path.GetRelativePath(from, file));
            File.Copy(file, dest, overwrite: false);
            if (!OperatingSystem.IsWindows())
                File.SetUnixFileMode(dest, File.GetUnixFileMode(file));
        }
    }

    /// <summary>Every file is there with its size; else the copy is not trusted.</summary>
    private static void Verify(string from, string to)
    {
        foreach (var file in Files(from))
        {
            var dest = new FileInfo(Path.Combine(to, Path.GetRelativePath(from, file)));
            if (!dest.Exists || dest.Length != new FileInfo(file).Length)
                throw new PlainMessageException($"The copy of {Path.GetFileName(file)} is not complete. Nothing was changed.");
        }
    }

    /// <summary>The program's files, without what an update leaves behind (Pairnets.old, Pairnets.new).</summary>
    private static IEnumerable<string> Files(string dir) =>
        Directory.EnumerateFiles(dir, "*", SearchOption.AllDirectories)
            .Where(f => !f.EndsWith(".old", StringComparison.Ordinal) && !f.EndsWith(".new", StringComparison.Ordinal));

    private static long Size(string dir) => Files(dir).Sum(f => new FileInfo(f).Length);

    private static long? FreeSpace(string dir)
    {
        try
        {
            return new DriveInfo(Path.GetFullPath(dir)).AvailableFreeSpace;
        }
        catch (Exception ex) when (ex is IOException or ArgumentException or UnauthorizedAccessException)
        {
            return null;
        }
    }

    private static bool Writable(string dir)
    {
        var probe = Path.Combine(dir, ".pairnets-write-test-" + Guid.NewGuid().ToString("N")[..8]);
        try
        {
            File.WriteAllText(probe, string.Empty);
            File.Delete(probe);
            return true;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return false;
        }
    }

    private static string? SafeRead(string path)
    {
        try
        {
            return File.ReadAllText(path);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return null;
        }
    }

    private static bool Same(string a, string b) =>
        string.Equals(Path.TrimEndingDirectorySeparator(a), Path.TrimEndingDirectorySeparator(b),
            OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal);

    /// <summary>True when <paramref name="path"/> is inside <paramref name="dir"/> (not the folder itself).</summary>
    private static bool Inside(string path, string dir)
    {
        var d = Path.TrimEndingDirectorySeparator(dir);
        if (d.Length == 0)
            return false;
        if (d == "/")
            return path.StartsWith('/') && path.Length > 1;
        var comparison = OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;
        return path.StartsWith(d + Path.DirectorySeparatorChar, comparison) || path.StartsWith(d + "/", comparison);
    }
}
