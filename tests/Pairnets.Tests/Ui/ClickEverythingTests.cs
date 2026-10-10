using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Headless.XUnit;
using Avalonia.Input;
using Avalonia.Input.Platform;
using Avalonia.Threading;
using Avalonia.VisualTree;
using Microsoft.Extensions.DependencyInjection;
using Pairnets.Core;
using Pairnets.Core.Client;
using Pairnets.Core.Settings;
using Pairnets.Core.Sync;
using Pairnets.Desktop.Views;
using Pairnets.Server.Storage;
using Pairnets.Tests.Infrastructure;
using Xunit.Abstractions;

namespace Pairnets.Tests.Ui;

/// <summary>What a press should have done: null when it did, otherwise what went wrong ("should …, but …").</summary>
public delegate Task<string?> Check();

/// <summary>The test servers, folders and release feed the button tests share, and what each window's run found.</summary>
public sealed class ClickEverythingFixture : IAsyncLifetime
{
    private readonly TempDir _dir = new("click-everything");

    /// <summary>A plain nest (no website) for the Settings page.</summary>
    public TestServer Nest { get; private set; } = null!;

    /// <summary>A nest with its website, Google and email sign-in, for the sign-in window.</summary>
    public TestServer Website { get; private set; } = null!;

    /// <summary>A release feed with a newer version on it ("Check now").</summary>
    public FakeReleaseFeed Feed { get; private set; } = null!;

    /// <summary>The synced folder the sample settings use (kept empty).</summary>
    public string Folder => _dir.Combine("Work");

    /// <summary>What the folder picker answers.</summary>
    public string PickedFolder => _dir.Combine("Picked");

    public string Reports => _dir.Combine("reports");

    internal Dictionary<string, Task<ClickReport>> Runs { get; } = [];

    public async Task InitializeAsync()
    {
        Directory.CreateDirectory(Folder);
        Directory.CreateDirectory(PickedFolder);
        Nest = await TestServer.StartAsync();
        Website = await TestServer.StartWithWebsiteAsync(new()
        {
            ["Sync:GoogleClientId"] = "client-123.apps.googleusercontent.com",
            ["Sync:GoogleClientSecret"] = "test-oauth-secret",
            ["Sync:SmtpHost"] = "127.0.0.1",
            ["Sync:SmtpFrom"] = "nest@example.com",
        });
        // The Google and email buttons show once the owner has those ways in.
        var auth = Website.Services.GetRequiredService<AuthStore>();
        auth.SetOwnerEmail("owner@example.com");
        auth.SetGoogleAccount("e2e-google-user", "owner@example.com");
        Feed = await FakeReleaseFeed.StartAsync("99.0.0");
    }

    public async Task DisposeAsync()
    {
        await Nest.DisposeAsync();
        await Website.DisposeAsync();
        await Feed.DisposeAsync();
        _dir.Dispose();
    }
}

/// <summary>What pressing everything in one group of windows found.</summary>
public sealed class ClickReport
{
    public List<string> Problems { get; } = [];

    /// <summary>The app's handlers that ran, as "MainWindow.OnSyncNow".</summary>
    public HashSet<string> Pressed { get; } = [];

    public int Presses { get; set; }
}

/// <summary>A window in one state, and what each control on it should do when pressed.</summary>
public sealed class Stage : IAsyncDisposable
{
    private readonly List<Window> _windows = [];
    private readonly IDisposable _tracking;

    public Stage(string where)
    {
        Where = where;
        _tracking = Window.WindowOpenedEvent.AddClassHandler(typeof(Window), (sender, _) =>
        {
            if (sender is Window window && !_windows.Contains(window))
                _windows.Add(window);
        });
    }

    /// <summary>"the main window (overview)": used in the messages.</summary>
    public string Where { get; }

    public Window Window { get; private set; } = null!;

    public SampleActions Actions { get; } = new();

    /// <summary>Events a view raised ("saved", "reset", …).</summary>
    public List<string> Events { get; } = [];

    /// <summary>Links and files a view asked to open.</summary>
    public List<string> Opened { get; } = [];

    /// <summary>
    /// What each control should do, by the handler it runs ("OnSyncNow"), the handler and what the control says
    /// ("OnNav:History", a trailing * matches the start), or only what it says (":Yes").
    /// </summary>
    public Dictionary<string, Check> Expect { get; } = new(StringComparer.Ordinal);

    /// <summary>What to type into a text field (by its name).</summary>
    public Dictionary<string, string> Typing { get; } = [];

    public List<Func<Task>> Cleanup { get; } = [];

    /// <summary>Every window opened since the stage began (dialogs included).</summary>
    public IReadOnlyList<Window> Windows => _windows.ToList();

    public T Show<T>(T window) where T : Window
    {
        Window = window;
        window.Show();
        Dispatcher.UIThread.RunJobs();
        return window;
    }

    /// <summary>Presses go to <paramref name="window"/> (a question the stage's window opened).</summary>
    public void PressIn(Window window) => Window = window;

    public async ValueTask DisposeAsync()
    {
        _tracking.Dispose();
        foreach (var window in _windows.Append(Window).Distinct().Reverse())
        {
            if (window is MainWindow main)
                main.AllowClose = true;
            window.Close();
        }
        Dispatcher.UIThread.RunJobs();
        foreach (var cleanup in Cleanup)
            await cleanup();
    }
}

/// <summary>
/// Presses every button, toggle, radio button, check box, menu item and context-menu item in every window of the
/// Mac/Linux app, in every state that shows different buttons, with real mouse clicks, and checks that each press
/// does what it says. A button nobody knows what to expect from, a button with nothing behind it, a crash and a
/// button that cannot be clicked (hidden or covered) all fail in plain English. Last, a guard checks that every
/// handler the AXAML files name was pressed.
/// </summary>
public sealed class ClickEverythingTests(ClickEverythingFixture fixture, ITestOutputHelper output) : IClassFixture<ClickEverythingFixture>
{
    private sealed record Scene(string Where, Func<Stage, Task> Arrange);

