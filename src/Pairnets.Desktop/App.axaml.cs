using Avalonia;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Markup.Xaml;

namespace Pairnets.Desktop;

public partial class App : Application
{
    private DesktopController? _controller;

    public override void Initialize() => AvaloniaXamlLoader.Load(this);

    public override void OnFrameworkInitializationCompleted()
    {
        if (ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop)
        {
            desktop.ShutdownMode = Avalonia.Controls.ShutdownMode.OnExplicitShutdown;
            _controller = new DesktopController(this, desktop, Platform.IPlatformServices.Current);
            desktop.ShutdownRequested += (_, _) => _controller.Dispose();
            _controller.Start(desktop.Args ?? []);
        }
        base.OnFrameworkInitializationCompleted();
    }
}
