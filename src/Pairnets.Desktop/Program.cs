using Avalonia;
using Pairnets.Core.Legacy;
using Pairnets.Core.Settings;

namespace Pairnets.Desktop;

internal static class Program
{
    [STAThread]
    public static int Main(string[] args)
    {
        if (!TakeOverTether())
            return 1;

        // Single instance: an exclusive lock on a file in the local data folder.
        Directory.CreateDirectory(PairnetsPaths.LocalDir);
        FileStream instanceLock;
        try
        {
            instanceLock = new FileStream(Path.Combine(PairnetsPaths.LocalDir, "app.lock"), FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None);
        }
        catch (IOException)
        {
            Console.Error.WriteLine("Pairnets is already running (look for its icon in the menu bar / system tray).");
            return 1;
        }

        using (instanceLock)
            return BuildAvaloniaApp().StartWithClassicDesktopLifetime(args, Avalonia.Controls.ShutdownMode.OnExplicitShutdown);
    }

    /// <summary>
    /// First start after Tether: moves its settings, sync state, token file and logs to Pairnets'
    /// folders and keeps "start at login". False when Tether is still running (it must be quit first).
    /// </summary>
    private static bool TakeOverTether()
    {
        var platform = Platform.IPlatformServices.Current;
        var localBase = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        var result = TetherMigration.MoveAppFolders(TetherMigration.DefaultBaseDirs(), () => TetherMigration.OldDesktopAppRunning(localBase));
        if (result == TetherMigration.FolderResult.OldAppRunning)
        {
            const string message = "Tether is still running. Quit it first, then start Pairnets again: Pairnets takes over Tether's settings and folders.";
            Console.Error.WriteLine(message);
            platform.Notify("Pairnets", message);
            return false;
        }
        try
        {
            if (platform.RemoveLegacyAutoStart())
                platform.SetAutoStart(true);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // The start-at-login choice can be set again in Settings.
        }
        return true;
    }

    public static AppBuilder BuildAvaloniaApp() =>
        AppBuilder.Configure<App>()
            .UsePlatformDetect()
            .With(new MacOSPlatformOptions { ShowInDock = false })
            .LogToTrace();
}
