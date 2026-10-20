using System.Windows;
using Pairnets.Core;
using Pairnets.Core.Client;
using Pairnets.Core.Legacy;

namespace Pairnets.Client;

internal static class Program
{
    /// <summary>Takes Pairnets out of File Explorer and quits (the uninstaller runs it).</summary>
    public const string UnregisterExplorerArgument = "--unregister-explorer";

    /// <summary>Asks before uninstalling when files are only on the nest; exit code 1 means "do not uninstall".</summary>
    public const string CheckBeforeUninstallArgument = "--check-before-uninstall";

    /// <summary>
    /// With Files On-Demand some files may be only on the nest: without Pairnets they could not open. The person
    /// chooses: stop (and switch Files On-Demand off in Pairnets first, which brings every file here), or go on.
    /// </summary>
    private static int CheckBeforeUninstall()
    {
        int online;
        try
        {
            online = Platform.CloudFiles.WindowsCloudFolder.CountCloudOnly();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.Runtime.InteropServices.COMException)
        {
            return 0;
        }
        if (online == 0)
            return 0;
        return MessageBox.Show($"{online} file(s) in your Pairnets folder are only on your nest (Files On-Demand). Without Pairnets they cannot open.\n\n" +
            "To keep them on this PC: choose No, open Pairnets, and set Settings → Files in Explorer to \"Plain folder\". Pairnets downloads them, then you can uninstall.\n\n" +
            "Uninstall anyway? (They stay safe on your nest.)", "Uninstall Pairnets", MessageBoxButton.YesNo, MessageBoxImage.Warning) == MessageBoxResult.Yes ? 0 : 1;
    }

    [STAThread]
    private static int Main(string[] args)
    {
        // The uninstaller first asks whether files would be left behind, then takes Pairnets out of File Explorer
        // (left side, right-click items, folder icon, the cloud folder).
        if (args.Contains(CheckBeforeUninstallArgument))
            return CheckBeforeUninstall();
        if (args.Contains(UnregisterExplorerArgument))
        {
            try
            {
                Platform.CloudFiles.WindowsCloudFolder.ForgetAll();
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.Runtime.InteropServices.COMException)
            {
                // The rest still goes.
            }
            return Platform.WindowsPlatform.SetExplorerEntry(null, null, Pairnets.Core.Settings.PairnetsPaths.LocalDir) ? 0 : 1;
        }

        using var mutex = new Mutex(initiallyOwned: true, PairnetsInfo.WindowsMutex, out var createdNew);
        // Started by the Pairnets this one just replaced (a copy from the zip updating itself): wait for it to quit.
        if (!createdNew && args.Contains(SelfReplace.AfterUpdateArgument))
            createdNew = WaitForOldApp(mutex);
        if (!createdNew)
        {
            // Started for a pairnets:// link (the nest's website sending you back after approving
            // this computer): poke the running Pairnets to the front and leave quietly.
            if (args.Any(AppActivation.IsActivationUrl))
            {
                AppActivation.TrySignalRunning();
                return 0;
            }
            // A right-click item in Explorer ("Open in Pairnets", "Show sync status"): the running Pairnets does it.
            if (FileManagerCommand.FromArguments(args) is { } command)
                return AppActivation.TrySendToRunning(command.ToMessage()) ? 0 : 1;
            MessageBox.Show("Pairnets is already running. Look for its icon in the notification area.", "Pairnets",
                MessageBoxButton.OK, MessageBoxImage.Information);
            return 1;
        }

        SelfReplace.CleanUp(Environment.ProcessPath);
        if (!TakeOverTether())
            return 1;

        var app = new App();
        return app.Run();
    }

    private static bool WaitForOldApp(Mutex mutex)
    {
        try
        {
            return mutex.WaitOne(SelfReplace.WaitForOldApp);
        }
        catch (AbandonedMutexException)
        {
            return true; // the old Pairnets ended without letting go: the mutex is ours now
        }
    }

    /// <summary>
    /// First start after Tether: moves its settings, sync state and logs to Pairnets' folders and keeps
    /// "start with Windows". False when Tether is still running (it must be quit first).
    /// </summary>
    private static bool TakeOverTether()
    {
        var result = TetherMigration.MoveAppFolders(TetherMigration.DefaultBaseDirs(), () =>
        {
            if (!Mutex.TryOpenExisting(TetherNames.WindowsMutex, out var tether))
                return false;
            tether.Dispose();
            return true;
        });
        if (result == TetherMigration.FolderResult.OldAppRunning)
        {
            MessageBox.Show("Tether is still running. Quit it first (right-click its icon, then Exit), then start Pairnets again. " +
                "Pairnets takes over Tether's settings and folders.", "Pairnets", MessageBoxButton.OK, MessageBoxImage.Information);
            return false;
        }
        if (result == TetherMigration.FolderResult.Failed
            && MessageBox.Show("Pairnets could not take over Tether's settings (a file was in use). Restart the computer and start " +
                "Pairnets again to retry, or press OK to set Pairnets up from scratch.", "Pairnets",
                MessageBoxButton.OKCancel, MessageBoxImage.Warning) != MessageBoxResult.OK)
            return false;
        try
        {
            if (Platform.AutoStart.RemoveLegacy())
                Platform.AutoStart.Set(true);
        }
        catch (Exception ex) when (ex is UnauthorizedAccessException or System.Security.SecurityException or IOException)
        {
            // The start-at-login choice can be set again in Settings.
        }
        return true;
    }
}
