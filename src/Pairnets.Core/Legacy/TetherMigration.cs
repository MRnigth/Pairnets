using Pairnets.Core.Paths;

namespace Pairnets.Core.Legacy;

/// <summary>
/// Takes over what an installed Tether app left behind, the first time Pairnets starts: its folders
/// (settings, sync state, logs, token file) and, in each synced folder, its marker. Everything is
/// moved, never deleted, and nothing is touched while the old app is still running.
/// </summary>
public static class TetherMigration
{
    public enum FolderResult
    {
        /// <summary>There was no Tether app data, or Pairnets already has its own.</summary>
        NothingToMove,

        Moved,

        /// <summary>Tether is still running; it must be quit first.</summary>
        OldAppRunning,

        /// <summary>Moving failed (for example a file in use); nothing was lost and Pairnets starts fresh.</summary>
        Failed,
    }

    /// <summary>
    /// Moves &lt;base&gt;/Tether to &lt;base&gt;/Pairnets for each base folder (roaming and local app data),
    /// unless Pairnets already has a folder there.
    /// </summary>
    /// <param name="oldAppRunning">Whether the old app holds its single-instance lock.</param>
    public static FolderResult MoveAppFolders(IEnumerable<string> baseDirs, Func<bool> oldAppRunning)
    {
        var moves = baseDirs
            .Distinct(OperatingSystem.IsWindows() ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal)
            .Select(b => (From: Path.Combine(b, TetherNames.AppFolder), To: Path.Combine(b, Settings.PairnetsPaths.FolderName)))
            .Where(m => Directory.Exists(m.From) && !Directory.Exists(m.To))
            .ToList();
        if (moves.Count == 0)
            return FolderResult.NothingToMove;
        if (oldAppRunning())
            return FolderResult.OldAppRunning;
        try
        {
            foreach (var (from, to) in moves)
                Directory.Move(from, to);
            return FolderResult.Moved;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return FolderResult.Failed;
        }
    }

    /// <summary>The usual places: %AppData% and %LocalAppData% (or their Mac/Linux equivalents).</summary>
    public static IEnumerable<string> DefaultBaseDirs() =>
    [
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
    ];

    /// <summary>Whether the old Mac/Linux app holds its lock file (it is running).</summary>
    public static bool OldDesktopAppRunning(string localBase)
    {
        var lockFile = Path.Combine(localBase, TetherNames.AppFolder, TetherNames.DesktopLockFile);
        if (!File.Exists(lockFile))
            return false;
        try
        {
            using var _ = new FileStream(lockFile, FileMode.Open, FileAccess.ReadWrite, FileShare.None);
            return false;
        }
        catch (IOException)
        {
            return true;
        }
        catch (UnauthorizedAccessException)
        {
            return false;
        }
    }

    /// <summary>
    /// In a synced folder: renames Tether's marker to Pairnets' (its content, the folder's id, stays)
    /// and removes Tether's download staging folder, which only ever holds unfinished downloads.
    /// </summary>
    public static void MigrateSyncFolder(string root)
    {
        try
        {
            var oldMarker = Path.Combine(root, TetherNames.MarkerFileName);
            var newMarker = Path.Combine(root, PathRules.MarkerFileName);
            if (File.Exists(oldMarker) && !File.Exists(newMarker))
                File.Move(oldMarker, newMarker);
            var oldTemp = Path.Combine(root, TetherNames.TempFolderName);
            if (Directory.Exists(oldTemp))
                Directory.Delete(oldTemp, recursive: true);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // Read-only or busy: the old names stay reserved, so nothing syncs by mistake; try again next pass.
        }
    }
}
