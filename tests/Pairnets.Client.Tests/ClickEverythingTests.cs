using System.Text.RegularExpressions;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using Microsoft.Extensions.DependencyInjection;
using Pairnets.Client.Themes;
using Pairnets.Client.Ui;
using Pairnets.Core;
using Pairnets.Core.Client;
using Pairnets.Core.Settings;
using Pairnets.Core.Sync;
using Pairnets.Server.Storage;
using Pairnets.Tests.Infrastructure;
using Xunit.Sdk;

namespace Pairnets.Client.Tests;

/// <summary>A test nest and release feed for the click-everything tests, and each window's results (run once).</summary>
public sealed class ClickFixture : IAsyncLifetime
{
    public const string OwnerEmail = "owner@example.com";

    private readonly Dictionary<string, Task<IReadOnlyList<string>>> _runs = [];

    public TestServer Nest { get; private set; } = null!;

    public FakeReleaseFeed Feed { get; private set; } = null!;

    /// <summary>A computer key the nest knows ("DESKTOP").</summary>
    public DeviceKeyGrant Grant { get; private set; } = null!;

    /// <summary>What the sign-in window finds at the nest's address: a nest with Google and email sign-in.</summary>
    public NestCheck Found { get; private set; } = null!;

    /// <summary>The tray menu items, by the text they start with (filled by the tray menu run).</summary>
    public IReadOnlyList<string> TrayMenuItems { get; set; } = [];

    public async Task InitializeAsync()
    {
        Nest = await TestServer.StartWithWebsiteAsync(new()
        {
            ["Sync:SmtpHost"] = "smtp.example.test",
            ["Sync:SmtpFrom"] = "Pairnets nest <nest@example.test>",
            ["Sync:GoogleClientId"] = "client-123.apps.googleusercontent.com",
            ["Sync:GoogleClientSecret"] = "test-oauth-secret",
        });
        var auth = Nest.Services.GetRequiredService<AuthStore>();
        auth.SetOwnerEmail(OwnerEmail);
        auth.SetGoogleAccount("e2e-google-user", OwnerEmail);
        Grant = Nest.MintKey("DESKTOP");
        Found = await Pairnets.Core.Client.Nest.CheckAsync(Nest.Url.ToString());
        Feed = await FakeReleaseFeed.StartAsync();
    }

    /// <summary>Runs <paramref name="run"/> the first time <paramref name="name"/> is asked for; later calls get the same result.</summary>
    public Task<IReadOnlyList<string>> Run(string name, Func<Task<IReadOnlyList<string>>> run)
    {
        lock (_runs)
        {
            if (!_runs.TryGetValue(name, out var task))
                _runs[name] = task = Task.Run(run);
            return task;
        }
    }

    /// <summary>Turns away every sign-in still waiting on the nest (it lets only three wait per address).</summary>
    public void DenyPendingSignIns()
    {
        var auth = Nest.Services.GetRequiredService<AuthStore>();
        foreach (var request in auth.ListPendingPairRequests())
            auth.DecidePairRequest(request.Id, approve: false, "test");
    }

    public async Task DisposeAsync()
    {
        await Feed.DisposeAsync();
        await Nest.DisposeAsync();
    }
}

/// <summary>What one press can see: a fresh window in its state, and everything it asked the app or Windows for.</summary>
internal sealed class Bench : IDisposable
{
    private readonly TempDir _dir = new("wpf-press");

    public Bench(ClickFixture fixture, bool brokenHistory)
    {
        Fixture = fixture;
        History = new SampleHistory(brokenHistory);
        Actions = new RecordingActions(History);
        Script.Folder = Directory.CreateDirectory(_dir.Combine("Chosen")).FullName;
    }

    public ClickFixture Fixture { get; }

    public RecordingActions Actions { get; }

    public SampleHistory History { get; }

    public DialogScript Script { get; } = new();

    public FakeSecrets Secrets { get; } = new();

    /// <summary>Links and files the window asked to open.</summary>
    public Log<string> Opened { get; } = new();

    /// <summary>Events a view raised for its window to handle ("Saved", "Cancelled", …).</summary>
    public Log<string> Events { get; } = new();

    /// <summary>A folder of this press's own (it exists).</summary>
    public string Dir => _dir.Path;

    public bool Closed { get; set; }

    public UpdateService? Updates { get; set; }

    /// <summary>Forgets what happened while the window opened, so only the press itself counts.</summary>
    public void ResetLogs()
    {
        Actions.Calls.Clear();
        History.Reads.Clear();
        History.Restored.Clear();
        Script.Asked.Clear();
        Script.Copied.Clear();
        Script.FolderPickers.Clear();
        Opened.Clear();
        Events.Clear();
    }

    public void Dispose()
    {
        Updates?.Dispose();
        _dir.Dispose();
    }
}

/// <summary>One window in one state. <see cref="Ready"/> runs once it shows (data that arrives later, pages).</summary>
internal sealed record Screen(string Window, string State, Func<Bench, Window> Build, Func<Bench, Window, Task>? Ready = null,
    bool Modal = false, bool BrokenHistory = false, Action<DialogScript>? Answers = null)
{
    public override string ToString() => $"{Window} ({State})";
}

/// <summary>What the press of a control should do, and how to see that it did (checked on the WPF thread).</summary>
internal sealed record Rule(Func<Target, bool> Matches, string Should, Func<Check, bool> Done, int Seconds = 5);

/// <param name="Item">
/// The row the pressed control belonged to, read before the press: a closed menu can lose it while the check waits.
/// </param>
internal sealed record Check(Bench Bench, Window Window, Target Target, bool? WasChecked, object? Item);

/// <summary>
/// Presses every button, switch, radio button, menu item and clickable card in every window of the Windows app, in
/// every state that shows different ones, and checks each press against what it should do. A press that crashes or
/// does nothing fails in plain English, and so does a handler in the XAML that no test ever presses.
/// </summary>
public sealed class ClickEverythingTests(ClickFixture fixture) : IClassFixture<ClickFixture>
{
    private static WpfThread Wpf => WpfThread.Instance;

