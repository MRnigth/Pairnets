using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.SignalR.Client;
using Microsoft.Extensions.DependencyInjection;
using Pairnets.Core;
using Pairnets.Core.Settings;
using Pairnets.Core.Sync;
using Pairnets.Desktop.Views;
using Pairnets.Server.Services;
using Pairnets.Tests.Infrastructure;

namespace Pairnets.Tests.Ui;

/// <summary>
/// The real Mac/Linux app (<see cref="Pairnets.Desktop.DesktopController"/>) against a real test nest, driven
/// only through its windows, its tray menu and the nest's website, as a person would. The Windows app's tests
/// (tests/Pairnets.Client.Tests) use the same scenario names.
/// </summary>
public sealed class DesktopControllerTests
{
    // ------------------------------------------------------------------ first run

    [AvaloniaFact]
    public async Task FirstRun_SignInWithBrowser_ApproveOnNest_PickFolder_Start()
    {
        await using var app = await AppUnderTest.StartAsync(signedIn: false);
        var signIn = await app.WindowAsync("Pairnets – sign in");
        Assert.Null(app.App.Window); // nothing but the sign-in before signing in

        // Which nest: its address, then "Sign in with your browser".
        Person.Type(Screen.Named<TextBox>(signIn, "AddressBox"), app.Nest.Url.ToString());
        var browser = Screen.Named<Button>(signIn, "BrowserButton");
        await Wait.Until(() => browser.IsEffectivelyEnabled, "the nest to be found");
        Assert.Equal("Sign in with your browser", Screen.Describe(browser));
        Person.Click(browser);

        // The nest's approval page opens with the code the window shows.
        var link = await Wait.For(() => app.Platform.Opened.FirstOrDefault(o => o.Contains("/link?code=", StringComparison.Ordinal)), "the nest's approval page to open");
        var code = link[(link.IndexOf("code=", StringComparison.Ordinal) + 5)..];
        Assert.StartsWith("https://localhost/link?code=", link);
        Assert.Equal(code, Screen.Named<TextBlock>(signIn, "CodeText").Text);

        // The owner allows it on the nest's website.
        await app.ApproveOnNestAsync(code);

        // The folder: picked with Browse… (it does not exist yet, so Pairnets offers to create it), then start.
        var start = await Wait.For(() => Screen.All<Button>(signIn).FirstOrDefault(b => b.IsEffectivelyVisible && Screen.Describe(b) == "Start syncing"),
            "the folder step after approving");
        using (AppUnderTest.Picking(app.Folder))
            Person.Click(Screen.Find<Button>(signIn, "Browse…"));
        Assert.Equal(app.Folder, Screen.Named<TextBox>(signIn, "FolderBox").Text);
        Person.Click(start);
        await app.AnswerAsync("Pairnets", "Yes"); // "The folder … does not exist. Create it?"

        // Signed in: settings in the app's own folder, the key in the secret store, the main window, syncing.
        await Wait.Until(() => !signIn.IsVisible, "the sign-in window to close");
        await Wait.Until(() => app.App.Window is { IsVisible: true }, "the main window to open");
        var device = Assert.Single(app.NestDevices);
        var saved = SettingsStore.Load(app.Env.SettingsPath);
        Assert.Equal((device.Id, app.Folder, true), (saved.DeviceId, saved.Folder, saved.FirstRunCompleted));
        Assert.Equal(device.Name, saved.DeviceName);
        Assert.Equal(app.Nest.Url.ToString(), saved.ServerUrl);
        Assert.Single(app.Platform.SecretStore.Kept);
        Assert.True(Directory.Exists(app.Folder));
        Assert.Equal([false], app.Platform.AutoStartChanges); // "Start Pairnets when I log in" was left off
        await app.WaitUntilIdleAsync();
        File.WriteAllText(Path.Combine(app.Folder, "first.txt"), "synced after signing in");
        Person.Click(Screen.Find<Button>(app.MainWindow, "Sync now"));
        await app.WaitForNestAsync(f => f.ContainsKey("first.txt"), "the first file to reach the nest");
    }