    // ------------------------------------------------------------------ the tests

    [AvaloniaFact]
    public async Task TheMainWindow_EveryButtonDoesWhatItSays() => Pass(await RunAsync("main window"));

    [AvaloniaFact]
    public async Task TheTrayPanel_EveryButtonDoesWhatItSays() => Pass(await RunAsync("tray panel"));

    [AvaloniaFact]
    public async Task TheSettingsPage_EveryButtonDoesWhatItSays() => Pass(await RunAsync("settings"));

    [AvaloniaFact]
    public async Task TheSignInWindow_EveryButtonDoesWhatItSays() => Pass(await RunAsync("sign-in"));

    [AvaloniaFact]
    public async Task TheServerUpdateWindow_EveryButtonDoesWhatItSays() => Pass(await RunAsync("server update"));

    [AvaloniaFact]
    public async Task TheBugReportWindow_EveryButtonDoesWhatItSays() => Pass(await RunAsync("bug report"));

    [AvaloniaFact]
    public async Task TheQuestions_EveryButtonDoesWhatItSays() => Pass(await RunAsync("questions"));

    /// <summary>Every handler an AXAML file names ran at least once in the runs above.</summary>
    [AvaloniaFact]
    public async Task EveryHandlerInTheAxamlIsPressedByATest()
    {
        var pressed = new HashSet<string>();
        foreach (var group in Groups.Keys)
            pressed.UnionWith((await RunAsync(group)).Pressed);
        var never = AxamlHandlers()
            .Where(h => !pressed.Contains($"{h.View}.{h.Handler}"))
            .Select(h => $"The button {h.Handler} in {h.View}.axaml is never pressed by a test.")
            .ToList();
        Assert.True(never.Count == 0, string.Join(Environment.NewLine, never));
    }

    /// <summary>Every switch, choice and box on the Settings page ends up in what Save hands to the app.</summary>
    [AvaloniaFact]
    public async Task SavingSettingsKeepsEveryChoice()
    {
        await using var stage = new Stage("the Settings page");
        var view = await SettingsStage(stage, ownKey: true);
        foreach (var label in new[] { "Start Pairnets when I log in", "Check for updates when Pairnets starts", "Update the server automatically", "Debug mode",
                     "Wait while my other computer uploads many files", "Limit upload to the server to", "Limit download from the server to", "2" })
            Person.Click(Screen.Find<ToggleButton>(view, label));
        Person.Expand(Screen.All<Expander>(view).Single());
        Person.Type(Screen.Named<TextBox>(view, "IgnoreBox"), "*.bak");
        Person.Click(Screen.Find<Button>(view, "Save"));
        await Wait.Until(() => stage.Events.Contains("saved"), "Save to hand the settings to the app");

        var saved = view.Result!;
        Assert.True(saved.StartWithWindows);
        Assert.False(saved.CheckForUpdates);
        Assert.True(saved.AutoUpdateServer);
        Assert.True(saved.DebugMode);
        Assert.False(saved.WaitForPeerBatches);
        Assert.Equal(2, saved.ParallelTransfers);
        Assert.Equal(5, saved.UploadLimitMBps);
        Assert.Equal(10, saved.DownloadLimitMBps);
        Assert.Equal(["*.bak"], saved.ExtraIgnore);
        Assert.Equal(fixture.Folder, saved.Folder);
    }

    private void Pass(ClickReport report)
    {
        output.WriteLine($"{report.Presses} presses, {report.Pressed.Count} handlers: {string.Join(", ", report.Pressed.Order())}");
        Assert.True(report.Presses > 0, "Nothing was pressed: the windows did not show any button.");
        Assert.True(report.Problems.Count == 0, string.Join(Environment.NewLine, report.Problems));
    }

    // ------------------------------------------------------------------ the windows, in every state