    private static readonly DateTimeOffset Now = DateTimeOffset.Now;
    private static readonly DateTimeOffset Utc = DateTimeOffset.UtcNow;
    private const string FolderPath = @"D:\Sync\Work";
    private static readonly ServerInfo OlderServer = new("id", 1, 1, "0.9.0", 412L << 30, 1L << 40);
    private static readonly ServerInfo CurrentServer = new("id", 1, 1, PairnetsInfo.ProductVersion, 412L << 30, 1L << 40);

    private static readonly IReadOnlyList<DeviceInfo> Devices =
    [
        new("DESKTOP", Utc.AddDays(-30), Utc, true, "1.0.58", "Windows"),
        new("LAPTOP", Utc.AddDays(-30), Utc.AddHours(-3), false, "1.0.58", "Windows"),
    ];

    /// <summary>Recent activity: files that the sample History also has, and a message without a file.</summary>
    private static IReadOnlyList<ActivityItem> Rows()
    {
        var feed = new ActivityFeed();
        feed.Add(new ActivityItem(Now.AddDays(-1), ActivityKind.Uploaded, "Projects/report.docx", "Uploaded"));
        feed.Add(new ActivityItem(Now.AddHours(-3), ActivityKind.Downloaded, "Notes/ideas.md", "Downloaded"));
        feed.Add(new ActivityItem(Now.AddMinutes(-12), ActivityKind.DeletedOnServer, "Projects/2026/budget-draft.xlsx", "Deleted"));
        feed.Add(new ActivityItem(Now.AddMinutes(-5), ActivityKind.Info, null, "Server updated to 1.0.58"));
        return feed.Items;
    }

    private const string ConflictTitle = "Conflict copy: report (conflict LAPTOP).docx";
    private const string JoinTitle = "LAPTOP-2 (Windows) wants to join";

    private static IReadOnlyList<AttentionItem> Attention(Bench b) =>
    [
        new(ConflictTitle, "Both computers changed this file.", "Show in folder", () => b.Actions.Calls.Add("attention:" + ConflictTitle)),
        new(JoinTitle, "Code KQ7M-4PXD.", "Review in browser", () => b.Actions.Calls.Add("attention:" + JoinTitle)),
    ];

    // ------------------------------------------------------------------ the tests

    [Fact]
    public Task TrayPanel_EveryButtonWorks() => Expect(fixture.Run("tray panel", () => PressEverything(TrayPanelScreens(), TrayPanelRules)));

    [Fact]
    public Task MainWindow_EveryButtonWorks() => Expect(fixture.Run("main window", () => PressEverything(MainWindowScreens(), MainWindowRules)));

    [Fact]
    public Task Settings_EveryButtonWorks() => Expect(fixture.Run("settings", () => PressEverything(SettingsScreens(), SettingsRules)));

    [Fact]
    public Task SignIn_EveryButtonWorks() => Expect(fixture.Run("sign-in", () => PressEverything(SignInScreens(), SignInRules)));

    [Fact]
    public Task ServerUpdateWindow_EveryButtonWorks() => Expect(fixture.Run("server update", () => PressEverything(ServerUpdateScreens(), ServerUpdateRules)));

    [Fact]
    public Task BugReportWindow_EveryButtonWorks() => Expect(fixture.Run("bug report", () => PressEverything(BugReportScreens(), BugReportRules)));

    [Fact]
    public Task TrayMenu_EveryItemWorks() => Expect(fixture.Run("tray menu", PressTheTrayMenu));

    /// <summary>The guard: nothing in the XAML, and nothing in the tray menu, goes unpressed.</summary>
    [Fact]
    public async Task EveryHandlerInTheXamlAndEveryTrayItemIsPressed()
    {
        // Run every window first (each runs once, whichever test asks first).
        foreach (var run in new Func<Task>[] { TrayPanel_EveryButtonWorks, MainWindow_EveryButtonWorks, Settings_EveryButtonWorks, SignIn_EveryButtonWorks,
                     ServerUpdateWindow_EveryButtonWorks, BugReportWindow_EveryButtonWorks, TrayMenu_EveryItemWorks })
        {
            try
            {
                await run();
            }
            catch (Exception)
            {
                // That window's own test reports it; here only what was never pressed counts.
            }
        }
        var missing = new List<string>();
        foreach (var handler in XamlHandlers.All())
        {
            if (!PressCoverage.Contains(handler.Key))
                missing.Add($"The button {handler.Label} ({handler.Event}=\"{handler.Handler}\") in {handler.File} is never pressed by a test.");
        }
        if (fixture.TrayMenuItems.Count == 0)
            missing.Add("The tray menu is never opened by a test.");
        foreach (var item in fixture.TrayMenuItems)
        {
            if (!PressCoverage.Contains("tray menu: " + item))
                missing.Add($"The tray menu item '{item}' is never pressed by a test.");
        }
        foreach (var click in new[] { "click", "double-click" })
        {
            if (!PressCoverage.Contains("tray icon: " + click))
                missing.Add($"A {click} on the tray icon is never tested.");
        }
        Assert.True(missing.Count == 0, string.Join(Environment.NewLine, missing));
    }

    private static async Task Expect(Task<IReadOnlyList<string>> run)
    {
        var failures = await run;
        if (failures.Count > 0)
            throw new XunitException($"{failures.Count} press(es) went wrong:{Environment.NewLine}" + string.Join(Environment.NewLine, failures.Select(f => "• " + f)));
    }

    // ------------------------------------------------------------------ pressing everything

    /// <summary>
    /// Opens each screen to see what it shows, then presses each control in a freshly opened copy of it. A control
    /// that an earlier state of the same window already had (same text, same handler, on or off alike, such as the
    /// header's "Sync now" on every page) is pressed there only.
    /// </summary>
    private async Task<IReadOnlyList<string>> PressEverything(IEnumerable<Screen> screens, IReadOnlyList<Rule> rules)
    {
        var failures = new List<string>();
        var pressed = new HashSet<string>();
        foreach (var screen in screens)
        {
            List<(string Label, string Key)> labels;
            try
            {
                // A radio button or switch that is already on does something else when pressed than one that is off.
                labels = await Open(screen, (_, window) => Wpf.Ui(() => Presser.Find(window)
                    .Select(t => (t.Label, $"{t.Label}|{t.Kind}|{string.Join(",", t.Handlers)}|{(t.Element as ToggleButton)?.IsChecked}")).ToList()));
            }
            catch (Exception ex)
            {
                failures.Add($"Opening {screen} failed: {ex.Message}");
                continue;
            }
            if (labels.Count == 0)
                failures.Add($"{screen} shows nothing to press.");
            var seenBefore = labels.Select(l => l.Key).Where(pressed.Contains).ToHashSet();
            for (var i = 0; i < labels.Count; i++)
            {
                if (!pressed.Add(labels[i].Key) && seenBefore.Contains(labels[i].Key))
                    continue;
                var index = i;
                try
                {
                    if (await Open(screen, (bench, window) => PressOne(screen, rules, bench, window, index, labels[index].Label)) is { } failure)
                        failures.Add(failure);
                }
                catch (Exception ex)
                {
                    failures.Add($"Pressing '{labels[i].Label}' on {screen} went wrong: {ex.Message}");
                }
            }
        }
        return failures;
    }