    [AvaloniaFact]
    public async Task FirstRun_GoogleAndEmailButtons_OpenTheNest()
    {
        await using var app = await AppUnderTest.StartAsync(signedIn: false, nestConfig: AppUnderTest.GoogleAndEmail);
        app.OwnerUsesGoogleAndEmail();
        var signIn = await app.WindowAsync("Pairnets – sign in");
        Person.Type(Screen.Named<TextBox>(signIn, "AddressBox"), app.Nest.Url.ToString());
        var google = Screen.Named<Button>(signIn, "GoogleButton");
        await Wait.Until(() => google.IsEffectivelyVisible && google.IsEffectivelyEnabled, "\"Continue with Google\" to show");
        Assert.Equal("More ways to sign in in your browser", Screen.Describe(Screen.Named<Button>(signIn, "BrowserButton")));

        Person.Click(google);
        var googleLink = await Wait.For(() => app.Platform.Opened.FirstOrDefault(o => o.Contains("method=google", StringComparison.Ordinal)), "the nest to open on Google");
        Assert.Matches(@"^https://localhost/link\?code=[A-Z2-9]{4}-[A-Z2-9]{4}&method=google$", googleLink);

        Person.Click(Screen.Find<Button>(signIn, "Cancel")); // back to the first step
        var email = Screen.Named<Button>(signIn, "EmailButton");
        Assert.False(email.IsEffectivelyEnabled); // no address typed yet
        Person.Type(Screen.Named<TextBox>(signIn, "EmailBox"), "owner@example.com");
        await Wait.Until(() => email.IsEffectivelyEnabled, "\"Continue with email\" to be pressable");
        Person.Click(email);
        var emailLink = await Wait.For(() => app.Platform.Opened.FirstOrDefault(o => o.Contains("method=email", StringComparison.Ordinal)), "the nest to open on email");
        Assert.EndsWith("&method=email&email=owner%40example.com", emailLink);

        Person.Click(Screen.Find<Button>(signIn, "Cancel"));
        Person.Click(Screen.Named<Button>(signIn, "BrowserButton"));
        await Wait.Until(() => app.Platform.Opened.Count == 3, "the nest to open for the other ways to sign in");
        Assert.DoesNotContain("method=", app.Platform.Opened[2]);
    }

    // ------------------------------------------------------------------ syncing

    [AvaloniaFact]
    public async Task SyncNow_UploadsAFile()
    {
        await using var app = await AppUnderTest.StartAsync();
        await app.WaitUntilIdleAsync();
        var passes = new List<PassReport>();
        app.App.Session!.Runner.PassCompleted += r =>
        {
            lock (passes)
                passes.Add(r);
        };
        bool ManualPasses(int count)
        {
            lock (passes)
                return passes.Count(p => p.Reasons.Contains("manual")) >= count;
        }

        File.WriteAllText(Path.Combine(app.Folder, "hello.txt"), "hello from the Mac");
        Person.Click(Screen.Find<Button>(app.MainWindow, "Sync now"));
        await Wait.Until(() => ManualPasses(1), "a sync pass started by \"Sync now\"");
        await app.WaitForNestAsync(f => f.TryGetValue("hello.txt", out var size) && size == 18, "hello.txt to reach the nest");

        // The tray menu and the tray panel's menu do the same.
        Person.Click(app.TrayItem("Sync now"));
        await Wait.Until(() => ManualPasses(2), "a sync pass started from the tray menu");
        app.App.ClickTrayIcon();
        var panel = await Wait.For(() => app.App.Panel is { IsVisible: true } p ? p : null, "the tray panel to open");
        Person.Click(Screen.Find<Button>(panel, "More"));
        Person.Click(Screen.Find<MenuItem>(panel, "Sync now"));
        await Wait.Until(() => ManualPasses(3), "a sync pass started from the tray panel");
    }

