using Pairnets.Core.State;

namespace Pairnets.Core.Settings;

/// <summary>
/// Finds the sync state belonging to a folder's .pairnets-marker, so a folder that was moved or
/// renamed can continue with its existing state instead of resyncing from scratch.
/// </summary>
public static class StateLocator
{
    /// <summary>Returns the state directory whose marker id equals <paramref name="markerId"/>, or null.</summary>
    public static string? FindStateDirForMarker(string markerId, string? baseDir = null)
    {
        var root = baseDir ?? PairnetsPaths.LocalDir;
        if (!Directory.Exists(root))
            return null;
        foreach (var dir in Directory.EnumerateDirectories(root))
        {
            var db = Path.Combine(dir, "state.db");
            if (!File.Exists(db))
                continue;
            try
            {
                using var state = new StateDb(db);
                if (string.Equals(state.MarkerId, markerId, StringComparison.OrdinalIgnoreCase))
                    return dir;
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or Microsoft.Data.Sqlite.SqliteException or InvalidOperationException)
            {
                // Unreadable or in use: skip.
            }
        }
        return null;
    }

    /// <summary>Reads the marker id of a folder, or null if it has none.</summary>
    public static string? ReadMarker(string folder)
    {
        Legacy.TetherMigration.MigrateSyncFolder(folder);
        var path = Path.Combine(folder, Paths.PathRules.MarkerFileName);
        try
        {
            if (!File.Exists(path))
                return null;
            var text = File.ReadAllText(path).Trim();
            return Guid.TryParse(text, out var g) ? g.ToString("D") : null;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return null;
        }
    }

    /// <summary>
    /// Copies the state of a moved folder to the state directory of its new location.
    /// Refuses to overwrite an existing state there.
    /// </summary>
    public static void AdoptState(string oldStateDir, string newFolder, string? baseDir = null)
    {
        var target = PairnetsPaths.StateDirFor(newFolder, baseDir);
        if (File.Exists(Path.Combine(target, "state.db")))
            throw new IOException($"The new location already has sync state ({target}).");
        Directory.CreateDirectory(target);
        Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
        foreach (var suffix in new[] { "", "-wal", "-shm" })
        {
            var src = Path.Combine(oldStateDir, "state.db" + suffix);
            if (File.Exists(src))
                File.Copy(src, Path.Combine(target, "state.db" + suffix));
        }
        using var state = new StateDb(Path.Combine(target, "state.db"));
        state.SetMeta(StateDb.MetaFolder, Path.GetFullPath(newFolder));
    }
}