    private Dictionary<string, Scene[]> Groups => new()
    {
        ["main window"] =
        [
            new("the main window (overview)", s => MainStage(s, w =>
            {
                w.ShowUpdate("Pairnets 1.0.58 is available", "You have 1.0.52. Your settings are kept.", "Download");
                w.ShowStatus(Idle, "/Users/me/Work", "MacBook");
                w.ShowActivity(SampleFeed().Items);
                w.ShowAttention([Attention(s)]);
            })),
            new("the main window (blocked, deletions to allow)", s => MainStage(s, w => w.ShowStatus(Idle with
            {
                Status = RunnerStatus.Blocked, BlockReason = BlockReason.MassDelete, PendingDeletes = 12, Text = "This pass would delete 12 files.",
            }, "/Users/me/Work"))),
            new("the main window (paused)", s => MainStage(s, w => w.ShowStatus(Idle with { Status = RunnerStatus.Paused, Paused = true, Text = "Paused" }, "/Users/me/Work"))),
            new("the main window (waiting for the other computer)", s => MainStage(s, w => w.ShowStatus(Idle with { WaitingFor = new PeerWait("DESKTOP", 340, 12) }, "/Users/me/Work"))),
            new("the main window (Activity page)", s => MainStage(s, w =>
            {
                w.ShowActivity(SampleFeed().Items);
                w.Navigate(MainPage.Activity);
            })),
            new("the main window (History page)", s => MainStage(s, w => w.Navigate(MainPage.History), async w => await w.LoadHistoryAsync())),
            new("the main window (History page, a file chosen)", s => MainStage(s, w => w.Navigate(MainPage.History), async w =>
            {
                await w.LoadHistoryAsync();
                w.SelectHistoryRow(0);
                await Wait.Until(() => w.VersionRows == 3, "the versions of the chosen file to show");
            })),
            new("the main window (History page, server unreachable)", s =>
            {
                s.Actions.HistoryFrom = new CountingHistory(null);
                return MainStage(s, w => w.Navigate(MainPage.History), async w => await w.LoadHistoryAsync());
            }),
            new("the main window (Devices page)", s => MainStage(s, w =>
            {
                w.ShowStatus(Idle, "/Users/me/Work", "MacBook");
                w.Navigate(MainPage.Devices);
            })),
            new("the main window (Needs attention page)", s => MainStage(s, w =>
            {
                w.ShowAttention([Attention(s), Attention(s)]);
                w.Navigate(MainPage.Attention);
            })),
        ],
        ["tray panel"] =
        [
            new("the tray panel", s => TrayStage(s, p =>
            {
                p.ShowStatus(Idle, "MacBook");
                p.ShowActivity(SampleFeed().Items);
            })),
            new("the tray panel (blocked, things to decide)", s => TrayStage(s, p =>
            {
                p.ShowStatus(Idle with { Status = RunnerStatus.Blocked, BlockReason = BlockReason.MassDelete, PendingDeletes = 3 }, "MacBook");
                p.ShowAttention(2);
            })),
        ],
        ["settings"] =
        [
            new("the Settings page (signed in with its own key)", s => SettingsStage(s, ownKey: true)),
            new("the Settings page (on the shared token)", s => SettingsStage(s, ownKey: false)),
        ],
        ["sign-in"] =
        [
            new("the sign-in window (nest found)", s => SignInStage(s)),
            new("the sign-in window (email typed)", s => SignInStage(s, view =>
            {
                Screen.Named<TextBox>(view, "EmailBox").Text = "owner@example.com";
                return Task.CompletedTask;
            })),
            new("the sign-in window (waiting for approval)", s => SignInStage(s, view => StartSignInAsync(s, view))),
            new("the sign-in window (turned down on the nest)", s => SignInStage(s, async view =>
            {
                await StartSignInAsync(s, view);
                DenyWaitingComputers();
                await Wait.Until(() => Screen.Named<Button>(view, "RetryButton").IsEffectivelyVisible, "the turned-down sign-in to offer \"Start over\"");
            })),
            new("the sign-in window (choosing the folder)", s => SignInStage(s, view =>
            {
                view.ShowFolderStep(fixture.Website.MintKey("MACBOOK-" + Guid.NewGuid().ToString("N")[..6]), fixture.Website.Url);
                view.Folder = fixture.Folder;
                return Task.CompletedTask;
            })),
        ],
        ["server update"] =
        [
            new("the server update window (asking)", s => ServerUpdateStage(s, new ServerUpdateWindow(() => null, "1.0.52", "1.0.58"))),
            new("the server update window (up to date)", s => ServerUpdateStage(s, new ServerUpdateWindow(() => null, "1.0.58", "1.0.58"))),
            new("the server update window (one-time setup)", s => ServerUpdateStage(s, new ServerUpdateWindow(() => null, "1.0.52", "1.0.58"),
                w => w.ShowResult(new ServerUpdateResult(false, "Old server", "1.0.52", CanUpdateItself: false)))),
            new("the server update window (not updated)", s => ServerUpdateStage(s, new ServerUpdateWindow(() => null, "1.0.52", "1.0.58"),
                w => w.ShowResult(new ServerUpdateResult(false, "The download did not match its checksum.", "1.0.52", CanUpdateItself: true)))),
            new("the server update window (Debug mode)", s => ServerUpdateStage(s, new ServerUpdateWindow(() => null, "1.0.52", "1.0.58", debug: () => true),
                w => w.AppendDetail("[  0s] Asking the server to update"))),
        ],
        ["bug report"] =
        [
            new("the bug report window", BugReportStage),
        ],
        ["questions"] =
        [
            new("a yes/no question", s => QuestionStage(s, confirm: true)),
            new("a message", s => QuestionStage(s, confirm: false)),
        ],
    };

    // ------------------------------------------------------------------ main window

    private static readonly ServerInfo SampleServer = new("id", 1, 1, "1.0.58", 412L << 30, 1L << 40);

    private static StatusSnapshot Idle => StatusSnapshot.Initial with
    {
        Status = RunnerStatus.Idle, Text = "Up to date", LastSyncAt = DateTimeOffset.Now, Server = SampleServer,
        Devices =
        [
            new DeviceInfo("MacBook", DateTimeOffset.UtcNow.AddDays(-30), DateTimeOffset.UtcNow, true, "1.0.58", "macOS"),
            new DeviceInfo("DESKTOP", DateTimeOffset.UtcNow.AddDays(-30), DateTimeOffset.UtcNow, true, "1.0.58", "Windows"),
        ],
    };

    /// <summary>Activity rows for files the sample History knows, so "Show versions…" finds them.</summary>
    private static ActivityFeed SampleFeed()
    {
        var now = DateTimeOffset.Now;
        var feed = new ActivityFeed();
        feed.Add(new ActivityItem(now.AddHours(-3), ActivityKind.Downloaded, "Projects/2026/plan.xlsx", "Downloaded"));
        feed.Add(new ActivityItem(now.AddMinutes(-47), ActivityKind.Uploaded, "Notes/meeting-notes.md", "Uploaded"));
        feed.Add(new ActivityItem(now.AddMinutes(-5), ActivityKind.Uploaded, "Projects/report.docx", "Uploaded"));
        return feed;
    }

    private static AttentionItem Attention(Stage s) =>
        new("Conflict copy: report (conflict LAPTOP).docx", "Both computers changed this file.", "Show in folder", () => s.Events.Add("attention"));