    [AvaloniaFact]
    public async Task PauseAndResume()
    {
        await using var app = await AppUnderTest.StartAsync();
        await app.WaitUntilIdleAsync();
        var main = app.MainWindow;

        Person.Click(Screen.Find<Button>(main, "Pause"));
        await Wait.Until(() => app.App.Session!.Status.Paused, "syncing to pause");
        Assert.True(SettingsStore.Load(app.Env.SettingsPath).Paused); // still paused after a restart
        app.App.RefreshNow();
        Assert.NotNull(Screen.Find<Button>(main, "Resume"));

        // A file written while paused stays on this computer.
        File.WriteAllText(Path.Combine(app.Folder, "while-paused.txt"), "later");
        await Task.Delay(3000); // longer than the folder watcher's 2 seconds
        Assert.DoesNotContain("while-paused.txt", (await app.NestFilesAsync()).Keys);

        // "Resume syncing" in the tray menu.
        Person.Click(app.TrayItem("Resume syncing"));
        await Wait.Until(() => !app.App.Session!.Status.Paused, "syncing to resume");
        Assert.False(SettingsStore.Load(app.Env.SettingsPath).Paused);
        await app.WaitForNestAsync(f => f.ContainsKey("while-paused.txt"), "the file written while paused to reach the nest");
        Assert.Equal("Pause syncing", app.TrayItem("Pause syncing").Header);

        // And the tray panel's Pause / Resume button.
        app.App.ClickTrayIcon();
        var panel = await Wait.For(() => app.App.Panel is { IsVisible: true } p ? p : null, "the tray panel to open");
        Person.Click(Screen.Find<Button>(panel, "Pause"));
        await Wait.Until(() => app.App.Session!.Status.Paused, "the tray panel to pause syncing");
        app.App.RefreshNow();
        Person.Click(Screen.Find<Button>(panel, "Resume"));
        await Wait.Until(() => !app.App.Session!.Status.Paused, "the tray panel to resume syncing");
    }

    [AvaloniaFact]
    public async Task HistoryRestore_BringsBackAnOldVersion()
    {
        await using var app = await AppUnderTest.StartAsync();
        await app.WaitUntilIdleAsync();
        var main = app.MainWindow;
        var file = Path.Combine(app.Folder, "notes.txt");
        File.WriteAllText(file, "first version");
        Person.Click(Screen.Find<Button>(main, "Sync now"));
        await app.WaitForNestAsync(f => f.TryGetValue("notes.txt", out var size) && size == 13, "the first version to reach the nest");
        File.WriteAllText(file, "the second version");
        Person.Click(Screen.Find<Button>(main, "Sync now"));
        await app.WaitForNestAsync(f => f.TryGetValue("notes.txt", out var size) && size == 18, "the second version to reach the nest");

        app.Open("History");
        Person.Click(Screen.Find<RadioButton>(main, "All files"));
        var row = await Wait.For(() => Screen.All<ListBoxItem>(main).FirstOrDefault(r => Screen.Describe(r).StartsWith("notes.txt", StringComparison.Ordinal)),
            "notes.txt to be listed");
        Person.Click(row);
        await Wait.Until(() => main.VersionRows == 1, "the older version of notes.txt to be listed");
        Person.Click(Screen.Find<Button>(main, "Restore"));

        await Wait.Until(() => main.RestoreText.StartsWith("Restored.", StringComparison.Ordinal), "the page to say it was restored");
        await Wait.Until(() => File.ReadAllText(file) == "first version", "the old version to be back in the folder");
        await app.WaitForNestAsync(f => f["notes.txt"] == 13, "the old version to be the current one on the nest");
    }

