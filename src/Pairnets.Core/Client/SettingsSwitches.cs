using Pairnets.Core.Settings;
using Pairnets.Core.Sync;

namespace Pairnets.Core.Client;

/// <summary>
/// The Settings page's switches apply at once (no Save). This says what a change needs: most only need saving (and the app
/// reads them again), the speed limits change on the running connection, Pairnets' place in the file manager is put there
/// or taken away, and the settings the sync engine reads when it starts (wait for big batches, files at a time, the
/// Windows cloud folder, where the files live) need a new session.
/// </summary>
public static class SettingsSwitches
{
    /// <summary>True when going from <paramref name="before"/> to <paramref name="after"/> needs the session started again.</summary>
    public static bool NeedsNewSession(ClientSettings before, ClientSettings after) =>
        before.WaitForPeerBatches != after.WaitForPeerBatches
        || before.EffectiveParallelTransfers != after.EffectiveParallelTransfers
        || before.CloudFolder != after.CloudFolder
        || PlacementChanged(before, after);

    /// <summary>True when a folder's "where your files live" choice differs.</summary>
    public static bool PlacementChanged(ClientSettings before, ClientSettings after) =>
        before.Placement.Count != after.Placement.Count
        || before.Placement.Any(p => !after.Placement.TryGetValue(p.Key, out var choice) || choice != p.Value);

    /// <summary>True when "Show Pairnets in File Explorer / the file manager" or the right-click menu changed.</summary>
    public static bool FileManagerChanged(ClientSettings before, ClientSettings after) =>
        before.ShowInFileManager != after.ShowInFileManager || before.ExplorerRightClick != after.ExplorerRightClick;

    /// <summary>True when the speed limits differ (they apply to the running connection).</summary>
    public static bool LimitsChanged(ClientSettings before, ClientSettings after) =>
        Limit(before.UploadLimitMBps) != Limit(after.UploadLimitMBps) || Limit(before.DownloadLimitMBps) != Limit(after.DownloadLimitMBps);

    /// <summary>
    /// <paramref name="current"/> with the switches (and the files-at-a-time choice and speed limits) of
    /// <paramref name="from"/>; everything else (folder, name, key, notifications) stays as it is now.
    /// </summary>
    public static ClientSettings WithSwitchesFrom(ClientSettings current, ClientSettings from)
    {
        var next = current.Clone();
        next.StartWithWindows = from.StartWithWindows;
        next.CheckForUpdates = from.CheckForUpdates;
        next.WaitForPeerBatches = from.WaitForPeerBatches;
        next.ShowMapSpeed = from.ShowMapSpeed;
        next.AutoUpdateServer = from.AutoUpdateServer;
        next.DebugMode = from.DebugMode;
        next.ShowInFileManager = from.ShowInFileManager;
        next.ExplorerRightClick = from.ExplorerRightClick;
        next.CloudFolder = from.CloudFolder;
        next.Placement = new Dictionary<string, FilePlacement>(from.Placement, StringComparer.Ordinal);
        next.ParallelTransfers = from.ParallelTransfers;
        next.UploadLimitMBps = Limit(from.UploadLimitMBps);
        next.DownloadLimitMBps = Limit(from.DownloadLimitMBps);
        return next;
    }

    private static double? Limit(double? mbps) => mbps is > 0 ? mbps : null;
}
