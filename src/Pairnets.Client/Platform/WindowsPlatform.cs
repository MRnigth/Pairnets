using System.Diagnostics;
using Microsoft.Extensions.Logging;
using Microsoft.Win32;
using Pairnets.Core.Settings;
using Pairnets.Core.Sync;

namespace Pairnets.Client.Platform;

/// <summary>The real Windows: DPAPI, the Recycle Bin, the per-user Run key and Explorer (opening things, and Pairnets' place in it).</summary>
public sealed class WindowsPlatform : IWindowsPlatform
{
    public ISecretProtector Secrets { get; } = new DpapiProtector();

    public ILocalTrash CreateTrash(ILogger log) => new RecycleBinTrash(log);

    public bool IsAutoStartEnabled() => AutoStart.IsEnabled();

    public void SetAutoStart(bool enabled) => AutoStart.Set(enabled);

    public void Open(string path) => Process.Start(new ProcessStartInfo(path) { UseShellExecute = true })?.Dispose();

    public void Reveal(string path) =>
        Process.Start(new ProcessStartInfo("explorer.exe", $"/select,\"{path}\"") { UseShellExecute = false })?.Dispose();

    public void RunInstaller(string path, string arguments) =>
        Process.Start(new ProcessStartInfo(path, arguments) { UseShellExecute = true })?.Dispose();

    public void StartUpdatedApp(string path, string arguments) =>
        Process.Start(new ProcessStartInfo(path, arguments) { UseShellExecute = false, WorkingDirectory = Path.GetDirectoryName(path) })?.Dispose();

    public bool SetExplorerEntry(string? folder, bool leftSide = true) => SetExplorerEntry(folder, Environment.ProcessPath, PairnetsPaths.LocalDir, leftSide);

    public bool SetExplorerEntries(IReadOnlyList<ExplorerNest> nests, bool leftSide = true) =>
        nests.Count == 0 ? SetExplorerEntry(null, leftSide) : SetExplorerEntries(nests, Environment.ProcessPath, PairnetsPaths.LocalDir, leftSide);

    /// <summary><see cref="SetExplorerEntries(IReadOnlyList{ExplorerNest}, bool)"/> with the program and the folder for the icons given.</summary>
    public static bool SetExplorerEntries(IReadOnlyList<ExplorerNest> nests, string? exePath, string localDir, bool leftSide = true)
    {
        if (exePath is null)
            return SetExplorerEntry(null, null, localDir);
        try
        {
            using var software = Registry.CurrentUser.CreateSubKey("Software");
            var before = ExplorerShell.RegisteredFolders(software);
            var now = nests.Select(n => Path.TrimEndingDirectorySeparator(Path.GetFullPath(n.Folder))).ToList();
            foreach (var gone in before.Where(b => !now.Contains(Path.TrimEndingDirectorySeparator(Path.GetFullPath(b)), StringComparer.OrdinalIgnoreCase)))
                ExplorerShell.RemoveFolderIcon(gone); // a nest left or its folder moved: that folder goes back to a plain folder
            var icons = Path.Combine(localDir, "Explorer");
            Directory.CreateDirectory(icons);
            var appIcon = CopyIcon("Pairnets.AppIcon.ico", icons);
            var folderIcon = CopyIcon("Pairnets.FolderIcon.ico", icons);
            var ok = ExplorerShell.Apply(software, nests.Select(n => new ExplorerEntry(n.Folder, exePath, appIcon, folderIcon, n.Name, n.Key)).ToList(), leftSide);
            if (!before.SequenceEqual(now, StringComparer.OrdinalIgnoreCase))
                ExplorerShell.TellExplorer(); // only when something Explorer shows changed (it redraws every icon)
            return ok;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.Security.SecurityException or ArgumentException)
        {
            return false;
        }
    }

    public CloudFiles.ICloudFolder? StartCloudFolder(string folder, CloudFolderMode mode, ILogger log, out string? problem)
    {
        string icon;
        try
        {
            var icons = Path.Combine(PairnetsPaths.LocalDir, "Explorer");
            Directory.CreateDirectory(icons);
            icon = CopyIcon("Pairnets.AppIcon.ico", icons);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            problem = "Pairnets could not write its icon: " + ex.Message;
            return null;
        }
        return CloudFiles.WindowsCloudFolder.Start(folder, mode, icon, log, out problem);
    }

    public bool HasCloudFolder() => CloudFiles.WindowsCloudFolder.Registered().Count > 0;

    public int ForgetCloudFolders() => CloudFiles.WindowsCloudFolder.ForgetAll();

    /// <summary>
    /// <see cref="SetExplorerEntry(string?)"/> with the program and the folder for the icons given. The icons are copied
    /// out of Pairnets.exe, so Explorer keeps showing them while the program is being replaced by an update.
    /// </summary>
    public static bool SetExplorerEntry(string? folder, string? exePath, string localDir, bool leftSide = true)
    {
        try
        {
            using var software = Registry.CurrentUser.CreateSubKey("Software");
            var before = ExplorerShell.RegisteredFolder(software);
            if (folder is null || exePath is null)
            {
                ExplorerShell.Remove(software, before);
                ExplorerShell.TellExplorer();
                return folder is null;
            }
            if (before is not null && !string.Equals(Path.GetFullPath(before), Path.GetFullPath(folder), StringComparison.OrdinalIgnoreCase))
                ExplorerShell.RemoveFolderIcon(before); // the folder moved: the old one goes back to a plain folder
            var icons = Path.Combine(localDir, "Explorer");
            Directory.CreateDirectory(icons);
            var entry = new ExplorerEntry(folder, exePath, CopyIcon("Pairnets.AppIcon.ico", icons), CopyIcon("Pairnets.FolderIcon.ico", icons));
            var ok = ExplorerShell.Apply(software, entry, leftSide);
            if (before is null || !string.Equals(before, Path.TrimEndingDirectorySeparator(Path.GetFullPath(folder)), StringComparison.OrdinalIgnoreCase))
                ExplorerShell.TellExplorer(); // only when something Explorer shows changed (it redraws every icon)
            return ok;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.Security.SecurityException or ArgumentException)
        {
            return false;
        }
    }

    /// <summary>Writes one of the icons built into Pairnets.exe next to the sync notes (only when it changed), and returns its path.</summary>
    private static string CopyIcon(string resource, string dir)
    {
        var path = Path.Combine(dir, resource["Pairnets.".Length..]);
        using var source = typeof(WindowsPlatform).Assembly.GetManifestResourceStream(resource)
            ?? throw new IOException("The icon " + resource + " is missing from Pairnets.exe.");
        using var copy = new MemoryStream();
        source.CopyTo(copy);
        var bytes = copy.ToArray();
        if (!File.Exists(path) || !File.ReadAllBytes(path).AsSpan().SequenceEqual(bytes))
            File.WriteAllBytes(path, bytes);
        return path;
    }
}