    [AvaloniaFact]
    public async Task FixBlocked_MassDelete_AllowDeletions()
    {
        await using var app = await AppUnderTest.StartAsync();
        await app.WaitUntilIdleAsync();
        var main = app.MainWindow;
        for (var i = 1; i <= 6; i++)
            File.WriteAllText(Path.Combine(app.Folder, $"file{i}.txt"), $"file {i}");
        Person.Click(Screen.Find<Button>(main, "Sync now"));
        await app.WaitForNestAsync(f => f.Count == 6, "the six files to reach the nest");
        await app.WaitUntilIdleAsync();

        // Deleting two of six (more than a fifth) blocks the pass until someone decides.
        File.Delete(Path.Combine(app.Folder, "file1.txt"));
        File.Delete(Path.Combine(app.Folder, "file2.txt"));
        Person.Click(Screen.Find<Button>(main, "Sync now"));
        await Wait.Until(() => app.App.Session!.Status is { Status: RunnerStatus.Blocked, BlockReason: BlockReason.MassDelete }, "the deletions to be blocked");
        await Wait.Until(() => app.Platform.Notices.Any(n => n.Title == "Deletions blocked"), "a \"Deletions blocked\" notification");
        Assert.Equal(6, (await app.NestFilesAsync()).Count); // nothing was deleted on the nest
        app.App.RefreshNow();
        Assert.True(app.TrayItem("Allow these deletions (2)…").IsEnabled);

        Person.Click(Screen.Find<Button>(main, "Allow these deletions (2)…"));
        var question = await app.DialogAsync("Pairnets – allow deletions?");
        Assert.Contains("file1.txt", AppUnderTest.TextOf(question));
        await app.AnswerAsync("Pairnets – allow deletions?", "Yes");
        await app.WaitForNestAsync(f => f.Count == 4 && !f.ContainsKey("file1.txt"), "the two deletions to reach the nest");
        await app.WaitUntilIdleAsync();

        // Again, decided from the tray menu this time.
        File.Delete(Path.Combine(app.Folder, "file3.txt"));
        File.Delete(Path.Combine(app.Folder, "file4.txt"));
        Person.Click(app.TrayItem("Sync now"));
        await Wait.Until(() => app.App.Session!.Status.BlockReason == BlockReason.MassDelete, "the second deletions to be blocked");
        Person.Click(app.TrayItem("Allow these deletions (2)…"));
        await app.AnswerAsync("Pairnets – allow deletions?", "Yes");
        await app.WaitForNestAsync(f => f.Count == 2, "the second deletions to reach the nest");
        Assert.Equal("No action needed", app.TrayItem("No action needed").Header);
    }

    // ------------------------------------------------------------------ settings, signing out, starting over

    [AvaloniaFact]
    public async Task SettingsSave_RenamesThisComputer()
    {
        await using var app = await AppUnderTest.StartAsync();
        await app.WaitUntilIdleAsync();

        // Another computer listens on the nest's push channel.
        var other = app.Nest.MintKey("DESKTOP");
        await using var hub = new HubConnectionBuilder()
            .WithUrl(new Uri(app.Nest.Url, "hub"), o =>
            {
                o.AccessTokenProvider = () => Task.FromResult<string?>(other.Key);
                o.Headers[PairnetsHeaders.DeviceId] = other.Name;
            })
            .Build();
        var renamed = new TaskCompletionSource<(string Id, string Name)>(TaskCreationOptions.RunContinuationsAsynchronously);
        hub.On<string, string>("DeviceRenamed", (id, name) => renamed.TrySetResult((id, name)));
        await hub.StartAsync();

        var page = await app.SettingsPageAsync();
        Person.Type(Screen.Named<TextBox>(page, "DeviceBox"), "STUDIO-MAC");
        Person.Click(Screen.Find<Button>(page, "Save"));

        Assert.Equal((app.Device!.Id, "STUDIO-MAC"), await renamed.Task.WaitAsync(TimeSpan.FromSeconds(20)));
        await Wait.Until(() => app.MainWindow.Page == MainPage.Overview, "the app to go back to the overview");
        Assert.Equal("STUDIO-MAC", SettingsStore.Load(app.Env.SettingsPath).DeviceName);
        Assert.Equal("STUDIO-MAC", app.NestDevices.Single(d => d.Id == app.Device.Id).Name);
        await Wait.Until(() => app.App.Session?.Settings.DeviceName == "STUDIO-MAC", "syncing to go on under the new name");
        await app.WaitUntilIdleAsync();
    }