    private static async Task MainStage(Stage s, Action<MainWindow> arrange, Func<MainWindow, Task>? settle = null)
    {
        var a = s.Actions;
        var history = (CountingHistory)(a.HistoryFrom ??= new CountingHistory(a.Samples));
        var w = s.Show(new MainWindow(a) { Width = 980, Height = 700 });
        arrange(w);
        if (settle is not null)
            await settle(w);
        Dispatcher.UIThread.RunJobs();
        a.Calls.Clear(); // only what the press asks for counts
        var reads = history.Reads;
        s.Typing["HistorySearch"] = "taxes";
        s.Expect["OnUpdateNow"] = Calls(a, "update");
        s.Expect["OnUpdateLater"] = Calls(a, "dismiss");
        s.Expect["OnOpenFolder"] = Calls(a, "folder");
        s.Expect["OnPause"] = Calls(a, "pause");
        s.Expect["OnSyncNow"] = Calls(a, "sync");
        s.Expect["OnViewLog"] = Calls(a, "log");
        s.Expect["OnReportBug"] = Calls(a, "bug");
        s.Expect["OnFix"] = Calls(a, "fix");
        s.Expect["OnDownloadNow"] = Calls(a, "download");
        s.Expect["OnUpdateServer"] = Calls(a, "server");
        s.Expect["OnAddComputer"] = Calls(a, "add-computer");
        s.Expect["OnManageDevices"] = Calls(a, "manage-devices");
        s.Expect["OnReviewAttention"] = OnPage(w, MainPage.Attention);
        s.Expect["OnSeeAllActivity"] = OnPage(w, MainPage.Activity);
        s.Expect["OnNav:Overview"] = OnPage(w, MainPage.Overview);
        s.Expect["OnNav:Activity"] = OnPage(w, MainPage.Activity);
        s.Expect["OnNav:History"] = All(OnPage(w, MainPage.History), That(() => w.HistoryRows == 4, "the deleted files show"));
        s.Expect["OnNav:Devices"] = All(OnPage(w, MainPage.Devices), Calls(a, "devices"));
        s.Expect["OnNav:Needs attention*"] = OnPage(w, MainPage.Attention);
        s.Expect["OnNav:Settings"] = Calls(a, "settings"); // the controller hands over a fresh form
        s.Expect["OnRevealActivity"] = That(() => a.Calls.Count == 1 && a.Calls[0].StartsWith("reveal:", StringComparison.Ordinal), "the app show the file in its folder");
        s.Expect["OnActivityVersions"] = That(() => w.Page == MainPage.History && w.HistoryRows == 1 && w.VersionRows == 3, "History open on that file with its versions");
        s.Expect["OnHistoryModeChanged:All files"] = That(() => w.HistoryRows == 3, "the current files show");
        s.Expect["OnHistoryModeChanged:Deleted files"] = That(() => w.HistoryRows == 4, "the deleted files show");
        s.Expect["OnHistorySearch"] = That(() => w.HistoryRows == 1, "only the matching file show");
        s.Expect["OnHistoryReload"] = That(() => history.Reads == reads + 1 && (history.Unreachable || w.HistoryRows == 4), "the files be read again",
            () => $"read {history.Reads - reads} time(s), {w.HistoryRows} row(s)");
        s.Expect["OnFileSelected"] = That(() => w.VersionRows == 3, "the versions of that file show");
        s.Expect["OnRestore"] = That(() => a.Samples.Restored.Count == 1 && w.RestoreText.StartsWith("Restored.", StringComparison.Ordinal), "that version come back",
            () => $"{a.Samples.Restored.Count} restored, it says \"{w.RestoreText}\"");
        s.Expect["OnAttentionAction"] = That(() => s.Events.SequenceEqual(["attention"]), "the item's action run");
    }

    /// <summary>Counts how often the file list is read; with no <paramref name="inner"/> source the server cannot be reached.</summary>
    private sealed class CountingHistory(IHistorySource? inner) : IHistorySource
    {
        public int Reads { get; private set; }

        public bool Unreachable => inner is null;

        public Task<IReadOnlyList<ServerFile>> GetServerFilesAsync(CancellationToken ct)
        {
            Reads++;
            return inner?.GetServerFilesAsync(ct) ?? throw new Pairnets.Core.Api.PairnetsNetworkException("Connection refused");
        }

        public Task<IReadOnlyList<HistoryVersion>> GetVersionsAsync(string path, CancellationToken ct) =>
            inner?.GetVersionsAsync(path, ct) ?? Task.FromResult<IReadOnlyList<HistoryVersion>>([]);

        public Task<string?> RestoreVersionAsync(string path, HistoryVersion version, CancellationToken ct) =>
            inner?.RestoreVersionAsync(path, version, ct) ?? Task.FromResult<string?>("Not connected.");
    }

    // ------------------------------------------------------------------ tray panel

    private static Task TrayStage(Stage s, Action<TrayPanel> arrange)
    {
        var panel = s.Show(new TrayPanel(s.Actions));
        arrange(panel);
        Dispatcher.UIThread.RunJobs();
        var a = s.Actions;
        var menu = Screen.All<Button>(panel).Single(b => b.Flyout is not null).Flyout!;
        Check hides = That(() => !panel.IsVisible, "the panel close");
        s.Expect["OnMenuOpened"] = That(() => menu.IsOpen && panel.IsVisible, "its menu open while the panel stays");
        s.Expect["OnFix"] = All(Calls(a, "fix"), hides);
        s.Expect["OnAttention"] = All(Calls(a, "open:Attention"), hides);
        s.Expect["OnOpenApp"] = All(Calls(a, "open:Overview"), hides);
        s.Expect["OnOpenFolder"] = All(Calls(a, "folder"), hides);
        s.Expect["OnPause"] = Calls(a, "pause");
        s.Expect["OnSyncNow"] = Calls(a, "sync");
        s.Expect["OnSettings"] = All(Calls(a, "open:Settings"), hides);
        s.Expect["OnHistory"] = All(Calls(a, "open:History"), hides);
        s.Expect["OnViewLog"] = All(Calls(a, "log"), hides);
        s.Expect["OnReportBug"] = All(Calls(a, "bug"), hides);
        s.Expect["OnQuit"] = All(Calls(a, "quit"), hides);
        return Task.CompletedTask;
    }

