using System.Net;
using System.Net.Http.Json;
using System.Reflection;
using System.Windows;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Pairnets.Client.Ui;
using Pairnets.Core;
using Pairnets.Core.Client;
using Pairnets.Core.Logging;
using Pairnets.Core.Settings;
using Pairnets.Core.Sync;
using Pairnets.Server.Storage;
using Pairnets.Tests.Infrastructure;
using Xunit.Sdk;
using Forms = System.Windows.Forms;

namespace Pairnets.Client.Tests;

/// <summary>
/// The real Windows tray controller in a sandbox: folders of its own (<see cref="ClientEnvironment.Isolated"/>), a
/// stand-in for Windows that only writes down what it was asked, and a script that answers the message boxes. It
/// talks to a real test nest. Everything is pressed through the same buttons and menu items a person uses.
/// </summary>
internal sealed class TrayHarness : IAsyncDisposable
{
    /// <summary>An update feed nobody answers on (port 9 refuses), for tests that are not about updates.</summary>
    public static readonly Uri NoUpdates = new("http://127.0.0.1:9/download/");

    private readonly ILoggerFactory _loggers;
    private readonly RollingFileLoggerProvider _fileLog;
    private readonly IDisposable _dialogs;
    private readonly List<Task> _running = [];
    private int _shutdowns;

    private TrayHarness(ClientEnvironment env, TempDir root)
    {
        Root = root;
        Env = env;
        Folder = root.Combine("Sync");
        Directory.CreateDirectory(Folder);
        _fileLog = new RollingFileLoggerProvider(env.LogsDir, retentionDays: 14);
        _loggers = LoggerFactory.Create(b => b.AddProvider(_fileLog).SetMinimumLevel(LogLevel.Debug));
        _dialogs = Dialogs.Install();
    }

    public WpfThread Wpf => WpfThread.Instance;

    public TempDir Root { get; }

    public ClientEnvironment Env { get; }

    public FakeWindowsPlatform Platform { get; } = new();

    public DialogScript Dialogs { get; } = new();

    /// <summary>The folder kept in sync.</summary>
    public string Folder { get; }

    public TrayController Tray { get; private set; } = null!;

    /// <summary>How often the app asked to end (Exit, or after starting an installer).</summary>
    public int Shutdowns => Volatile.Read(ref _shutdowns);

    /// <summary>Notifications ("toasts") the app showed: title and text.</summary>
    public Log<(string Title, string Text)> Notifications { get; } = new();

    /// <summary>The tray menu items as they were made, by their first text ("Pause syncing", "Fix…").</summary>
    public IReadOnlyDictionary<Forms.ToolStripMenuItem, string> MenuItems { get; private set; } = new Dictionary<Forms.ToolStripMenuItem, string>();

    /// <summary>The items a person can press (all but the greyed-out status line), by their first text.</summary>
    public IReadOnlyList<string> PressableMenuItems { get; private set; } = [];

    /// <summary>
    /// A sandboxed app. <paramref name="signedInTo"/>: as if this computer signed in to that nest earlier (its own key,
    /// this sandbox's folder). <paramref name="watchFolder"/> false: only "Sync now" (and start-up) start a sync pass.
    /// </summary>
    public static async Task<TrayHarness> CreateAsync(TestServer? signedInTo = null, string name = "DESKTOP", bool watchFolder = true,
        Func<ClientEnvironment, ClientEnvironment>? environment = null, Action<ClientSettings>? settings = null)
    {
        var root = new TempDir("wpf-app");
        var env = ClientEnvironment.Isolated(root.Path, NoUpdates);
        env = environment?.Invoke(env) ?? env;
        var harness = new TrayHarness(env, root);
        if (signedInTo is not null)
            harness.Grant = harness.SaveSignIn(signedInTo, name, settings);
        await harness.Wpf.Ui(() =>
        {
            harness.Tray = new TrayController(harness._loggers, harness._fileLog, harness.Platform, env, harness.Wpf.Dispatcher,
                () => Interlocked.Increment(ref harness._shutdowns))
            {
                RunnerOptionsForTests = o => new RunnerOptions
                {
                    ServerUrl = o.ServerUrl,
                    Token = o.Token,
                    DeviceId = o.DeviceId,
                    WaitForPeerBatches = o.WaitForPeerBatches,
                    EnableWatcher = watchFolder,
                    WatcherDebounce = TimeSpan.FromMilliseconds(200),
                    PeriodicInterval = TimeSpan.FromHours(1),
                    UnstableRetry = TimeSpan.FromMilliseconds(300),
                    ErrorRetry = TimeSpan.FromMilliseconds(500),
                    OfflineBackoff = [TimeSpan.FromMilliseconds(200)],
                },
            };
            harness.Tray.Notified += (title, text) => harness.Notifications.Add((title, text));
            var items = harness.Tray.TrayIcon.ContextMenuStrip!.Items.OfType<Forms.ToolStripMenuItem>().ToList();
            harness.MenuItems = items.ToDictionary(i => i, i => i.Text ?? string.Empty);
            harness.PressableMenuItems = items.Where(i => i.Enabled).Select(i => i.Text ?? string.Empty).ToList();
        });
        return harness;
    }