    [AvaloniaFact]
    public async Task SignOut_RemovesTheDeviceOnTheNest()
    {
        await using var app = await AppUnderTest.StartAsync();
        await app.WaitUntilIdleAsync();
        File.WriteAllText(Path.Combine(app.Folder, "mine.txt"), "stays here");

        var page = await app.SettingsPageAsync();
        Person.Click(Screen.Find<Button>(page, "Sign out of this computer"));
        await app.AnswerAsync("Pairnets – sign out", "Sign out");

        await app.WindowAsync("Pairnets – sign in");
        Assert.DoesNotContain(app.NestDevices, d => d.Id == app.Device!.Id); // its key no longer works on the nest
        var saved = SettingsStore.Load(app.Env.SettingsPath);
        Assert.Equal((null, null, false), (saved.ProtectedToken, saved.DeviceId, saved.FirstRunCompleted));
        Assert.Equal(app.Folder, saved.Folder); // signing in again offers the same folder
        Assert.Null(app.App.Session);
        Assert.True(File.Exists(Path.Combine(app.Folder, "mine.txt")));
    }

    [AvaloniaFact]
    public async Task Reset_ForgetsEverythingAndShowsSignIn()
    {
        await using var app = await AppUnderTest.StartAsync();
        await app.WaitUntilIdleAsync();
        var key = SettingsStore.Load(app.Env.SettingsPath).ProtectedToken!;
        File.WriteAllText(Path.Combine(app.Folder, "keep-me.txt"), "mine");
        Person.Click(Screen.Find<Button>(app.MainWindow, "Sync now"));
        await app.WaitForNestAsync(f => f.ContainsKey("keep-me.txt"), "keep-me.txt to reach the nest");
        var notes = app.App.Session!.StateDirectory;
        Assert.StartsWith(app.Env.LocalDir, notes); // the sync notes live in the app's own folder

        var page = await app.SettingsPageAsync();
        Person.Click(Screen.Find<Button>(page, "Reset this app…"));
        await app.AnswerAsync("Pairnets – reset", "Reset");

        await Wait.Until(() => app.Desktop.Windows.Any(w => w.IsVisible && w.Title is "Pairnets – sign in" or "Pairnets"), "the sign-in window (or a note) after the reset");
        if (app.Desktop.Windows.LastOrDefault(w => w.IsVisible && w.Title == "Pairnets" && w.GetType() == typeof(Window)) is { } note)
            Assert.Fail("Reset left notes: " + AppUnderTest.TextOf(note));
        await app.WindowAsync("Pairnets – sign in");
        Assert.False(File.Exists(app.Env.SettingsPath));
        Assert.False(Directory.Exists(notes));
        Assert.Contains(key, app.Platform.SecretStore.Forgotten);
        Assert.Empty(app.Platform.SecretStore.Kept);
        Assert.False(app.Platform.AutoStartChanges[^1]);
        Assert.DoesNotContain(app.NestDevices, d => d.Id == app.Device!.Id);
        Assert.True(File.Exists(Path.Combine(app.Folder, "keep-me.txt"))); // the files stay
        Assert.False(File.Exists(Path.Combine(app.Folder, Pairnets.Core.Paths.PathRules.MarkerFileName)));
        Assert.True(File.Exists(app.App.LogFile)); // the logs stay too
    }

    // ------------------------------------------------------------------ updates