    // ------------------------------------------------------------------ settings

    private Task<SettingsView> SettingsStage(Stage s, bool ownKey)
    {
        var secrets = new MemorySecrets();
        var key = ownKey ? fixture.Nest.MintKey("MACBOOK-" + Guid.NewGuid().ToString("N")[..6]) : null;
        var settings = new ClientSettings
        {
            ServerUrl = fixture.Nest.Url.ToString(),
            ProtectedToken = secrets.Protect(key?.Key ?? fixture.Nest.Token),
            DeviceId = key?.Id,
            DeviceName = key?.Name ?? "MACBOOK",
            Folder = fixture.Folder,
            FirstRunCompleted = true,
        };
        var updates = new UpdateService(new UpdateChecker(new HttpClient(), fixture.Feed.DownloadBase, fixture.Feed.ReleasePage), "1.0.0", fixture.Feed.Asset);
        s.Cleanup.Add(() =>
        {
            updates.Dispose();
            return Task.CompletedTask;
        });
        var view = new SettingsView(settings, secrets, autoStart: false, updates, "Server 1.0.0");
        view.Saved += _ => s.Events.Add("saved");
        view.Cancelled += () => s.Events.Add("cancelled");
        view.SignOutRequested += () => s.Events.Add("sign-out");
        view.ResetRequested += () => s.Events.Add("reset");
        view.ManageDevicesRequested += () => s.Events.Add("manage-devices");
        s.Show(new Window { Content = view, Width = 700, Height = 780 });

        var expected = fixture.Feed.Asset is null ? "up to date" : "version 99.0.0 is available";
        s.Expect["OnManageDevices"] = Raised(s, "manage-devices");
        s.Expect["OnSignOut"] = Raised(s, "sign-out");
        s.Expect["OnReset"] = Raised(s, "reset");
        s.Expect["OnCancel"] = Raised(s, "cancelled");
        s.Expect["OnSave"] = All(Raised(s, "saved"), That(() => view.Result is { } r && r.Folder == fixture.Folder && r.DeviceId == key?.Id && r.FirstRunCompleted,
            "the saved settings keep this computer's folder and key"));
        s.Expect["OnShowAddress"] = That(() => Screen.Named<TextBox>(view, "ServerUrlBox").IsEffectivelyVisible, "the server address and token show");
        s.Expect["OnBrowse"] = That(() => Screen.Named<TextBox>(view, "FolderBox").Text == fixture.PickedFolder, "the picked folder fill the field");
        s.Expect["OnCheckNow"] = That(() => Screen.Named<TextBlock>(view, "VersionText").Text?.EndsWith(expected, StringComparison.Ordinal) == true, $"\"{expected}\" show");
        s.Expect["OnTest"] = That(() => Screen.Named<TextBlock>(view, "TestResult").Text?.StartsWith("✓", StringComparison.Ordinal) == true, "the test say the connection works");
        s.Expect["OnScrollWheel"] = That(() => Screen.Named<ScrollViewer>(view, "Scroller").Offset.Y > 0, "the form scroll");
        return Task.FromResult(view);
    }

    // ------------------------------------------------------------------ sign-in

    private async Task SignInStage(Stage s, Func<SignInView, Task>? arrange = null)
    {
        var window = s.Show(new SettingsWindow(new ClientSettings { DeviceName = "MACBOOK" }, new MemorySecrets(), autoStart: false, s.Opened.Add));
        var view = window.SignIn;
        var check = await Nest.CheckAsync(fixture.Website.Url.ToString());
        Assert.Equal(NestCheckStatus.Found, check.Status);
        view.ShowAddress(fixture.Website.Url.ToString(), check);
        s.Cleanup.Add(() =>
        {
            DenyWaitingComputers();
            return Task.CompletedTask;
        });
        if (arrange is not null)
            await arrange(view);
        var shownCode = Screen.Named<TextBlock>(view, "CodeText").Text;

        bool Waiting() => Screen.Named<StackPanel>(view, "WaitStep").IsEffectivelyVisible && Screen.Named<TextBlock>(view, "CodeText").Text is { Length: 9 };
        Check OpensTheNest(string? method) => That(() => Waiting() && s.Opened.Count == 1 && s.Opened[0].Contains("/link?code=", StringComparison.Ordinal)
            && (method is null ? !s.Opened[0].Contains("method=", StringComparison.Ordinal) : s.Opened[0].Contains(method, StringComparison.Ordinal)),
            "the nest's approval page open" + (method is null ? string.Empty : $" with {method}"));
        s.Expect["OnSignIn"] = OpensTheNest(null);
        s.Expect["OnGoogle"] = OpensTheNest("method=google");
        s.Expect["OnEmail"] = OpensTheNest("method=email&email=owner%40example.com");
        s.Expect["OnOpenAgain"] = That(() => s.Opened.Count == 2 && s.Opened[1] == s.Opened[0], "the approval page open again");
        s.Expect["OnCopy"] = All(That(() => Screen.Named<Button>(view, "CopyButton").Content as string == "Copied", "the button say \"Copied\""),
            Clipboard(window, () => s.Opened[0]));
        s.Expect["OnBack"] = That(() => Screen.Named<StackPanel>(view, "WelcomeStep").IsEffectivelyVisible, "the first step show again");
        s.Expect["OnRetry"] = That(() => Waiting() && Screen.Named<TextBlock>(view, "CodeText").Text != shownCode, "a new code show");
        s.Expect["OnBrowse"] = That(() => view.Folder == fixture.PickedFolder, "the picked folder fill the field");
        s.Expect["OnStart"] = That(() => window.Result is { } r && r.Folder == fixture.Folder && r.FirstRunCompleted && !window.IsVisible,
            "the window close with this computer signed in");
    }

