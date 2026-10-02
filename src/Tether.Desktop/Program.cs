using Avalonia;
using Tether.Core.Settings;

namespace Tether.Desktop;

internal static class Program
{
    [STAThread]
    public static int Main(string[] args)
    {
        // Single instance: an exclusive lock on a file in the local data folder.
        Directory.CreateDirectory(TetherPaths.LocalDir);
        FileStream instanceLock;
        try
        {
            instanceLock = new FileStream(Path.Combine(TetherPaths.LocalDir, "app.lock"), FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None);
        }
        catch (IOException)
        {
            Console.Error.WriteLine("Tether is already running (look for its icon in the menu bar / system tray).");
            return 1;
        }

        using (instanceLock)
            return BuildAvaloniaApp().StartWithClassicDesktopLifetime(args, Avalonia.Controls.ShutdownMode.OnExplicitShutdown);
    }

    public static AppBuilder BuildAvaloniaApp() =>
        AppBuilder.Configure<App>()
            .UsePlatformDetect()
            .With(new MacOSPlatformOptions { ShowInDock = false })
            .LogToTrace();
}