    /// <summary>Opens the screen fresh, runs <paramref name="body"/> with it, and closes everything again.</summary>
    private async Task<T> Open<T>(Screen screen, Func<Bench, Window, Task<T>> body)
    {
        using var bench = new Bench(fixture, screen.BrokenHistory);
        screen.Answers?.Invoke(bench.Script);
        using var dialogs = bench.Script.Install();
        Wpf.TakeCrashes();
        var window = await Wpf.Ui(() =>
        {
            var w = screen.Build(bench);
            w.Closed += (_, _) => bench.Closed = true;
            return w;
        });
        var modal = screen.Modal ? Wpf.Post(() => window.ShowDialog()) : null;
        try
        {
            if (modal is null)
                await Wpf.Ui(window.Show);
            await Wpf.WaitUntil(() => window.IsLoaded, $"{screen} to open", TimeSpan.FromSeconds(15));
            if (screen.Ready is not null)
                await screen.Ready(bench, window);
            await Wpf.Settle();
            PressCoverage.Add(await Wpf.Ui(() => Presser.LoadedHandlers(window).ToList()));
            if (Wpf.TakeCrashes() is { Count: > 0 } crashes)
                throw new XunitException($"Opening {screen} crashed: {crashes[0]}");
            return await body(bench, window);
        }
        finally
        {
            await Wpf.CloseAllWindows();
            if (modal is not null)
                await modal.WaitAsync(TimeSpan.FromSeconds(15));
            fixture.DenyPendingSignIns();
        }
    }

    private static async Task<string?> PressOne(Screen screen, IReadOnlyList<Rule> rules, Bench bench, Window window, int index, string label)
    {
        var targets = await Wpf.Ui(() => Presser.Find(window));
        if (index >= targets.Count || targets[index].Label != label)
            return $"{screen} showed other controls the second time it opened (control {index + 1} should be '{label}').";
        var target = targets[index];
        var rule = await Wpf.Ui(() => rules.FirstOrDefault(r => r.Matches(target)));
        if (rule is null)
            return $"Nothing says what pressing '{label}' on {screen} should do. Add it to the expectations in {nameof(ClickEverythingTests)}.";
        bench.ResetLogs();
        Wpf.TakeCrashes();
        var wasChecked = await Wpf.Ui(() => (target.Element as ToggleButton)?.IsChecked ?? (target.Element as Expander)?.IsExpanded);
        var item = await Wpf.Ui(() => (target.MenuOwner ?? target.Element).DataContext);
        try
        {
            var ran = await Wpf.Ui(() => Presser.Press(target));
            PressCoverage.Add(target.Handlers);
            PressCoverage.Add(ran);
        }
        catch (Exception ex)
        {
            return $"Pressing '{label}' on {screen} crashed: {ex.GetBaseException()}";
        }
        var check = new Check(bench, window, target, wasChecked, item);
        var deadline = DateTime.UtcNow.AddSeconds(rule.Seconds);
        while (true)
        {
            await Wpf.Settle();
            if (Wpf.TakeCrashes() is { Count: > 0 } crashes)
                return $"Pressing '{label}' on {screen} crashed: {crashes[0]}";
            if (await Wpf.Ui(() => rule.Done(check)))
                break;
            if (DateTime.UtcNow > deadline)
                return $"Pressing '{label}' on {screen} did nothing: it should {rule.Should}.";
            await Task.Delay(50);
        }
        PressCoverage.Add(await Wpf.Ui(() => Presser.CloseMenus(window)));
        return null;
    }

    // ------------------------------------------------------------------ rules shared by the windows

    private static Rule On(string label, string should, Func<Check, bool> done, int seconds = 5) => new(t => t.Label == label, should, done, seconds);

    private static Rule On(string[] labels, string should, Func<Check, bool> done, int seconds = 5) => new(t => labels.Contains(t.Label), should, done, seconds);

    private static Func<Check, bool> Called(string call) => c => c.Bench.Actions.Calls.Items.Contains(call);

    /// <summary>A switch (check box): pressing turns it the other way.</summary>
    private static readonly Rule Switch = new(t => t.Element is CheckBox, "turn the switch the other way",
        c => ((CheckBox)c.Target.Element).IsChecked != c.WasChecked);

    private static readonly Rule Closes = On(["Close", "Later"], "close the window", c => c.Bench.Closed);

    // ------------------------------------------------------------------ the tray panel

    private static IEnumerable<Screen> TrayPanelScreens()
    {
        const string name = "the tray panel";
        yield return new Screen(name, "idle, with recent files", b => new TrayPanel(b.Actions), (_, w) => Wpf.Ui(() =>
        {
            var panel = (TrayPanel)w;
            panel.ShowStatus(StatusSnapshot.Initial with
            {
                Status = RunnerStatus.Idle, Text = "Up to date", LastSyncAt = Now, Server = CurrentServer, Devices = Devices,
                HeardFrom = new Dictionary<string, DateTimeOffset> { ["LAPTOP"] = Utc },
            }, "DESKTOP");
            panel.ShowActivity(Rows());
            panel.ShowAttention(0);
        }));
        yield return new Screen(name, "deletions blocked, paused, two things to look at", b => new TrayPanel(b.Actions), (_, w) => Wpf.Ui(() =>
        {
            var panel = (TrayPanel)w;
            panel.ShowStatus(StatusSnapshot.Initial with
            {
                Status = RunnerStatus.Blocked, BlockReason = BlockReason.MassDelete, PendingDeletes = 37, Paused = true,
                Text = "This sync would delete 37 files on the server. Nothing was deleted.", Server = CurrentServer, Devices = Devices,
            }, "DESKTOP");
            panel.ShowActivity(Rows());
            panel.ShowAttention(2);
        }));
    }

