using Avalonia;
using Pairnets.Core.Client;
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
            instanceLock = TakeInstanceLock(Path.Combine(PairnetsPaths.LocalDir, "app.lock"),
                args.Contains(SelfReplace.AfterUpdateArgument) ? SelfReplace.WaitForOldApp : TimeSpan.Zero);
        }
        catch (IOException)
        {
            // Started for a pairnets:// link (the nest's website sending you back after approving
            // this computer): poke the running Pairnets to the front and leave quietly.
            if (args.Any(AppActivation.IsActivationUrl))
            {
                AppActivation.TrySignalRunning();
                return 0;
            }
            // A right-click item in the file manager ("Open in Pairnets", "Show sync status"): the running Pairnets does it.
            if (FileManagerCommand.FromArguments(args) is { } command)
                return AppActivation.TrySendToRunning(command.ToMessage()) ? 0 : 1;
            Console.Error.WriteLine("Pairnets is already running (look for its icon in the menu bar / system tray).");
            return 1;
        }

        SelfReplace.CleanUp(Environment.ProcessPath);
        using (instanceLock)
            return BuildAvaloniaApp().StartWithClassicDesktopLifetime(args, Avalonia.Controls.ShutdownMode.OnExplicitShutdown);
    }

    /// <summary>
    /// The exclusive lock on app.lock. <paramref name="wait"/>: how long to keep trying (started by the Pairnets this one
    /// just replaced, which is still quitting).
    /// </summary>
    private static FileStream TakeInstanceLock(string path, TimeSpan wait)
    {
        var until = DateTime.UtcNow + wait;
        while (true)
        {
            try
            {
                return new FileStream(path, FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None);
            }
            catch (IOException) when (DateTime.UtcNow < until)
            {
                Thread.Sleep(250);
            }
        }
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
