using System.Windows;
using System.Windows.Threading;
using Pairnets.Client.Themes;

[assembly: CollectionBehavior(DisableTestParallelization = true)]

namespace Pairnets.Client.Tests;

/// <summary>
/// The one WPF thread of the whole test run: an STA thread that holds the app's single <see cref="Application"/>
/// (with its theme, as the app has it) and runs its dispatcher until the run ends. Tests hand it work with
/// <see cref="Ui(Action)"/> and friends. An exception that a click handler lets escape (the app would show
/// "Pairnets hit an unexpected error") is kept in <see cref="TakeCrashes"/> instead of ending the run.
/// Windows open far off screen and never take the focus, so a run does not get in the way of whoever sits
/// at the computer.
/// </summary>
public sealed class WpfThread
{
    private static readonly Lazy<WpfThread> Shared = new(() => new WpfThread());
    private readonly List<Exception> _crashes = [];

    private WpfThread()
    {
        var ready = new TaskCompletionSource<Dispatcher>(TaskCreationOptions.RunContinuationsAsynchronously);
        var thread = new Thread(() =>
        {
            try
            {
                var app = new Application { ShutdownMode = ShutdownMode.OnExplicitShutdown };
                ThemeManager.Apply(app, dark: false); // a fixed theme: no listening to the Windows setting
                KeepWindowsOffScreen(app);
                app.DispatcherUnhandledException += (_, e) =>
                {
                    lock (_crashes)
                        _crashes.Add(e.Exception);
                    e.Handled = true;
                };
                ready.SetResult(Dispatcher.CurrentDispatcher);
            }
            catch (Exception ex)
            {
                ready.SetException(ex);
                return;
            }
            Dispatcher.Run();
        })
        {
            IsBackground = true,
            Name = "Pairnets WPF tests",
        };
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        Dispatcher = ready.Task.GetAwaiter().GetResult();
    }

    public static WpfThread Instance => Shared.Value;

    public Dispatcher Dispatcher { get; }

    /// <summary>Runs <paramref name="action"/> on the WPF thread; its exception fails the returned task.</summary>
    public Task Ui(Action action) => Dispatcher.InvokeAsync(action).Task;

    public Task<T> Ui<T>(Func<T> func) => Dispatcher.InvokeAsync(func).Task;

    public Task UiAsync(Func<Task> func) => Dispatcher.InvokeAsync(func).Task.Unwrap();

    public Task<T> UiAsync<T>(Func<Task<T>> func) => Dispatcher.InvokeAsync(func).Task.Unwrap();

    /// <summary>
    /// Starts <paramref name="action"/> on the WPF thread without waiting for it: for calls that open a window with
    /// ShowDialog and only return once it closes. Work handed over meanwhile runs inside that dialog's loop.
    /// </summary>
    public Task Post(Action action)
    {
        var done = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        Dispatcher.BeginInvoke(() =>
        {
            try
            {
                action();
                done.SetResult();
            }
            catch (Exception ex)
            {
                done.SetException(ex);
            }
        });
        return done.Task;
    }

    /// <summary>Lets everything already queued on the WPF thread run (clicks, layout, the first drawing).</summary>
    public Task Settle() => Dispatcher.InvokeAsync(() => { }, DispatcherPriority.ApplicationIdle).Task;

    /// <summary>Waits until <paramref name="condition"/> (checked on the WPF thread) is true.</summary>
    public async Task WaitUntil(Func<bool> condition, string what, TimeSpan? timeout = null)
    {
        var deadline = DateTime.UtcNow + (timeout ?? TimeSpan.FromSeconds(30));
        while (!await Ui(condition))
        {
            if (DateTime.UtcNow > deadline)
                throw new TimeoutException($"Waited {(timeout ?? TimeSpan.FromSeconds(30)).TotalSeconds:0} seconds for {what}.");
            await Settle(); // what the window already queued (Loaded, layout) usually is what was waited for
            if (await Ui(condition))
                return;
            await Task.Delay(25);
        }
    }

    /// <summary>The exceptions that escaped onto the WPF thread since the last call.</summary>
    public IReadOnlyList<Exception> TakeCrashes()
    {
        lock (_crashes)
        {
            var taken = _crashes.ToList();
            _crashes.Clear();
            return taken;
        }
    }

    /// <summary>Closes every open window (the main window's close button only hides it, so that is allowed first).</summary>
    public Task CloseAllWindows() => Ui(() =>
    {
        foreach (var window in Application.Current.Windows.Cast<Window>().ToList())
        {
            if (window is Ui.MainWindow main)
                main.AllowClose = true;
            foreach (var menu in OpenMenus(window))
                menu.IsOpen = false;
            window.Close();
        }
    });

    private static IEnumerable<System.Windows.Controls.ContextMenu> OpenMenus(Window window) =>
        Presser.Elements(window).Select(e => e.ContextMenu).OfType<System.Windows.Controls.ContextMenu>().Where(m => m.IsOpen);

    /// <summary>
    /// Every Pairnets window takes the "PairnetsWindow" style; this one adds an attached setting that moves the
    /// window off screen just before it first appears, and keeps it out of the taskbar and from taking the focus.
    /// </summary>
    private static void KeepWindowsOffScreen(Application app)
    {
        var original = (Style)app.FindResource("PairnetsWindow");
        var style = new Style(typeof(Window), original);
        style.Setters.Add(new Setter(OffScreenProperty, true));
        style.Setters.Add(new Setter(Window.ShowActivatedProperty, false));
        style.Setters.Add(new Setter(Window.ShowInTaskbarProperty, false));
        app.Resources["PairnetsWindow"] = style;
    }

    private static readonly DependencyProperty OffScreenProperty = DependencyProperty.RegisterAttached(
        "OffScreen", typeof(bool), typeof(WpfThread), new PropertyMetadata(false, (d, e) =>
        {
            if (d is Window window && e.NewValue is true)
                window.SourceInitialized += (_, _) => MoveOffScreen(window);
        }));

    /// <summary>Where the windows go: far left of every screen.</summary>
    public const double OffScreenLeft = -20000;

    private static void MoveOffScreen(Window window)
    {
        window.WindowStartupLocation = WindowStartupLocation.Manual;
        window.Left = OffScreenLeft;
        window.Top = 0;
    }
}