    private static Func<Check, bool> HidesAnd(string call) => c => !c.Window.IsVisible && c.Bench.Actions.Calls.Items.Contains(call);

    private static readonly IReadOnlyList<Rule> TrayPanelRules =
    [
        On("More", "open the menu with Sync now, Settings, History, the log, bug reports and Exit", c => ((TrayPanel)c.Window).MoreButton.ContextMenu!.IsOpen),
        On("Sync now", "ask the app to sync now", Called("SyncNow")),
        On("Settings…", "hide the panel and open Settings in the main window", HidesAnd("OpenWindow(Settings)")),
        On("History…", "hide the panel and open History in the main window", HidesAnd("OpenWindow(History)")),
        On("View log", "hide the panel and open the log", HidesAnd("ViewLog")),
        On("Report a bug…", "hide the panel and open a bug report", HidesAnd("ReportBug")),
        On("Exit Pairnets", "hide the panel and quit Pairnets", HidesAnd("Quit")),
        new(t => t.Label.StartsWith("Allow these deletions", StringComparison.Ordinal), "hide the panel and ask about the blocked deletions", HidesAnd("FixBlocked")),
        On("Open Pairnets", "hide the panel and open the main window", HidesAnd("OpenWindow(Overview)")),
        On("Folder", "hide the panel and open the synced folder", HidesAnd("OpenFolder")),
        On(["Pause", "Resume"], "ask the app to pause or resume syncing", Called("TogglePause")),
        On("AttentionButton", "hide the panel and open \"Needs attention\" in the main window", HidesAnd("OpenWindow(Attention)")),
    ];

    // ------------------------------------------------------------------ the main window

    private static MainWindow NewMainWindow(Bench b) => new(b.Actions) { Width = 1100, Height = 760 };

    private static IEnumerable<Screen> MainWindowScreens()
    {
        const string name = "the main window";
        yield return new Screen(name, "syncing, with an app update, an older server and two things to look at", NewMainWindow, (b, w) => Wpf.Ui(() =>
        {
            var main = (MainWindow)w;
            main.ShowActivity(Rows());
            main.ShowAttention(Attention(b));
            main.ShowUpdate("Pairnets 1.0.58 is available", "You have 1.0.52.", "Update now");
            main.ShowStatus(StatusSnapshot.Initial with
            {
                Status = RunnerStatus.Syncing, Text = "Syncing", LastSyncAt = Now.AddMinutes(-1), Server = OlderServer, Devices = Devices,
                CurrentPath = "Videos/talk.mp4", Operation = "upload", BytesDone = 6 << 20, BytesTotal = 10 << 20, FilesDone = 3, FilesTotal = 12,
                PassBytesDone = 40L << 20, PassBytesTotal = 120L << 20, BytesPerSecond = 4 << 20,
                Active = [new ActiveTransfer("Videos/talk.mp4", "upload", 60, 100), new ActiveTransfer("Photos/a.jpg", "upload", 20, 100)],
            }, FolderPath, "DESKTOP");
        }));
        yield return new Screen(name, "deletions blocked and paused, with an update to download", NewMainWindow, (b, w) => Wpf.Ui(() =>
        {
            var main = (MainWindow)w;
            main.ShowActivity(Rows());
            main.ShowUpdate("Pairnets 1.0.58 is available", "You have 1.0.52.", "Download");
            main.ShowStatus(StatusSnapshot.Initial with
            {
                Status = RunnerStatus.Blocked, BlockReason = BlockReason.MassDelete, PendingDeletes = 37, Paused = true, LastSyncAt = Now.AddMinutes(-3),
                Text = "This sync would delete 37 files on the server. Nothing was deleted.", Server = CurrentServer, Devices = Devices,
            }, FolderPath, "DESKTOP");
        }));
        yield return new Screen(name, "waiting for the other computer's big batch", NewMainWindow, (b, w) => Wpf.Ui(() =>
            ((MainWindow)w).ShowStatus(StatusSnapshot.Initial with
            {
                Status = RunnerStatus.Idle, Text = "Up to date", LastSyncAt = Now, Server = CurrentServer, Devices = Devices,
                WaitingFor = new PeerWait("LAPTOP", 340, 212),
            }, FolderPath, "DESKTOP")));
        yield return new Screen(name, "the Activity page", NewMainWindow, (b, w) => Wpf.Ui(() =>
        {
            var main = (MainWindow)w;
            main.ShowActivity(Rows());
            main.Navigate(MainPage.Activity);
        }));
        yield return new Screen(name, "the History page, a deleted file chosen", NewMainWindow, async (b, w) =>
        {
            var main = (MainWindow)w;
            await Wpf.Ui(() => main.Navigate(MainPage.History));
            await Wpf.WaitUntil(() => main.FileList.Items.Count > 0, "the History list");
            await Wpf.Ui(() => main.SelectHistoryRow(0));
            await Wpf.WaitUntil(() => main.VersionList.Items.Count == 2, "the versions of the chosen file");
        });
        yield return new Screen(name, "the History page, all files", NewMainWindow, async (b, w) =>
        {
            var main = (MainWindow)w;
            await Wpf.Ui(() => main.Navigate(MainPage.History));
            await Wpf.WaitUntil(() => main.FileList.Items.Count > 0, "the History list");
            await Wpf.Ui(() => main.FilesTab.IsChecked = true);
        });
        yield return new Screen(name, "the History page when the server cannot be read", NewMainWindow, async (b, w) =>
        {
            var main = (MainWindow)w;
            await Wpf.Ui(() => main.Navigate(MainPage.History));
            await Wpf.WaitUntil(() => main.HistoryRetry.IsVisible, "the History page's Try again");
        }, BrokenHistory: true);
        yield return new Screen(name, "the Devices page", NewMainWindow, (b, w) => Wpf.Ui(() =>
        {
            var main = (MainWindow)w;
            main.ShowStatus(StatusSnapshot.Initial with { Status = RunnerStatus.Idle, Text = "Up to date", Server = CurrentServer, Devices = Devices },
                FolderPath, "DESKTOP");
            main.Navigate(MainPage.Devices);
        }));
        yield return new Screen(name, "the Needs attention page", NewMainWindow, (b, w) => Wpf.Ui(() =>
        {
            var main = (MainWindow)w;
            main.ShowAttention(Attention(b));
            main.Navigate(MainPage.Attention);
        }));
    }