    [AvaloniaFact]
    public async Task UpdateServer_AsksTheNestToUpdate()
    {
        using var updaterDir = new TempDir("updater");
        var script = Path.Combine(updaterDir.Path, "update.sh"); // the server's self-updater is "installed"
        File.WriteAllText(script, "#!/bin/sh\n");
        var older = new OlderNest("0.9.0");
        await using var app = await AppUnderTest.StartAsync(nestConfig: new() { ["Sync:UpdaterScript"] = script },
            configureNest: b => b.Services.AddSingleton<IStartupFilter>(older));

        // The app finds the nest on an older version and asks by itself.
        var asked = (ServerUpdateWindow)await app.WindowAsync("Pairnets – update the server");
        Assert.Equal("Your server should be updated", asked.HeadingText);
        Assert.Contains("0.9.0", asked.ExplanationText);
        Person.Click(Screen.Find<Button>(asked, "Don't ask for this version"));
        await Wait.Until(() => !asked.IsVisible, "the window to close");
        Assert.Equal("0.9.0", SettingsStore.Load(app.Env.SettingsPath).SkippedServerVersion);

        // "Later" changes nothing.
        app.App.RefreshNow();
        var serverButton = Screen.Named<Button>(app.MainWindow, "VersionButton");
        Person.Click(serverButton);
        var later = await app.WindowAsync("Pairnets – update the server", not: asked);
        Person.Click(Screen.Find<Button>(later, "Later"));
        await Wait.Until(() => !later.IsVisible, "the window to close");
        Assert.False(SettingsStore.Load(app.Env.SettingsPath).AutoUpdateServer);

        // "From now on, update the server automatically" is kept.
        Person.Click(serverButton);
        var always = await app.WindowAsync("Pairnets – update the server", not: later);
        Person.Click(Screen.Find<CheckBox>(always, "From now on, update the server automatically"));
        Person.Click(Screen.Find<Button>(always, "Later"));
        await Wait.Until(() => SettingsStore.Load(app.Env.SettingsPath).AutoUpdateServer, "\"update automatically\" to be saved");

        // "Update server" asks the nest (POST /api/update); its updater installs the new version.
        Person.Click(serverButton);
        var update = (ServerUpdateWindow)await app.WindowAsync("Pairnets – update the server", not: always);
        var updater = app.Nest.Services.GetRequiredService<ServerUpdater>();
        var asks = 0;
        using var stop = new CancellationTokenSource();
        var root = Task.Run(async () =>
        {
            while (!stop.IsCancellationRequested)
            {
                if (File.Exists(updater.RequestFile))
                {
                    Interlocked.Increment(ref asks);
                    older.Version = null; // installed: the nest reports its real version from now on
                    File.WriteAllText(updater.StatusFile, JsonSerializer.Serialize(new UpdaterStatus(true, "succeeded", "Updated."), PairnetsJson.Options));
                    File.Delete(updater.RequestFile);
                }
                await Task.Delay(100);
            }
        });
        Person.Click(Screen.Find<Button>(update, "Update server"));
        await Wait.Until(() => update.HeadingText == $"Server updated to {PairnetsInfo.ProductVersion}", "the window to say the server was updated", seconds: 40);
        stop.Cancel();
        await root;
        Assert.Equal(1, asks);
        Person.Click(Screen.Find<Button>(update, "Close"));
        await Wait.Until(() => !update.IsVisible, "the window to close");
    }

    /// <summary>Makes the nest say it runs an older version (GET /api/info) until <see cref="Version"/> is null.</summary>
    private sealed class OlderNest(string version) : IStartupFilter
    {
        private volatile string? _version = version;

        public string? Version
        {
            get => _version;
            set => _version = value;
        }

        public Action<IApplicationBuilder> Configure(Action<IApplicationBuilder> next) => app =>
        {
            app.Use(async (context, nextStep) =>
            {
                if (Version is not { } reported || context.Request.Path != "/api/info")
                {
                    await nextStep(context);
                    return;
                }
                var body = context.Response.Body;
                using var buffer = new MemoryStream();
                context.Response.Body = buffer;
                try
                {
                    await nextStep(context);
                }
                finally
                {
                    context.Response.Body = body;
                }
                var info = JsonNode.Parse(buffer.ToArray())!.AsObject();
                info[info.Select(p => p.Key).First(k => k.Equals("serverVersion", StringComparison.OrdinalIgnoreCase))] = reported;
                var bytes = Encoding.UTF8.GetBytes(info.ToJsonString());
                context.Response.ContentLength = bytes.Length;
                await body.WriteAsync(bytes);
            });
            next(app);
        };
    }