    /// <summary>"Sign in with your browser": waits until the code shows.</summary>
    private static async Task StartSignInAsync(Stage s, SignInView view)
    {
        Person.Click(Screen.Named<Button>(view, "BrowserButton"));
        await Wait.Until(() => s.Opened.Count == 1, "the nest's approval page to open");
    }

    /// <summary>Turns down every computer waiting on the website nest (each nest lets only 3 wait per address).</summary>
    private void DenyWaitingComputers()
    {
        var auth = fixture.Website.Services.GetRequiredService<AuthStore>();
        foreach (var request in auth.ListPendingPairRequests())
            auth.DecidePairRequest(request.Id, approve: false, "test");
    }

    // ------------------------------------------------------------------ server update, bug report, questions

    private static Task ServerUpdateStage(Stage s, ServerUpdateWindow window, Action<ServerUpdateWindow>? arrange = null)
    {
        s.Show(window);
        arrange?.Invoke(window);
        Dispatcher.UIThread.RunJobs();
        Check closed = That(() => !window.IsVisible, "the window close");
        Check notConnected = That(() => window.HeadingText == "Not updated yet" && window.ExplanationText.Contains("Not connected", StringComparison.Ordinal),
            "the window explain that it is not connected");
        s.Expect["OnSkip"] = All(closed, That(() => window.Skipped, "this version be skipped"));
        s.Expect["OnLater"] = closed;
        s.Expect["OnUpdate"] = notConnected;
        s.Expect["OnCopy"] = Clipboard(window, () => "curl -fsSL https://raw.githubusercontent.com/MRnigth/Pairnets/main/deploy/get.sh | sudo bash");
        s.Expect["OnCopyDetails"] = Clipboard(window, () => "[  0s] Asking the server to update" + Environment.NewLine);
        return Task.CompletedTask;
    }

    private async Task BugReportStage(Stage s)
    {
        Directory.CreateDirectory(fixture.Reports);
        var window = s.Show(new BugReportWindow(() => Task.FromResult("Pairnets bug report\nline two"), s.Opened.Add, saveDirectory: fixture.Reports));
        await Wait.Until(() => window.SavedPath is not null && Screen.Find<Button>(window, "Open file").IsEffectivelyEnabled, "the report to be ready");
        s.Expect["OnOpenFile"] = That(() => s.Opened.SequenceEqual([window.SavedPath!]), "the saved report open");
        s.Expect["OnCopy"] = All(Clipboard(window, () => "Pairnets bug report\nline two"), That(() => Screen.Named<TextBlock>(window, "Heading").Text == "Bug report copied", "it say it was copied"));
        s.Expect["OnClose"] = That(() => !window.IsVisible, "the window close");
    }

    private static async Task QuestionStage(Stage s, bool confirm)
    {
        var owner = s.Show(new Window { Title = "Owner", Width = 600, Height = 400 });
        var answer = confirm ? Dialogs.ConfirmAsync(owner, "Pairnets – a question", "Continue?", "Continue", "Cancel") : Dialogs.InfoAsync(owner, "Pairnets", "Done.").ContinueWith(_ => true, TaskScheduler.Default);
        var dialog = await Wait.For(() => s.Windows.FirstOrDefault(w => w != owner && w.IsVisible), "the question to show");
        s.PressIn(dialog);
        s.Expect[":Continue"] = That(() => answer.IsCompleted && answer.Result && !dialog.IsVisible, "the question close with yes");
        s.Expect[":Cancel"] = That(() => answer.IsCompleted && !answer.Result && !dialog.IsVisible, "the question close with no");
        s.Expect[":OK"] = That(() => answer.IsCompleted && !dialog.IsVisible, "the message close");
    }

    // ------------------------------------------------------------------ checks

    private static Check Calls(SampleActions actions, params string[] expected) => async () =>
    {
        try
        {
            await Wait.Until(() => actions.Calls.SequenceEqual(expected), "the call", seconds: 3);
            return null;
        }
        catch (Xunit.Sdk.XunitException)
        {
            return $"should ask the app for [{string.Join(", ", expected)}], but it asked for [{string.Join(", ", actions.Calls)}]";
        }
    };

    private static Check That(Func<bool> condition, string what, Func<string>? state = null) => async () =>
    {
        try
        {
            await Wait.Until(condition, what, seconds: 10);
            return null;
        }
        catch (Xunit.Sdk.XunitException)
        {
            return $"should make {what}, but that did not happen" + (state is null ? string.Empty : $" ({state()})");
        }
    };

    private static Check OnPage(MainWindow window, MainPage page) => That(() => window.Page == page, $"the {page} page show");

    private static Check Raised(Stage s, string name) => That(() => s.Events.SequenceEqual([name]), $"the view report \"{name}\"");

    private static Check Clipboard(Window window, Func<string> expected) => async () =>
    {
        var deadline = DateTime.UtcNow.AddSeconds(5);
        string? text = null;
        while (DateTime.UtcNow < deadline)
        {
            text = window.Clipboard is { } clipboard ? await clipboard.TryGetTextAsync() : null;
            if (text == expected())
                return null;
            await Task.Delay(50);
        }
        return $"should copy \"{expected()}\", but the clipboard has \"{text}\"";
    };