    private static Func<Check, bool> OnPage(MainPage page) => c => ((MainWindow)c.Window).Page == page;

    private static bool ListShows(MainWindow main, Func<ServerFile, bool> match) => main.FileList.Items.Cast<ServerFile>().All(match);

    private static readonly IReadOnlyList<Rule> MainWindowRules =
    [
        // Rows of lists: what each row's own button does.
        new(t => t.Element is Button { CommandParameter: AttentionItem }, "run that item's action",
            c => c.Bench.Actions.Calls.Items.Contains("attention:" + ((AttentionItem)((Button)c.Target.Element).CommandParameter).Title)),
        new(t => t.Element is Button { CommandParameter: VersionRow }, "restore that version and say so",
            c => c.Bench.History.Restored.Items.Contains((((VersionRow)((Button)c.Target.Element).CommandParameter).Path, ((VersionRow)((Button)c.Target.Element).CommandParameter).Version.Id))
                && ((MainWindow)c.Window).RestoreMessage.IsVisible),
        new(t => t.Kind == PressKind.MenuItem && t.Label == "Show in folder", "show that file in Explorer",
            c => c.Bench.Actions.Calls.Items.Contains($"RevealFile({((ActivityItem)c.Item!).Path})")),
        new(t => t.Kind == PressKind.MenuItem && t.Label == "Show versions…", "open History on that file",
            c => ((MainWindow)c.Window).Page == MainPage.History
                && (((MainWindow)c.Window).FileList.SelectedItem as ServerFile)?.Path == ((ActivityItem)c.Item!).Path),

        // The sidebar.
        On("Overview", "show the Overview page", OnPage(MainPage.Overview)),
        On("Activity", "show the Activity page", OnPage(MainPage.Activity)),
        On("History", "show the History page and read the server's files",
            c => OnPage(MainPage.History)(c) && (c.WasChecked == true || c.Bench.History.Reads.Items.Contains("files"))),
        On("Devices", "show the Devices page and ask the server for its computers",
            c => OnPage(MainPage.Devices)(c) && (c.WasChecked == true || Called("RefreshDevices")(c))),
        On("Needs attention", "show the Needs attention page", OnPage(MainPage.Attention)),
        On("Settings", "ask the app for the Settings page", Called("ShowSettings")),
        On(["Update now", "Download"], "start the app update", Called("UpdateNow")),
        On("Later", "put the app update away", Called("DismissUpdate")),

        // The header and the overview.
        On(["Open folder", "Open"], "open the synced folder", Called("OpenFolder")),
        On(["Pause", "Resume"], "ask the app to pause or resume syncing", Called("TogglePause")),
        On("Sync now", "ask the app to sync now", Called("SyncNow")),
        On("More", "open the menu with the log and bug reports", c => ((MainWindow)c.Window).MoreButton.ContextMenu!.IsOpen),
        On("View log", "open the log", Called("ViewLog")),
        On("Report a bug…", "open a bug report", Called("ReportBug")),
        new(t => t.Label.StartsWith("Allow these deletions", StringComparison.Ordinal), "ask about the blocked deletions", Called("FixBlocked")),
        On("Download now anyway", "stop waiting for the other computer", Called("DownloadNow")),
        On("Review", "show the Needs attention page", OnPage(MainPage.Attention)),
        On(["Update server…", "Check for update"], "open the server update window", Called("UpdateServer")),
        On("See all", "show the Activity page", OnPage(MainPage.Activity)),

        // History.
        On("Deleted files", "list the deleted files", c => ((MainWindow)c.Window).DeletedTab.IsChecked == true && ListShows((MainWindow)c.Window, f => f.Deleted)),
        On("All files", "list the current files", c => ((MainWindow)c.Window).FilesTab.IsChecked == true && ListShows((MainWindow)c.Window, f => !f.Deleted)),
        On("HistorySearch", $"list only files matching \"{Presser.TypedText}\"",
            c => ((MainWindow)c.Window).SearchHint.Visibility != Visibility.Visible && ListShows((MainWindow)c.Window, f => f.Path.Contains(Presser.TypedText, StringComparison.OrdinalIgnoreCase))),
        On(["Reload from the server", "Try again"], "read the server's files again", c => c.Bench.History.Reads.Items.Contains("files")),
        On("FileList", "show the chosen file and its versions",
            c => ((MainWindow)c.Window) is { DetailPanel.IsVisible: true } main && main.FileList.SelectedItem is ServerFile f && main.DetailName.Text == f.Name),

        // Devices.
        On("+ Add a computer…", "explain how to add a computer", Called("AddComputer")),
        On("Manage devices on your nest", "open the nest's Devices page", Called("ManageDevices")),
    ];

    // ------------------------------------------------------------------ Settings

    private Window SettingsHost(Bench b, bool ownKey)
    {
        b.Updates = new UpdateService(new UpdateChecker(new HttpClient(), fixture.Feed.DownloadBase, fixture.Feed.ReleasePage),
            PairnetsInfo.ProductVersion, UpdateChecker.AssetForThisPlatform());
        var settings = new ClientSettings
        {
            ServerUrl = fixture.Nest.Url.ToString(),
            ProtectedToken = b.Secrets.Protect(ownKey ? fixture.Grant.Key : fixture.Nest.Token),
            DeviceId = ownKey ? fixture.Grant.Id : null,
            DeviceName = "DESKTOP",
            Folder = b.Dir,
            FirstRunCompleted = true,
        };
        var view = new SettingsView(settings, b.Secrets, b.Updates, "Server " + PairnetsInfo.ProductVersion);
        view.Saved += _ => b.Events.Add("Saved");
        view.Cancelled += () => b.Events.Add("Cancelled");
        view.SignOutRequested += () => b.Events.Add("SignOutRequested");
        view.ResetRequested += () => b.Events.Add("ResetRequested");
        view.ManageDevicesRequested += () => b.Events.Add("ManageDevicesRequested");
        var host = new Window { Title = "Settings", Width = 700, Height = 560, Content = view };
        ThemeManager.Attach(host);
        return host;
    }

