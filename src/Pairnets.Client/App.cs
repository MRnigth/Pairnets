using System.Windows;
using System.Windows.Threading;
using Microsoft.Extensions.Logging;
using Pairnets.Client.Platform;
using Pairnets.Client.Ui;
using Pairnets.Core.Client;
using Pairnets.Core.Logging;

namespace Pairnets.Client;

/// <summary>WPF host without a main window: the tray icon is the UI.</summary>
public sealed class App : Application
{
    private TrayController? _tray;
    private ILoggerFactory? _loggerFactory;
    private RollingFileLoggerProvider? _fileLog;
    private CrashCatcher? _crashes;

    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);
        ShutdownMode = ShutdownMode.OnExplicitShutdown;
        Themes.ThemeManager.Apply(this);

        var env = ClientEnvironment.Default;
        _fileLog = new RollingFileLoggerProvider(env.LogsDir, retentionDays: 14);
        _loggerFactory = LoggerFactory.Create(b => b.AddProvider(_fileLog).SetMinimumLevel(LogLevel.Debug));
        var log = _loggerFactory.CreateLogger("Pairnets.Client");
        log.LogInformation("Pairnets {Version} starting", typeof(App).Assembly.GetName().Version);

        // Errors nothing else caught are logged and kept as a cleaned crash card (nothing is sent anywhere).
        _crashes = new CrashCatcher(env.CrashesDir, TrayController.AppName, log,
            () => _tray is { } tray ? tray.PrivateValues() : PrivacyCleaner.ThisComputer());
        _crashes.Watch();
        DispatcherUnhandledException += (_, args) =>
        {
            _crashes.Record(args.Exception, CrashPlace.Screen);
            args.Handled = true;
            var answer = Dialogs.Ask(null, "Pairnets hit an unexpected error and will keep running:\n" + args.Exception.Message
                + "\n\nCreate a bug report? It is copied to the clipboard so you can paste it to whoever helps you; nothing is sent anywhere.",
                "Pairnets", MessageBoxButton.YesNo, MessageBoxImage.Error);
            if (answer == MessageBoxResult.Yes)
            {
                try
                {
                    _tray?.ReportBug(args.Exception);
                }
                catch (Exception ex)
                {
                    log.LogError(ex, "Could not open the bug report");
                }
            }
        };

        // The tray also answers the pairnets:// "come to the front" pokes (it starts listening before the sign-in window).
        _tray = new TrayController(_loggerFactory, _fileLog, new WindowsPlatform(), env, Dispatcher, Shutdown);
        _tray.Start();
    }

    protected override void OnExit(ExitEventArgs e)
    {
        _tray?.Dispose();
        _crashes?.Dispose();
        _loggerFactory?.Dispose();
        _fileLog?.Dispose();
        base.OnExit(e);
    }
}
