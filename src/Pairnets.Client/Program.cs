using System.Windows;
using Pairnets.Core;
using Pairnets.Core.Client;
using Pairnets.Core.Legacy;

namespace Pairnets.Client;

internal static class Program
{
    [STAThread]
    private static int Main(string[] args)
    {
        using var mutex = new Mutex(initiallyOwned: true, PairnetsInfo.WindowsMutex, out var createdNew);
        if (!createdNew)
        {
            // Started for a pairnets:// link (the nest's website sending you back after approving
            // this computer): poke the running Pairnets to the front and leave quietly.
            if (args.Any(AppActivation.IsActivationUrl))
            {
                AppActivation.TrySignalRunning();
                return 0;
            }
            MessageBox.Show("Pairnets is already running. Look for its icon in the notification area.", "Pairnets",
                MessageBoxButton.OK, MessageBoxImage.Information);
            return 1;
        }

        if (!TakeOverTether())
            return 1;

        var app = new App();
        return app.Run();
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