    private IEnumerable<Screen> SettingsScreens()
    {
        // "Save anyway" when the connection test does not like the shared token.
        yield return new Screen("Settings", "signed in with this computer's own key", b => SettingsHost(b, ownKey: true), Answers: s => s.AgreeToEverything());
        yield return new Screen("Settings", "on the shared token (address and token shown)", b => SettingsHost(b, ownKey: false), Answers: s => s.AgreeToEverything());
    }

    private static SettingsView View(Check c) => (SettingsView)c.Window.Content;

    private static readonly IReadOnlyList<Rule> SettingsRules =
    [
        Switch,
        On("Manage devices on the web", "ask the app to open the nest's Devices page", c => c.Bench.Events.Items.Contains("ManageDevicesRequested")),
        On("Sign out of this computer", "ask the app to sign this computer out", c => c.Bench.Events.Items.Contains("SignOutRequested")),
        On("Advanced: server address and token ▸", "show the server address and token", c => View(c).AddressFields.IsVisible),
        On("Test connection", "test the connection and show the result",
            c => View(c).TestResult.Text is { Length: > 0 } text && !text.StartsWith("Testing", StringComparison.Ordinal), seconds: 20),
        On("Browse…", "let you choose the folder and show it", c => View(c).FolderBox.Text == c.Bench.Script.Folder),
        new(t => t.Element is Expander, "open the files-to-ignore section", c => ((Expander)c.Target.Element).IsExpanded != c.WasChecked),
        On("Check now", "look for an app update and say what it found", c => View(c).VersionText.Text.Contains("is available", StringComparison.Ordinal), seconds: 20),
        new(t => t.Element is RadioButton { GroupName: "par" }, "choose that many files at the same time", c => ((RadioButton)c.Target.Element).IsChecked == true),
        On("Reset this app…", "ask the app to reset everything", c => c.Bench.Events.Items.Contains("ResetRequested")),
        On("Cancel", "leave Settings without saving", c => c.Bench.Events.Items.Contains("Cancelled")),
        On("Save", "check the settings and save them", c => c.Bench.Events.Items.Contains("Saved") && View(c).Result is not null, seconds: 20),
        On("Scroller", "scroll the page", c => View(c).Scroller.VerticalOffset > 0),
    ];

    // ------------------------------------------------------------------ signing in

    private static SignInView SignIn(Window w) => ((SettingsWindow)w).SignIn;

    private SettingsWindow SignInWindow(Bench b) => new(new ClientSettings { DeviceName = "DESKTOP" }, b.Secrets, b.Opened.Add);

    /// <summary>The nest as found, but offering no Google or email sign-in: only the browser button.</summary>
    private NestCheck BrowserOnly => fixture.Found with { Hello = fixture.Found.Hello! with { Methods = new SignInMethods(true, false, false, false) } };

    private string Address => fixture.Nest.Url.ToString();

    private const string SampleLink = "https://nest.example.com/link?code=KQ7M-4PXD";

    /// <summary>Starts a real sign-in with the browser button and waits until the code shows.</summary>
    private static async Task<string> StartSigningIn(Bench b, Window w)
    {
        var browser = await Wpf.Ui(() => Presser.Find(w).Single(t => t.Label == "Sign in with your browser"));
        await Wpf.Ui(() => Presser.Press(browser));
        await Wpf.WaitUntil(() => b.Opened.Items.Any(l => l.Contains("/link?code=", StringComparison.Ordinal)), "the sign-in code", TimeSpan.FromSeconds(15));
        return b.Opened.Items.Last();
    }

    private IEnumerable<Screen> SignInScreens()
    {
        const string name = "the sign-in window";
        yield return new Screen(name, "a nest found, the browser the one way in", SignInWindow,
            (b, w) => Wpf.Ui(() => SignIn(w).ShowAddress(Address, BrowserOnly)), Modal: true);
        yield return new Screen(name, "a nest with Google and email sign-in, an email typed", SignInWindow, (b, w) => Wpf.Ui(() =>
        {
            SignIn(w).ShowAddress(Address, fixture.Found);
            SignIn(w).EmailBox.Text = ClickFixture.OwnerEmail;
        }), Modal: true);
        yield return new Screen(name, "waiting for approval", SignInWindow, (b, w) => Wpf.Ui(() =>
            SignIn(w).ShowPairing(new PairingState(PairingStage.Waiting, "KQ7M-4PXD", SampleLink, DateTimeOffset.UtcNow.AddMinutes(9)), openBrowser: false)),
            Modal: true);
        yield return new Screen(name, "turned down on the nest", SignInWindow, async (b, w) =>
        {
            await Wpf.Ui(() => SignIn(w).ShowAddress(Address, BrowserOnly));
            var link = await StartSigningIn(b, w);
            var auth = fixture.Nest.Services.GetRequiredService<AuthStore>();
            auth.DecidePairRequest(auth.FindPairRequestByCode(TrayHarness.CodeIn(link))!.Id, approve: false, "test");
            await Wpf.WaitUntil(() => SignIn(w).RetryButton.IsVisible, "the sign-in to be turned down", TimeSpan.FromSeconds(15));
        }, Modal: true);
        yield return new Screen(name, "the code expired", SignInWindow, async (b, w) =>
        {
            await Wpf.Ui(() => SignIn(w).ShowAddress(Address, BrowserOnly));
            var link = await StartSigningIn(b, w);
            await Wpf.Ui(() => SignIn(w).ShowPairing(new PairingState(PairingStage.Expired, TrayHarness.CodeIn(link), link,
                DateTimeOffset.UtcNow.AddMinutes(-1), "The code expired.")));
        }, Modal: true);
        yield return new Screen(name, "signed in, choosing the folder", SignInWindow, (b, w) => Wpf.Ui(() =>
        {
            SignIn(w).ShowFolderStep(fixture.Grant, fixture.Nest.Url);
            SignIn(w).Folder = b.Dir;
        }), Modal: true, Answers: s => s.AgreeToEverything());
    }

    private static Func<Check, bool> OpensTheNest(string? method) => c =>
        SignIn(c.Window).WaitStep.IsVisible && c.Bench.Opened.Items.Any(l => l.Contains("/link?code=", StringComparison.Ordinal)
            && (method is null ? !l.Contains("method=", StringComparison.Ordinal) : l.Contains(method, StringComparison.Ordinal)));