    private static Check All(params Check[] checks) => async () =>
    {
        foreach (var check in checks)
        {
            if (await check() is { } problem)
                return problem;
        }
        return null;
    };

    // ------------------------------------------------------------------ pressing

    private enum Kind
    {
        Click,
        RightClick,
        Type,
        Scroll,
        Expand,
    }

    /// <summary>Something a person can press, with the app handlers a press runs.</summary>
    private sealed record Pressable(string Key, string Label, Control Control, Kind Kind, IReadOnlyList<string> Handlers)
    {
        /// <summary>"OnSyncNow" for "MainWindow.OnSyncNow".</summary>
        public IEnumerable<string> HandlerNames => Handlers.Select(h => h[(h.LastIndexOf('.') + 1)..]).Where(n => n.StartsWith("On", StringComparison.Ordinal));
    }

    private Task<ClickReport> RunAsync(string group)
    {
        if (!fixture.Runs.TryGetValue(group, out var run))
            fixture.Runs[group] = run = PressGroupAsync(Groups[group]);
        return run;
    }

    private async Task<ClickReport> PressGroupAsync(Scene[] scenes)
    {
        var report = new ClickReport();
        var picker = Dialogs.FolderPicker;
        Dialogs.FolderPicker = (_, _) => Task.FromResult<string?>(fixture.PickedFolder);
        try
        {
            var done = new HashSet<string>(); // a control shared by several states is pressed once
            foreach (var scene in scenes)
            {
                var queue = new Queue<string[]>();
                await using (var first = new Stage(scene.Where))
                {
                    await scene.Arrange(first);
                    Dispatcher.UIThread.RunJobs();
                    DeadButtons(first, report);
                    foreach (var p in Pressables(first.Window))
                        queue.Enqueue([p.Key]);
                }
                while (queue.TryDequeue(out var path))
                {
                    if (done.Add(string.Join(" > ", path)))
                        await PressPathAsync(scene, path, queue, report);
                }
            }
        }
        finally
        {
            Dialogs.FolderPicker = picker;
        }
        return report;
    }

    /// <summary>Opens a fresh copy of the scene, presses along <paramref name="path"/> and checks what the last press did.</summary>
    private static async Task PressPathAsync(Scene scene, string[] path, Queue<string[]> queue, ClickReport report)
    {
        var crashes = new List<Exception>();
        DispatcherUnhandledExceptionEventHandler onCrash = (_, e) =>
        {
            crashes.Add(e.Exception);
            e.Handled = true;
        };
        var label = path[^1];
        var stage = new Stage(scene.Where);
        Dispatcher.UIThread.UnhandledException += onCrash;
        try
        {
            await scene.Arrange(stage);
            Dispatcher.UIThread.RunJobs();
            FlyoutBase? menu = null;
            for (var i = 0; i < path.Length; i++)
            {
                var target = Pressables(stage.Window).FirstOrDefault(p => p.Key == path[i]);
                if (target is null)
                {
                    report.Problems.Add($"On {scene.Where}, '{path[i]}' could not be found again after pressing {string.Join(" > ", path[..i])}.");
                    return;
                }
                label = target.Label;
                var last = i == path.Length - 1;
                var before = Pressables(stage.Window).Select(p => p.Key).ToHashSet();
                var check = last ? CheckFor(stage, target, report) : null;
                if (last && check is null)
                    return; // CheckFor said why
                Press(stage, target);
                report.Presses++;
                foreach (var handler in target.Handlers)
                    report.Pressed.Add(handler);
                if ((target.Control as Button)?.Flyout is { } flyout)
                    menu = flyout;
                if (!last)
                    continue;
                string? problem;
                try
                {
                    problem = await check!();
                }
                catch (Exception ex)
                {
                    problem = $"could not be checked: {ex.GetType().Name}: {ex.Message}";
                }
                if (problem is not null)
                    report.Problems.Add($"Pressing '{label}' on {scene.Where} {problem}.");
                if (menu is not null && !menu.IsOpen)
                {
                    foreach (var handler in Screen.EventHandlers(menu, "Closed"))
                        report.Pressed.Add(handler);
                }
                // A menu opened: its items are pressed next, each in a fresh copy of the scene.
                foreach (var item in Pressables(stage.Window).Where(p => p.Control is MenuItem && !before.Contains(p.Key)))
                    queue.Enqueue([.. path, item.Key]);
            }
        }
        catch (CannotPressException ex)
        {
            report.Problems.Add($"On {scene.Where}: {ex.Message}");
        }
        finally
        {
            Dispatcher.UIThread.RunJobs();
            Dispatcher.UIThread.UnhandledException -= onCrash;
            if (crashes.Count > 0)
                report.Problems.Add($"Pressing '{label}' on {scene.Where} crashed: {crashes[0].GetType().Name}: {crashes[0].Message}");
            await stage.DisposeAsync();
        }
    }

    /// <summary>What pressing <paramref name="target"/> should do; null (with the reason noted) when nobody said.</summary>
    private static Check? CheckFor(Stage stage, Pressable target, ClickReport report)
    {
        foreach (var handler in target.HandlerNames)
        {
            var key = handler + ":" + target.Label;
            if (stage.Expect.TryGetValue(key, out var exact))
                return exact;
            if (stage.Expect.FirstOrDefault(e => e.Key.EndsWith('*') && key.StartsWith(e.Key[..^1], StringComparison.Ordinal)).Value is { } prefix)
                return prefix;
            if (stage.Expect.TryGetValue(handler, out var byHandler))
                return byHandler;
        }
        if (stage.Expect.TryGetValue(":" + target.Label, out var byLabel))
            return byLabel;
        if (!target.HandlerNames.Any())
        {
            switch (target.Control)
            {
                case Button { Flyout: { } flyout }:
                    return That(() => flyout.IsOpen, "its menu open");
                case ToggleButton toggle when target.Handlers.Count == 0:
                    var was = toggle.IsChecked;
                    return That(() => toggle is RadioButton ? toggle.IsChecked == true : toggle.IsChecked != was, "it switch");
                case Expander expander:
                    var open = expander.IsExpanded;
                    return That(() => expander.IsExpanded != open, "it open or close");
                case { ContextMenu: { } context } when target.Kind == Kind.RightClick:
                    return That(() => context.IsOpen, "its menu open");
            }
        }
        report.Problems.Add($"Nobody says what '{target.Label}' ({string.Join(", ", target.Handlers.DefaultIfEmpty("no handler"))}) on {stage.Where} should do: add it to the table in ClickEverythingTests.");
        return null;
    }

