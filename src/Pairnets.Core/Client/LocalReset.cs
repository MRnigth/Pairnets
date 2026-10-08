using System.Text.RegularExpressions;
using Pairnets.Core.Paths;
using Pairnets.Core.Settings;

namespace Pairnets.Core.Client;

/// <summary>What <see cref="LocalReset.Run"/> did, and what it could not.</summary>
/// <param name="Problems">Plain-language notes about things that could not be removed (empty when everything went).</param>
public sealed record LocalResetResult(bool SettingsRemoved, int StateFoldersRemoved, bool MarkerRemoved, bool KeyForgotten, IReadOnlyList<string> Problems);

/// <summary>
/// "Reset this app": removes everything Pairnets keeps for itself on this computer so it can be set up from
/// scratch. The caller stops the running session first, so no file is in use.
/// <list type="bullet">
/// <item>The settings file (and its leftovers), not the whole folders: an old Tether folder next to them would be taken over again.</item>
/// <item>The saved key in the system's secret store.</item>
/// <item>The sync notes (<c>state.db</c>) of every folder this computer ever synced.</item>
/// <item>The marker file and temporary folder Pairnets put in the synced folder. They never sync.</item>
/// </list>
/// Never touched: the files in the synced folder, the logs (they are what a bug report is made from), and the
/// single-instance lock of the running app.
/// </summary>
public static partial class LocalReset
{
    public static LocalResetResult Run(ClientSettings? settings, ISecretProtector? protector, string? settingsPath = null, string? localDir = null)
    {
        var problems = new List<string>();
        settingsPath ??= SettingsStore.DefaultPath;
        localDir ??= PairnetsPaths.LocalDir;

        // The key first: once the settings file is gone nothing says where it was kept.
        var keyForgotten = false;
        if (settings?.ProtectedToken is { Length: > 0 } saved && protector is not null)
        {
            try
            {
                protector.Forget(saved);
                keyForgotten = true;
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidOperationException or System.ComponentModel.Win32Exception)
            {
                problems.Add($"The saved key could not be removed from the system's secret store ({ex.Message}).");
            }
        }

        var markerRemoved = false;
        if (settings?.Folder is { Length: > 0 } folder && Path.IsPathFullyQualified(folder) && Directory.Exists(folder))
        {
            // Only the two names Pairnets itself put there; nothing else in the folder is looked at.
            markerRemoved = TryDelete(Path.Combine(folder, PathRules.MarkerFileName), problems);
            TryDeleteFolder(Path.Combine(folder, PathRules.TempFolderName), problems);
        }

        var stateFolders = 0;
        if (Directory.Exists(localDir))
        {
            foreach (var dir in Directory.EnumerateDirectories(localDir))
            {
                // A state folder is named after the hash of its sync folder and holds state.db; logs and anything else stay.
                if (!StateFolderName().IsMatch(Path.GetFileName(dir)) || !File.Exists(Path.Combine(dir, "state.db")))
                    continue;
                var clean = true;
                foreach (var suffix in new[] { string.Empty, "-wal", "-shm" })
                    clean &= TryDelete(Path.Combine(dir, "state.db" + suffix), problems, missingIsFine: true);
                if (clean && !Directory.EnumerateFileSystemEntries(dir).Any())
                    TryDeleteFolder(dir, problems);
                if (clean)
                    stateFolders++;
            }
        }

        var settingsRemoved = true;
        foreach (var suffix in new[] { string.Empty, ".tmp", ".corrupt" })
            settingsRemoved &= TryDelete(settingsPath + suffix, problems, missingIsFine: true);

        return new LocalResetResult(settingsRemoved, stateFolders, markerRemoved, keyForgotten, problems);
    }

    private static bool TryDelete(string path, List<string> problems, bool missingIsFine = false)
    {
        try
        {
            if (!File.Exists(path))
                return missingIsFine;
            File.Delete(path);
            return true;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            problems.Add($"{Path.GetFileName(path)} could not be removed ({ex.Message}).");
            return false;
        }
    }

    private static void TryDeleteFolder(string path, List<string> problems)
    {
        try
        {
            if (Directory.Exists(path))
                Directory.Delete(path, recursive: true);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            problems.Add($"{Path.GetFileName(path)} could not be removed ({ex.Message}).");
        }
    }

    [GeneratedRegex("^[0-9a-f]{16}$")]
    private static partial Regex StateFolderName();
}