    private static readonly IReadOnlyList<Rule> SignInRules =
    [
        Switch,
        On(["Sign in with your browser", "More ways to sign in in your browser"], "ask the nest for a code and open it in the browser", OpensTheNest(null), seconds: 15),
        On("Continue with Google", "ask the nest for a code and open its Google sign-in", OpensTheNest("&method=google"), seconds: 15),
        On("Continue with email", "ask the nest for a code and open its email sign-in",
            OpensTheNest("&method=email&email=" + Uri.EscapeDataString(ClickFixture.OwnerEmail)), seconds: 15),
        On("Open the browser again", "open the nest's page again", c => c.Bench.Opened.Items.Contains(SampleLink)),
        On("Copy link", "copy the link and say so", c => c.Bench.Script.Copied.Items.Contains(SampleLink) && (string)SignIn(c.Window).CopyButton.Content == "Copied"),
        On(["Cancel", "Back"], "go back to the start", c => SignIn(c.Window) is { WelcomeStep.IsVisible: true, WaitStep.IsVisible: false }),
        On(["Start over", "Get a new code"], "ask the nest for a new code",
            c => SignIn(c.Window) is { WaitStep.IsVisible: true, RetryButton.IsVisible: false } && c.Bench.Opened.Items.Any(l => l.Contains("/link?code=", StringComparison.Ordinal)),
            seconds: 15),
        On("Browse…", "let you choose the folder and show it", c => SignIn(c.Window).FolderBox.Text == c.Bench.Script.Folder),
        On("Start syncing", "finish signing in and close the window", c => ((SettingsWindow)c.Window).Result is { DeviceId: not null } && c.Bench.Closed, seconds: 20),
    ];

    // ------------------------------------------------------------------ the server update window

    private static IEnumerable<Screen> ServerUpdateScreens()
    {
        const string name = "the server update window";
        yield return new Screen(name, "an older server, Debug mode on", _ => new ServerUpdateWindow(() => null, "0.9.0", "1.0.58", debug: () => true));
        yield return new Screen(name, "the server is up to date", _ => new ServerUpdateWindow(() => null, "1.0.58", "1.0.58"));
        yield return new Screen(name, "the server needs the one-time setup", _ => new ServerUpdateWindow(() => null, "0.9.0", "1.0.58"),
            (_, w) => Wpf.Ui(() => ((ServerUpdateWindow)w).ShowResult(new ServerUpdateResult(false, "This server can't update itself yet.", "0.9.0", CanUpdateItself: false))));
        yield return new Screen(name, "the update did not work", _ => new ServerUpdateWindow(() => null, "0.9.0", "1.0.58"),
            (_, w) => Wpf.Ui(() => ((ServerUpdateWindow)w).ShowResult(new ServerUpdateResult(false, "The server said no.", "0.9.0", CanUpdateItself: true))));
    }

    private static readonly IReadOnlyList<Rule> ServerUpdateRules =
    [
        Switch,
        Closes,
        On("Don't ask for this version", "remember to skip this version and close", c => ((ServerUpdateWindow)c.Window).Skipped && c.Bench.Closed),
        On(["Update server", "Check for update", "Try again"], "ask the server to update and show how it went",
            c => ((ServerUpdateWindow)c.Window) is { ResultPanel.IsVisible: true } u && u.Explanation.Text.Contains("Not connected", StringComparison.Ordinal)),
        On("Copy command", "copy the install command", c => c.Bench.Script.Copied.Items.Contains(((ServerUpdateWindow)c.Window).CommandText.Text)),
        On("Copy details", "copy the details", c => c.Bench.Script.Copied.Count == 1),
    ];

    // ------------------------------------------------------------------ the bug report window

    private static IEnumerable<Screen> BugReportScreens()
    {
        yield return new Screen("the bug report window", "the report is ready",
            b => new BugReportWindow(() => Task.FromResult("Pairnets bug report" + Environment.NewLine + "(sample)"), b.Opened.Add, logsDir: b.Dir),
            (_, w) => Wpf.WaitUntil(() => ((BugReportWindow)w).CopyButton.IsEnabled, "the report"));
    }

    private static readonly IReadOnlyList<Rule> BugReportRules =
    [
        Closes,
        On("Open file", "open the saved report",
            c => c.Bench.Opened.Items is [var file] && file.StartsWith(c.Bench.Dir, StringComparison.OrdinalIgnoreCase) && File.Exists(file)),
        On("Copy again", "copy the report again", c => c.Bench.Script.Copied.Items.Any(t => t.StartsWith("Pairnets bug report", StringComparison.Ordinal))),
    ];

    // ------------------------------------------------------------------ the tray menu and icon (the real tray controller)