    [AvaloniaFact]
    public async Task AppUpdate_Download_OpensReleasePage()
    {
        await using var app = await AppUnderTest.StartAsync(releaseVersion: "99.0.0");
        await app.WaitUntilIdleAsync();
        if (app.Feed.Asset is null)
            return; // no download for this kind of computer (e.g. ARM Linux): there is nothing to offer

        var page = await app.SettingsPageAsync();
        Person.Click(Screen.Find<Button>(page, "Check now"));
        await Wait.Until(() => Screen.Named<TextBlock>(page, "VersionText").Text?.EndsWith("version 99.0.0 is available", StringComparison.Ordinal) == true,
            "Settings to say version 99.0.0 is available");
        await Wait.Until(() => app.Platform.Notices.Any(n => n.Title == "Pairnets update available"), "an \"update available\" notification");
        Person.Click(Screen.Find<Button>(page, "Cancel"));

        var main = app.MainWindow;
        app.App.RefreshNow();
        Assert.Equal("Pairnets 99.0.0 is available", Screen.Named<TextBlock>(main, "UpdateTitle").Text);
        Person.Click(Screen.Find<Button>(main, "Download"));
        Assert.Equal(app.Feed.ReleasePage.ToString(), app.Platform.Opened[^1]); // the app is unsigned: download by hand
        Person.Click(Screen.Find<Button>(main, "Later"));
        app.App.RefreshNow();
        Assert.False(Screen.Named<Border>(main, "UpdateCard").IsVisible);
        Assert.True(app.Feed.VersionChecks >= 1);
    }

    // ------------------------------------------------------------------ the rest of the buttons

    [AvaloniaFact]
    public async Task AddComputer_ManageDevices_ViewLog_ReportBug()
    {
        await using var app = await AppUnderTest.StartAsync();
        await app.WaitUntilIdleAsync();
        await Wait.Until(() => app.App.Session!.Status.NestUrl == "https://localhost", "the app to learn the nest's website");
        var main = app.MainWindow;

        app.Open("Devices");
        await Wait.Until(() => main.DeviceRows >= 1, "this computer to be listed on the Devices page");
        Person.Click(Screen.Find<Button>(main, "+ Add a computer…"));
        var steps = await app.DialogAsync("Pairnets – add a computer");
        Assert.Contains("type localhost and press \"Sign in with your browser\"", AppUnderTest.TextOf(steps));
        await app.AnswerAsync("Pairnets – add a computer", "OK");

        Person.Click(Screen.Find<Button>(main, "Manage devices on your nest"));
        Assert.Equal("https://localhost/devices", app.Platform.Opened[^1]);

        Person.Click(Screen.Find<Button>(main, "More"));
        Person.Click(Screen.Find<MenuItem>(main, "View log"));
        Assert.Equal(app.App.LogFile, app.Platform.Opened[^1]);
        Assert.StartsWith(app.Env.LogsDir, app.App.LogFile);
        Assert.True(File.Exists(app.App.LogFile));

        Person.Click(Screen.Find<Button>(main, "More"));
        Person.Click(Screen.Find<MenuItem>(main, "Report a bug…"));
        var report = await app.WindowAsync("Pairnets – report a bug");
        var open = Screen.Find<Button>(report, "Open file");
        await Wait.Until(() => open.IsEffectivelyEnabled, "the bug report to be ready");
        Person.Click(open);
        var saved = app.Platform.Opened[^1];
        Assert.StartsWith(app.Env.LogsDir, saved); // next to the app's own logs
        Assert.StartsWith("Pairnets bug report", File.ReadAllText(saved));
        Person.Click(Screen.Find<Button>(report, "Copy again"));
        Assert.Equal("Bug report copied", Screen.Named<TextBlock>(report, "Heading").Text);
        Person.Click(Screen.Find<Button>(report, "Close"));
        await Wait.Until(() => !report.IsVisible, "the bug report to close");
    }