    private static void Press(Stage stage, Pressable target)
    {
        switch (target.Kind)
        {
            case Kind.Click:
                Person.Click(target.Control);
                break;
            case Kind.RightClick:
                Person.RightClick(target.Control);
                break;
            case Kind.Type:
                Person.Type((TextBox)target.Control, stage.Typing.GetValueOrDefault(target.Control.Name ?? string.Empty, "test"));
                break;
            case Kind.Scroll:
                Person.Scroll(target.Control);
                break;
            case Kind.Expand:
                var header = target.Control.GetVisualDescendants().OfType<ToggleButton>().FirstOrDefault()
                    ?? throw new CannotPressException($"'{target.Label}' has no header to click.");
                Person.Click(header);
                break;
        }
    }

    /// <summary>Everything on a window a person could press right now (shown and not greyed out).</summary>
    private static List<Pressable> Pressables(Window window)
    {
        var found = new List<Pressable>();
        foreach (var control in Screen.All<Control>(window))
        {
            if (control.TemplatedParent is not null || !control.IsAttachedToVisualTree() || !control.IsEffectivelyVisible || !control.IsEffectivelyEnabled)
                continue; // parts of a built-in control (scroll bars, spinners) are not the app's buttons; nor is what is not on screen
            if (WhatAPressDoes(control) is not var (k, handlers))
                continue;
            var label = control is ListBoxItem || k == Kind.RightClick ? "a row" : Screen.Describe(control);
            found.Add(new Pressable($"{Area(control)}/{k} '{label}'", label, control, k, handlers));
        }
        return found;
    }

    /// <summary>How a person presses a control and which app handlers that runs; null when it is not something to press.</summary>
    private static (Kind, IReadOnlyList<string>)? WhatAPressDoes(Control control)
    {
        switch (control)
        {
            case ToggleButton toggle:
                return (Kind.Click, Screen.Handlers(toggle, Button.ClickEvent).Concat(Screen.Handlers(toggle, ToggleButton.IsCheckedChangedEvent)).ToList());
            case Button button:
                var opens = button.Flyout is null ? [] : Screen.EventHandlers(button.Flyout, "Opened");
                return (Kind.Click, Screen.Handlers(button, Button.ClickEvent).Concat(opens).ToList());
            case MenuItem item:
                return (Kind.Click, Screen.Handlers(item, MenuItem.ClickEvent));
            case Expander:
                return (Kind.Expand, []);
            case ListBoxItem row when row.FindAncestorOfType<ListBox>() is { } list && Screen.Handlers(list, SelectingItemsControl.SelectionChangedEvent).Count > 0:
                return (Kind.Click, Screen.Handlers(list, SelectingItemsControl.SelectionChangedEvent));
            case TextBox box when Screen.Handlers(box, TextBox.TextChangedEvent).Any(IsAxamlHandler):
                return (Kind.Type, Screen.Handlers(box, TextBox.TextChangedEvent));
            case ScrollViewer scroller when Screen.Handlers(scroller, InputElement.PointerWheelChangedEvent).Any(IsAxamlHandler):
                return (Kind.Scroll, Screen.Handlers(scroller, InputElement.PointerWheelChangedEvent));
            case { ContextMenu.IsVisible: true }:
                return (Kind.RightClick, []);
            default:
                return null;
        }
    }

    private static bool IsAxamlHandler(string handler) => handler[(handler.LastIndexOf('.') + 1)..].StartsWith("On", StringComparison.Ordinal);

    /// <summary>The nearest named part of the window around a control ("OverviewPage", "RecentList"), to tell same-named controls apart.</summary>
    private static string Area(Control control) =>
        control.GetVisualAncestors().OfType<Control>().FirstOrDefault(c => !string.IsNullOrEmpty(c.Name) && c.TemplatedParent is null)?.Name
        ?? (control.FindAncestorOfType<Avalonia.Controls.Primitives.OverlayLayer>() is not null ? "menu" : "window");

    /// <summary>A shown button or menu item with no handler, command or menu behind it.</summary>
    private static void DeadButtons(Stage stage, ClickReport report)
    {
        foreach (var control in Screen.All<Control>(stage.Window).Where(c => c is Button or MenuItem && c.TemplatedParent is null && c.IsVisible))
        {
            if (control is not ToggleButton && !Screen.DoesSomething(control))
                report.Problems.Add($"'{Screen.Describe(control)}' on {stage.Where} is a dead button: nothing happens when it is pressed.");
        }
    }

    // ------------------------------------------------------------------ the AXAML files

    /// <summary>Every "On…" handler the Mac/Linux app's AXAML files name, with the view it belongs to.</summary>
    private static IEnumerable<(string View, string Handler)> AxamlHandlers() =>
        XamlText.Views(RepoPaths.Of("src", "Pairnets.Desktop", "Views"), ".axaml")
            .SelectMany(v => v.Value.Select(h => (v.Key, h)))
            .OrderBy(h => h.Key, StringComparer.Ordinal);
}