    /// <summary>This computer's key on the nest when the sandbox started signed in.</summary>
    public DeviceKeyGrant? Grant { get; private set; }

    private DeviceKeyGrant SaveSignIn(TestServer nest, string name, Action<ClientSettings>? tweak)
    {
        var grant = nest.MintKey(name);
        var saved = new ClientSettings
        {
            ServerUrl = nest.Url.ToString(),
            ProtectedToken = Platform.Secrets.Protect(grant.Key),
            DeviceId = grant.Id,
            DeviceName = grant.Name,
            Folder = Folder,
            FirstRunCompleted = true,
            CheckForUpdates = false,
        };
        tweak?.Invoke(saved);
        SettingsStore.Save(Env.SettingsPath, saved);
        return grant;
    }

    /// <summary>The settings file as it is on disk now.</summary>
    public ClientSettings SavedSettings => SettingsStore.Load(Env.SettingsPath);

    /// <summary>Starts the app signed in: it opens the main window and syncs.</summary>
    public async Task StartAsync()
    {
        await Wpf.Ui(Tray.Start);
        await Wpf.WaitUntil(() => Tray.Session is not null && Tray.Window is { IsVisible: true }, "the main window after starting");
    }

    /// <summary>Starts the app on its first run: the sign-in window opens and waits, so this does not wait for it.</summary>
    public Task StartFirstRun()
    {
        var start = Wpf.Post(Tray.Start);
        _running.Add(start);
        return start;
    }

    /// <summary>Waits until the first sync pass is done.</summary>
    public Task SyncedOnce() => Wpf.WaitUntil(() => Tray.Session?.Status is { Status: RunnerStatus.Idle, LastSyncAt: not null }, "the first sync");

    public Task WaitUntil(Func<bool> condition, string what, int seconds = 30) => Wpf.WaitUntil(condition, what, TimeSpan.FromSeconds(seconds));

    /// <summary>Waits until the app opened something whose address matches, and returns it.</summary>
    public async Task<string> Opened(Func<string, bool> match, string what)
    {
        await Wpf.WaitUntil(() => Platform.Opened.Items.Any(match), what);
        return Platform.Opened.Items.Last(match);
    }

    /// <summary>The window of this type that is showing now (waits for it).</summary>
    public async Task<T> Showing<T>(string what) where T : Window
    {
        T? found = null;
        await Wpf.WaitUntil(() => (found = Application.Current.Windows.OfType<T>().LastOrDefault(w => w.IsVisible)) is not null, what);
        return found!;
    }

    // ------------------------------------------------------------------ pressing

    /// <summary>Clicks a tray menu item by its text, as a person does after right-clicking the icon.</summary>
    public async Task Menu(string text)
    {
        await Wpf.Ui(() =>
        {
            var items = Tray.TrayIcon.ContextMenuStrip!.Items.OfType<Forms.ToolStripMenuItem>().ToList();
            var item = items.FirstOrDefault(i => i.Text == text)
                ?? throw new XunitException($"The tray menu has no '{text}' item. It has: {string.Join(", ", items.Where(i => i.Available).Select(i => $"'{i.Text}'"))}.");
            if (!item.Available || !item.Enabled)
                throw new XunitException($"The tray menu's '{text}' item is {(item.Available ? "greyed out" : "hidden")}.");
            item.PerformClick();
            PressCoverage.Add("tray menu: " + MenuItems[item]);
        });
        await AfterPress($"Pressing '{text}' in the tray menu");
    }

    /// <summary>Right-clicks the icon: the menu opens and reads "start with Windows" from Windows.</summary>
    public async Task OpenMenu()
    {
        await Wpf.Ui(() =>
        {
            var menu = Tray.TrayIcon.ContextMenuStrip!;
            typeof(Forms.ToolStripDropDown).GetMethod("OnOpening", BindingFlags.NonPublic | BindingFlags.Instance)!
                .Invoke(menu, [new System.ComponentModel.CancelEventArgs()]);
        });
        await AfterPress("Opening the tray menu");
    }

    /// <summary>A left click on the tray icon (the quick-look panel).</summary>
    public Task ClickIcon() => IconEvent("OnMouseClick", "click");

    /// <summary>A double click on the tray icon (the main window).</summary>
    public Task DoubleClickIcon() => IconEvent("OnMouseDoubleClick", "double-click");

    private async Task IconEvent(string method, string what)
    {
        await Wpf.Ui(() =>
        {
            var raise = typeof(Forms.NotifyIcon).GetMethod(method, BindingFlags.NonPublic | BindingFlags.Instance, [typeof(Forms.MouseEventArgs)])
                ?? throw new XunitException($"WinForms changed: NotifyIcon.{method} is gone, so the tray icon cannot be clicked in tests.");
            raise.Invoke(Tray.TrayIcon, [new Forms.MouseEventArgs(Forms.MouseButtons.Left, what == "click" ? 1 : 2, 0, 0, 0)]);
            PressCoverage.Add("tray icon: " + what);
        });
        await AfterPress($"A {what} on the tray icon");
    }