    [AvaloniaFact]
    public async Task Quit_ShutsDown()
    {
        await using var app = await AppUnderTest.StartAsync();
        var main = app.MainWindow;

        app.App.ClickTrayIcon();
        var panel = await Wait.For(() => app.App.Panel is { IsVisible: true } p ? p : null, "the tray panel to open");
        Person.Click(Screen.Find<Button>(panel, "More"));
        Person.Click(Screen.Find<MenuItem>(panel, "Quit Pairnets"));
        Assert.Equal(1, app.Desktop.ShutdownCount);
        Assert.False(main.IsVisible);
        Assert.DoesNotContain(main, app.Desktop.Windows); // closed for good, not hidden
        Assert.False(panel.IsVisible);

        // The menu-bar menu's "Quit Pairnets" quits the same way.
        Person.Click(app.TrayItem("Quit Pairnets"));
        Assert.Equal(2, app.Desktop.ShutdownCount);
    }

    /// <summary>Every item of the menu-bar / tray menu (the native menu, no window of its own).</summary>
    [AvaloniaFact]
    public async Task TrayMenu_EveryItemDoesWhatItSays()
    {
        await using var app = await AppUnderTest.StartAsync();
        await app.WaitUntilIdleAsync();
        var main = app.MainWindow;
        var pressed = new HashSet<NativeMenuItem>();
        void Press(string header)
        {
            var item = app.TrayItem(header);
            Person.Click(item);
            pressed.Add(item);
        }

        Press("Quick status…");
        await Wait.Until(() => app.App.Panel is { IsVisible: true }, "the tray panel to open");
        app.App.Panel!.Hide();

        main.Hide();
        Press("Open Pairnets");
        Assert.True(main.IsVisible);

        Press("Open folder");
        Assert.Equal(app.Folder, app.Platform.Opened[^1]);

        Press("Settings…");
        await Wait.Until(() => main.Page == MainPage.Settings && main.SettingsShown, "the Settings page to show");

        Press("View log");
        Assert.Equal(app.App.LogFile, app.Platform.Opened[^1]);

        Press("Report a bug…");
        var report = await app.WindowAsync("Pairnets – report a bug");
        report.Close();

        Press("Pause syncing");
        await Wait.Until(() => app.App.Session!.Status.Paused, "syncing to pause");
        Press("Resume syncing");
        await Wait.Until(() => !app.App.Session!.Status.Paused, "syncing to resume");

        Press("Start at login");
        Assert.True(app.Platform.IsAutoStartEnabled());
        Assert.True(app.TrayItem("Start at login").IsChecked);
        Assert.True(SettingsStore.Load(app.Env.SettingsPath).StartWithWindows);
        Press("Start at login");
        Assert.False(app.TrayItem("Start at login").IsChecked);

        Press("Sync now");

        // The status line and "No action needed" only inform: they are greyed out.
        var status = app.App.TrayMenu!.Items.OfType<NativeMenuItem>().ElementAt(2);
        var fix = app.TrayItem("No action needed");
        Assert.Throws<CannotPressException>(() => Person.Click(status));
        Assert.Throws<CannotPressException>(() => Person.Click(fix));
        pressed.UnionWith([status, fix]); // pressed when something is blocked: FixBlocked_MassDelete_AllowDeletions

        Press("Quit Pairnets");
        Assert.Equal(1, app.Desktop.ShutdownCount);

        var never = app.App.TrayMenu!.Items.OfType<NativeMenuItem>().Where(i => i is not NativeMenuItemSeparator && !pressed.Contains(i))
            .Select(i => "\"" + i.Header + "\"").ToList();
        Assert.True(never.Count == 0, "Never pressed in the tray menu: " + string.Join(", ", never));
    }
}