    /// <summary>
    /// Every tray menu item and the icon's clicks, on the real tray controller against the test nest. The folder holds
    /// another computer's marker, so syncing waits for "Confirm this folder…", the item that only shows when blocked.
    /// </summary>
    private async Task<IReadOnlyList<string>> PressTheTrayMenu()
    {
        var failures = new List<string>();
        await using var app = await TrayHarness.CreateAsync(fixture.Nest, name: "TRAY-TEST", watchFolder: false);
        fixture.TrayMenuItems = app.PressableMenuItems;
        File.WriteAllText(Path.Combine(app.Folder, ".pairnets-marker"), Guid.NewGuid().ToString("D"));
        app.Dialogs.When("was synced by Pairnets before", MessageBoxResult.Yes);
        await app.StartAsync();

        async Task Step(string what, Func<Task> press, Func<bool> done, string should, int seconds = 15)
        {
            try
            {
                await press();
                await app.WaitUntil(done, should, seconds);
            }
            catch (TimeoutException)
            {
                failures.Add($"{what} did nothing: it should {should}.");
            }
            catch (XunitException ex)
            {
                failures.Add(ex.Message);
            }
        }

        await app.WaitUntil(() => app.Tray.Session?.Status.BlockReason == BlockReason.ForeignMarker, "the folder to wait for a decision");
        await app.Wpf.Ui(app.Tray.RefreshNow);
        await Step("Pressing 'Confirm this folder…' in the tray menu", () => app.Menu("Confirm this folder…"),
            () => app.Dialogs.Asked.Items.Any(q => q.Text.Contains("was synced by Pairnets before", StringComparison.Ordinal))
                && app.Tray.Session?.Status.Status == RunnerStatus.Idle, "ask whether to keep syncing this folder, and then sync it");

        await app.Wpf.Ui(() => app.Tray.Window!.Hide());
        await Step("Pressing 'Open Pairnets' in the tray menu", () => app.Menu("Open Pairnets"), () => app.Tray.Window is { IsVisible: true },
            "open the main window");
        await Step("Pressing 'Settings…' in the tray menu", () => app.Menu("Settings…"),
            () => app.Tray.Window is { IsVisible: true, Page: MainPage.Settings, SettingsPage.Content: SettingsView }, "open Settings in the main window");

        File.WriteAllText(Path.Combine(app.Folder, "from-the-tray.txt"), "hello");
        await Step("Pressing 'Sync now' in the tray menu", () => app.Menu("Sync now"),
            () => fixture.Nest.Store.ReadManifest(null).Entries.Any(e => e.Path == "from-the-tray.txt" && !e.Deleted), "upload a new file");
        await Step("Pressing 'Open folder' in the tray menu", () => app.Menu("Open folder"), () => app.Platform.Opened.Items.Contains(app.Folder),
            "open the synced folder");
        await Step("Pressing 'View log' in the tray menu", () => app.Menu("View log"),
            () => app.Platform.Opened.Items.Any(p => p.StartsWith(app.Env.LogsDir, StringComparison.OrdinalIgnoreCase)), "open today's log");
        await Step("Pressing 'Report a bug…' in the tray menu", () => app.Menu("Report a bug…"),
            () => Application.Current.Windows.OfType<BugReportWindow>().Any(w => w.IsVisible)
                && app.Dialogs.Copied.Items.Any(t => t.StartsWith("Pairnets bug report", StringComparison.Ordinal)), "build a bug report and copy it");
        await Step("Pressing 'Pause syncing' in the tray menu", () => app.Menu("Pause syncing"),
            () => app.Tray.Session?.Status.Paused == true && app.SavedSettings.Paused, "pause syncing and remember it");
        await app.Wpf.Ui(app.Tray.RefreshNow);
        await Step("Pressing 'Resume syncing' in the tray menu", () => app.Menu("Resume syncing"),
            () => app.Tray.Session?.Status.Paused == false && !app.SavedSettings.Paused, "resume syncing and remember it");
        await Step("Pressing 'Start with Windows' in the tray menu", () => app.Menu("Start with Windows"),
            () => app.Platform.AutoStartChanges.Items.LastOrDefault() && app.SavedSettings.StartWithWindows, "turn on starting with Windows");
        app.Platform.SetAutoStart(false); // turned off in Windows' own list of startup apps
        await Step("Opening the tray menu", app.OpenMenu,
            () => !app.Tray.TrayIcon.ContextMenuStrip!.Items.OfType<System.Windows.Forms.ToolStripMenuItem>().Single(i => i.Text == "Start with Windows").Checked,
            "show 'Start with Windows' as Windows has it (off)");
        await Step("A click on the tray icon", app.ClickIcon, () => app.Tray.Panel is { IsVisible: true }, "open the quick-look panel");
        await Step("A double-click on the tray icon", app.DoubleClickIcon, () => app.Tray.Panel is not { IsVisible: true } && app.Tray.Window is { IsVisible: true },
            "close the panel and open the main window");
        await Step("Pressing 'Exit' in the tray menu", () => app.Menu("Exit"), () => app.Shutdowns == 1 && app.Tray.Window is not { IsVisible: true },
            "close the windows and quit Pairnets");
        return failures;
    }
}

/// <summary>The event handlers named in the Windows app's XAML files (Click="OnPause" and the like).</summary>
internal static partial class XamlHandlers
{
    public sealed record XamlHandler(string File, string Class, string Event, string Handler, string Label)
    {
        public string Key => Class + "." + Handler;
    }

    public static IEnumerable<XamlHandler> All()
    {
        foreach (var path in Directory.EnumerateFiles(RepoPaths.Of("src", "Pairnets.Client", "Ui"), "*.xaml").Order(StringComparer.Ordinal))
        {
            var text = File.ReadAllText(path);
            var cls = ClassName().Match(text).Groups[1].Value.Split('.')[^1];
            foreach (Match element in Element().Matches(text))
            {
                var attributes = Attribute().Matches(element.Groups["attrs"].Value).ToDictionary(a => a.Groups[1].Value, a => a.Groups[2].Value);
                var label = attributes.GetValueOrDefault("Content") ?? attributes.GetValueOrDefault("Header") ?? attributes.GetValueOrDefault("ToolTip")
                    ?? InnerText(text, element) ?? attributes.GetValueOrDefault("x:Name");
                foreach (var (name, value) in attributes.Where(a => a.Key != "Mode" && Regex.IsMatch(a.Value, "^On[A-Z][A-Za-z0-9]*$")))
                    yield return new XamlHandler(Path.GetFileName(path), cls, name, value, label is null ? $"<{element.Groups["tag"].Value}>" : $"\"{label}\"");
            }
        }
    }

    /// <summary>The first Text="…" inside a button ("Pause" in a button made of an icon and a text).</summary>
    private static string? InnerText(string xaml, Match element)
    {
        var tag = element.Groups["tag"].Value;
        if (!tag.EndsWith("Button", StringComparison.Ordinal) || element.Value.EndsWith("/>", StringComparison.Ordinal))
            return null;
        var start = element.Index + element.Length;
        var end = xaml.IndexOf("</" + tag + ">", start, StringComparison.Ordinal);
        var inner = end < 0 ? Match.Empty : InnerTextAttribute().Match(xaml, start, end - start);
        return inner.Success ? inner.Groups[1].Value : null;
    }

    [GeneratedRegex("\\bText=\"([^\"{][^\"]*)\"")]
    private static partial Regex InnerTextAttribute();

    [GeneratedRegex("x:Class=\"([^\"]+)\"")]
    private static partial Regex ClassName();

    [GeneratedRegex("<(?<tag>[A-Za-z][\\w:.]*)(?<attrs>(?:\\s+[\\w:.]+\\s*=\\s*\"[^\"]*\")*)\\s*/?>")]
    private static partial Regex Element();

    [GeneratedRegex("([\\w:.]+)\\s*=\\s*\"([^\"]*)\"")]
    private static partial Regex Attribute();
}
