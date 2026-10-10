using Avalonia;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Markup.Xaml;
using Pairnets.Core.Client;

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
            _controller = new DesktopController(this, desktop, Platform.IPlatformServices.Current, ClientEnvironment.Default);
            desktop.ShutdownRequested += (_, _) => _controller.Dispose();
            _controller.Start(desktop.Args ?? []);
            // macOS hands a pairnets:// link to the running app as an event (no second process starts).
            if (TryGetFeature(typeof(IActivatableLifetime)) is IActivatableLifetime activatable)
                activatable.Activated += (_, e) =>
                {
                    if (e.Kind == ActivationKind.OpenUri)
                        _controller?.ComeToFront();
                };
        }
        base.OnFrameworkInitializationCompleted();
    }
}