    /// <summary>Presses the control labelled <paramref name="label"/> in <paramref name="window"/> (waits for it to show).</summary>
    public async Task Press(Window window, string label, string? where = null)
    {
        where ??= Describe(window);
        Target? target = null;
        try
        {
            await Wpf.WaitUntil(() => (target = Presser.Find(window).FirstOrDefault(t => t.Label == label)) is not null, $"'{label}' on {where}",
                TimeSpan.FromSeconds(15));
        }
        catch (TimeoutException)
        {
            var shown = await Wpf.Ui(() => string.Join(", ", Presser.Find(window).Select(t => t.ToString())));
            throw new XunitException($"There is no '{label}' to press on {where}. It shows: {shown}.");
        }
        var ran = await Wpf.Ui(() => Presser.Press(target!));
        PressCoverage.Add(target!.Handlers);
        PressCoverage.Add(ran);
        await AfterPress($"Pressing '{label}' on {where}");
    }

    /// <summary>Types into a text box (as a person does, every change reaches the app).</summary>
    public Task Type(System.Windows.Controls.TextBox box, string text) => Wpf.Ui(() => box.Text = text);

    private async Task AfterPress(string what)
    {
        await Wpf.Settle();
        if (Wpf.TakeCrashes() is { Count: > 0 } crashes)
            throw new XunitException($"{what} crashed: {crashes[0]}");
    }

    public static string Describe(Window window) => window switch
    {
        MainWindow => "the main window",
        TrayPanel => "the tray panel",
        SettingsWindow => "the sign-in window",
        ServerUpdateWindow => "the server update window",
        BugReportWindow => "the bug report window",
        _ => window.Title,
    };

    // ------------------------------------------------------------------ the nest

    /// <summary>The settings a test nest needs for <see cref="Website"/>: it sits behind a tunnel, like sync.pairnets.app.</summary>
    public static Dictionary<string, string?> BehindATunnel(Dictionary<string, string?>? more = null) =>
        new(more ?? []) { ["Sync:TrustProxyHeaders"] = "true" };

    /// <summary>
    /// A browser on the nest's website, reaching it the way the Cloudflare tunnel does: plain HTTP on this machine, with
    /// the tunnel's word that it came in over HTTPS for the nest's own name. (The test nest's own HTTPS port needs a
    /// certificate chain that not every Windows computer can build; this way the tests do not depend on it.)
    /// </summary>
    public static HttpClient Website(TestServer nest)
    {
        var web = new HttpClient(new SocketsHttpHandler { UseProxy = false, UseCookies = false, AllowAutoRedirect = false }) { BaseAddress = nest.Url };
        web.DefaultRequestHeaders.Host = "localhost";
        web.DefaultRequestHeaders.Add("X-Forwarded-Proto", "https");
        web.DefaultRequestHeaders.Add("Origin", "https://localhost");
        return web;
    }

    /// <summary>The nest's owner, signed in on its website (as when approving a computer there).</summary>
    public static async Task<HttpClient> OwnerOnTheWebsite(TestServer nest)
    {
        var web = Website(nest);
        var code = nest.Services.GetRequiredService<AuthStore>().CreateSetupCode();
        var setup = await web.PostAsJsonAsync("web/api/setup", new { code });
        if (setup.StatusCode != HttpStatusCode.NoContent || !setup.Headers.TryGetValues("Set-Cookie", out var cookies))
            throw new XunitException($"Could not sign in to the test nest's website: {setup.StatusCode}");
        // The session cookie is Secure: a browser sends it back over the tunnel's HTTPS, this client does it by hand.
        web.DefaultRequestHeaders.Add("Cookie", string.Join("; ", cookies.Select(c => c.Split(';')[0])));
        return web;
    }

    /// <summary>The sign-in code in a link the app opened ("…/link?code=ABCD-EFGH&…").</summary>
    public static string CodeIn(string link) =>
        System.Web.HttpUtility.ParseQueryString(new Uri(link).Query)["code"] ?? throw new XunitException("No code in " + link);

    public async ValueTask DisposeAsync()
    {
        try
        {
            await Wpf.Ui(() => Tray?.Dispose());
            await Wpf.CloseAllWindows();
            await Task.WhenAll(_running).WaitAsync(TimeSpan.FromSeconds(30));
        }
        finally
        {
            Wpf.TakeCrashes();
            _dialogs.Dispose();
            _loggers.Dispose();
            _fileLog.Dispose();
            Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools(); // the sync notes, so the folder can go
            Root.Dispose();
        }
    }
}

/// <summary>Which XAML handlers and tray items the tests pressed (the guard checks that nothing is left out).</summary>
public static class PressCoverage
{
    private static readonly HashSet<string> Pressed = [];

    public static void Add(string what)
    {
        lock (Pressed)
            Pressed.Add(what);
    }

    public static void Add(IEnumerable<string> what)
    {
        foreach (var w in what)
            Add(w);
    }

    public static bool Contains(string what)
    {
        lock (Pressed)
            return Pressed.Contains(what);
    }
}
