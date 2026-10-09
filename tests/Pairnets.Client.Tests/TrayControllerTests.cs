using System.Net;
using System.Text.Json.Nodes;
using System.Windows;
using System.Windows.Controls;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.SignalR.Client;
using Microsoft.Extensions.DependencyInjection;
using Pairnets.Client.Ui;
using Pairnets.Core;
using Pairnets.Core.Client;
using Pairnets.Core.Paths;
using Pairnets.Core.Settings;
using Pairnets.Core.Sync;
using Pairnets.Server.Storage;
using Pairnets.Tests.Infrastructure;

namespace Pairnets.Client.Tests;

/// <summary>
/// The real Windows tray controller, driven only through its windows, its tray menu and its icon, against a real test
/// nest: signing in, syncing, pausing, history, blocked deletions, settings, signing out, resetting, updating the
/// server and the app, and quitting. Windows itself is a stand-in (<see cref="FakeWindowsPlatform"/>).
/// </summary>
public sealed class TrayControllerTests
{
    private static bool OnServer(TestServer nest, string path) => nest.Store.ReadManifest(null).Entries.Any(e => e.Path == path && !e.Deleted);

    private static AuthStore Auth(TestServer nest) => nest.Services.GetRequiredService<AuthStore>();

    [Fact]
    public async Task FirstRun_SignInWithBrowser_ApproveOnNest_PickFolder_Start()
    {
        await using var nest = await TestServer.StartWithWebsiteAsync(TrayHarness.BehindATunnel());
        await using var app = await TrayHarness.CreateAsync();
        app.Dialogs.Folder = app.Folder;
        var started = app.StartFirstRun();

        var signIn = await app.Showing<SettingsWindow>("the sign-in window on the first start");
        // The browser comes back through a pairnets:// link, which pokes the running app: it already listens.
        Assert.True(AppActivation.TrySignalRunning(app.Env.ActivationPipeName), "The sign-in window does not hear the browser's pairnets:// link.");

        await app.Type(signIn.SignIn.AddressBox, nest.Url.ToString());
        await app.WaitUntil(() => signIn.SignIn.BrowserButton.IsEnabled, "the sign-in window to find the nest");
        await app.Press(signIn, "Sign in with your browser");
        var link = await app.Opened(l => l.Contains("/link?code=", StringComparison.Ordinal), "the browser to open the nest");
        Assert.Equal($"https://localhost/link?code={TrayHarness.CodeIn(link)}", link);
        Assert.Equal(TrayHarness.CodeIn(link), await app.Wpf.Ui(() => signIn.SignIn.CodeText.Text)); // the same code in both places

        using (var owner = await TrayHarness.OwnerOnTheWebsite(nest))
            Assert.Equal(HttpStatusCode.OK, (await owner.PostAsync($"web/api/pair/{TrayHarness.CodeIn(link)}/approve", null)).StatusCode);
        await app.WaitUntil(() => signIn.SignIn.FolderStep.IsVisible, "the folder step after approving on the nest");

        await app.Press(signIn, "Browse…");
        Assert.Equal(app.Folder, await app.Wpf.Ui(() => signIn.SignIn.Folder));
        File.WriteAllText(Path.Combine(app.Folder, "brought-along.txt"), "already here");
        await app.Press(signIn, "Start syncing");
        await started.WaitAsync(TimeSpan.FromSeconds(30));

        var saved = app.SavedSettings;
        var device = Auth(nest).ListDevices().Single();
        Assert.Equal((device.Id, device.Name, app.Folder, true, true), (saved.DeviceId, saved.DeviceName, saved.Folder, saved.FirstRunCompleted, saved.StartWithWindows));
        Assert.Equal(nest.Url.ToString(), saved.ServerUrl);
        Assert.StartsWith(FakeSecrets.Prefix, saved.ProtectedToken); // kept by the secret store, never in plain text
        Assert.Equal([true], app.Platform.AutoStartChanges.Items);
        await app.WaitUntil(() => app.Tray.Window is { IsVisible: true }, "the main window");
        await app.WaitUntil(() => OnServer(nest, "brought-along.txt"), "the folder's file to reach the nest");
    }

    [Fact]
    public async Task FirstRun_GoogleAndEmailButtons_OpenTheNest()
    {
        await using var nest = await TestServer.StartWithWebsiteAsync(TrayHarness.BehindATunnel(new()
        {
            ["Sync:SmtpHost"] = "smtp.example.test",
            ["Sync:SmtpFrom"] = "Pairnets nest <nest@example.test>",
            ["Sync:GoogleClientId"] = "client-123.apps.googleusercontent.com",
            ["Sync:GoogleClientSecret"] = "test-oauth-secret",
        }));
        Auth(nest).SetOwnerEmail("owner@example.com");
        Auth(nest).SetGoogleAccount("e2e-google-user", "owner@example.com");
        await using var app = await TrayHarness.CreateAsync();
        var started = app.StartFirstRun();
        var signIn = await app.Showing<SettingsWindow>("the sign-in window on the first start");
        using var site = TrayHarness.Website(nest);

        await app.Type(signIn.SignIn.AddressBox, nest.Url.ToString());
        await app.WaitUntil(() => signIn.SignIn.GoogleButton.IsVisible && signIn.SignIn.GoogleButton.IsEnabled, "the Google button for a nest with Google sign-in");
        await app.Press(signIn, "Continue with Google");
        var google = await app.Opened(l => l.Contains("method=google", StringComparison.Ordinal), "the nest's Google sign-in in the browser");
        Assert.Equal($"https://localhost/link?code={TrayHarness.CodeIn(google)}&method=google", google);
        Assert.Equal(Environment.MachineName, Auth(nest).FindPairRequestByCode(TrayHarness.CodeIn(google))!.Name);
        Assert.Equal(HttpStatusCode.OK, (await site.GetAsync(new Uri(google).PathAndQuery.TrimStart('/'))).StatusCode); // the nest has that page

        await app.Press(signIn, "Cancel");
        await app.Type(signIn.SignIn.EmailBox, "owner@example.com");
        await app.Press(signIn, "Continue with email");
        var email = await app.Opened(l => l.Contains("method=email", StringComparison.Ordinal), "the nest's email sign-in in the browser");
        Assert.Equal($"https://localhost/link?code={TrayHarness.CodeIn(email)}&method=email&email=owner%40example.com", email);
        Assert.NotEqual(TrayHarness.CodeIn(google), TrayHarness.CodeIn(email)); // a new code each time
        Assert.Equal(HttpStatusCode.OK, (await site.GetAsync(new Uri(email).PathAndQuery.TrimStart('/'))).StatusCode);

        await app.Wpf.Ui(signIn.Close); // not finished: nothing is saved
        await started.WaitAsync(TimeSpan.FromSeconds(15));
        Assert.False(app.SavedSettings.FirstRunCompleted);
        Assert.Empty(Auth(nest).ListDevices());
    }

    [Fact]
    public async Task SyncNow_UploadsAFile()
    {
        await using var nest = await TestServer.StartWithWebsiteAsync();
        await using var app = await TrayHarness.CreateAsync(nest, watchFolder: false); // only "Sync now" starts a pass
        await app.StartAsync();
        await app.SyncedOnce();

        File.WriteAllText(Path.Combine(app.Folder, "from-the-window.txt"), "1");
        await app.Press(app.Tray.Window!, "Sync now");
        await app.WaitUntil(() => OnServer(nest, "from-the-window.txt"), "the main window's Sync now to upload the file");

        File.WriteAllText(Path.Combine(app.Folder, "from-the-tray-menu.txt"), "2");
        await app.Menu("Sync now");
        await app.WaitUntil(() => OnServer(nest, "from-the-tray-menu.txt"), "the tray menu's Sync now to upload the file");

        File.WriteAllText(Path.Combine(app.Folder, "from-the-panel.txt"), "3");
        await app.ClickIcon();
        var panel = await app.Showing<TrayPanel>("the tray panel");
        await app.Press(panel, "More");
        await app.Press(panel, "Sync now");
        await app.WaitUntil(() => OnServer(nest, "from-the-panel.txt"), "the tray panel's Sync now to upload the file");
        await app.WaitUntil(() => app.Tray.Session!.Activity.Items.Any(i => i.Kind == ActivityKind.Uploaded && i.Path == "from-the-panel.txt"),
            "the upload in Recent activity");
    }

    [Fact]
    public async Task PauseAndResume()
    {
        await using var nest = await TestServer.StartWithWebsiteAsync();
        await using var app = await TrayHarness.CreateAsync(nest);
        await app.StartAsync();
        await app.SyncedOnce();
        var main = app.Tray.Window!;

        await app.Menu("Pause syncing");
        await app.WaitUntil(() => app.Tray.Session!.Status.Status == RunnerStatus.Paused, "syncing to pause");
        Assert.True(app.SavedSettings.Paused); // still paused after a restart
        await app.Wpf.Ui(app.Tray.RefreshNow);
        Assert.Equal("Resume", await app.Wpf.Ui(() => main.PauseText.Text));

        File.WriteAllText(Path.Combine(app.Folder, "while-paused.txt"), "waits");
        await Task.Delay(1500); // the folder watcher would have sent it by now
        Assert.False(OnServer(nest, "while-paused.txt"), "A file was uploaded while syncing was paused.");

        await app.Press(main, "Resume");
        await app.WaitUntil(() => OnServer(nest, "while-paused.txt"), "the waiting file to upload after resuming");
        Assert.False(app.SavedSettings.Paused);

        await app.ClickIcon();
        var panel = await app.Showing<TrayPanel>("the tray panel");
        await app.Press(panel, "Pause");
        await app.WaitUntil(() => app.Tray.Session!.Status.Paused, "the tray panel's Pause to pause syncing");
        await app.Wpf.Ui(app.Tray.RefreshNow);
        await app.Press(panel, "Resume");
        await app.WaitUntil(() => !app.Tray.Session!.Status.Paused && !app.SavedSettings.Paused, "the tray panel's Resume to resume syncing");
        await app.WaitUntil(() => app.Tray.TrayIcon.ContextMenuStrip!.Items.OfType<System.Windows.Forms.ToolStripMenuItem>().Any(i => i.Text == "Pause syncing"),
            "the tray menu to offer pausing again");
    }

    [Fact]
    public async Task HistoryRestore_BringsBackAnOldVersion()
    {
        await using var nest = await TestServer.StartWithWebsiteAsync();
        await using var app = await TrayHarness.CreateAsync(nest);
        var notes = Path.Combine(app.Folder, "notes.txt");
        await File.WriteAllTextAsync(notes, "version one");
        await app.StartAsync();
        await app.WaitUntil(() => OnServer(nest, "notes.txt"), "the first version to reach the nest");
        var first = nest.Store.ReadManifest(null).Entries.Single(e => e.Path == "notes.txt").Hash;
        await File.WriteAllTextAsync(notes, "version two, longer");
        await app.WaitUntil(() => nest.Store.ReadManifest(null).Entries.Single(e => e.Path == "notes.txt").Hash != first, "the second version to reach the nest");

        var main = app.Tray.Window!;
        await app.Press(main, "History");
        await app.Press(main, "All files");
        await app.WaitUntil(() => main.FileList.Items.Cast<ServerFile>().Any(f => f.Path == "notes.txt"), "notes.txt in History");
        await app.Wpf.Ui(() => main.FileList.SelectedItem = main.FileList.Items.Cast<ServerFile>().Single(f => f.Path == "notes.txt"));
        await app.WaitUntil(() => main.VersionList.Items.Count == 1, "the older version of notes.txt");
        await app.Press(main, "Restore");

        await app.WaitUntil(() => main.RestoreMessage.IsVisible, "History to say it restored the file");
        Assert.StartsWith("Restored.", await app.Wpf.Ui(() => main.RestoreMessageText.Text));
        await app.WaitUntil(() => File.ReadAllText(notes) == "version one", "the old version to come back into the folder");
    }

    [Fact]
    public async Task FixBlocked_MassDelete_AllowDeletions()
    {
        await using var nest = await TestServer.StartWithWebsiteAsync();
        await using var app = await TrayHarness.CreateAsync(nest);
        for (var i = 0; i < 10; i++)
            File.WriteAllText(Path.Combine(app.Folder, $"f{i}.txt"), i.ToString(System.Globalization.CultureInfo.InvariantCulture));
        await app.StartAsync();
        await app.WaitUntil(() => nest.Store.ReadManifest(null).Entries.Count(e => !e.Deleted) == 10, "the ten files to reach the nest");
        await app.SyncedOnce();

        for (var i = 0; i < 5; i++)
            File.Delete(Path.Combine(app.Folder, $"f{i}.txt"));
        await app.WaitUntil(() => app.Tray.Session!.Status.BlockReason == BlockReason.MassDelete, "the deletions to be held back");
        await app.WaitUntil(() => app.Notifications.Items.Any(n => n.Title == "Deletions blocked"), "a notification about the held-back deletions");
        Assert.Equal(10, nest.Store.ReadManifest(null).Entries.Count(e => !e.Deleted)); // nothing deleted yet
        await app.Wpf.Ui(app.Tray.RefreshNow);
        Assert.Contains(app.Tray.TrayIcon.ContextMenuStrip!.Items.OfType<System.Windows.Forms.ToolStripMenuItem>(),
            i => i.Text == "Allow these deletions (5)…" && i.Available);

        app.Dialogs.When("f0.txt", MessageBoxResult.Yes);
        await app.Press(app.Tray.Window!, "Allow these deletions (5)…");
        var question = Assert.Single(app.Dialogs.Asked.Items);
        Assert.Equal("Pairnets – allow deletions?", question.Title);
        Assert.Contains("f4.txt", question.Text);
        await app.WaitUntil(() => nest.Store.ReadManifest(null).Entries.Count(e => e.Deleted) == 5, "the five deletions to reach the nest");
        await app.WaitUntil(() => app.Tray.Session!.Status.Status == RunnerStatus.Idle, "syncing to carry on");
    }

    [Fact]
    public async Task SettingsSave_RenamesThisComputer()
    {
        await using var nest = await TestServer.StartWithWebsiteAsync();
        var laptop = nest.MintKey("LAPTOP");
        var renamed = new TaskCompletionSource<(string Id, string Name)>(TaskCreationOptions.RunContinuationsAsynchronously);
        await using var other = new HubConnectionBuilder()
            .WithUrl(new Uri(nest.Url, "hub"), o =>
            {
                o.AccessTokenProvider = () => Task.FromResult<string?>(laptop.Key);
                o.Headers[PairnetsHeaders.DeviceId] = "LAPTOP";
            })
            .Build();
        other.On<string, string>("DeviceRenamed", (id, name) => renamed.TrySetResult((id, name)));
        await other.StartAsync();
        await using var app = await TrayHarness.CreateAsync(nest, name: "DESKTOP");
        await app.StartAsync();
        await app.SyncedOnce();

        await app.Menu("Settings…");
        var main = app.Tray.Window!;
        await app.WaitUntil(() => main.SettingsPage.Content is SettingsView, "the Settings page");
        var form = await app.Wpf.Ui(() => (SettingsView)main.SettingsPage.Content);
        await app.Type(form.DeviceBox, "RENAMED-PC");
        await app.Press(main, "Save");

        var (id, name) = await renamed.Task.WaitAsync(TimeSpan.FromSeconds(30)); // the other computer hears it from the nest
        Assert.Equal((app.Grant!.Id, "RENAMED-PC"), (id, name));
        Assert.Equal("RENAMED-PC", Auth(nest).GetDevice(app.Grant.Id)!.Name);
        await app.WaitUntil(() => app.Tray.Session?.Settings.DeviceName == "RENAMED-PC", "the app to carry on under the new name");
        Assert.Equal(("RENAMED-PC", app.Grant.Id), (app.SavedSettings.DeviceName, app.SavedSettings.DeviceId));
        Assert.Equal(MainPage.Overview, await app.Wpf.Ui(() => main.Page));
    }

    [Fact]
    public async Task SignOut_RemovesTheDeviceOnTheNest()
    {
        await using var nest = await TestServer.StartWithWebsiteAsync();
        await using var app = await TrayHarness.CreateAsync(nest);
        File.WriteAllText(Path.Combine(app.Folder, "keep-me.txt"), "stays");
        await app.StartAsync();
        await app.SyncedOnce();

        await app.Menu("Settings…");
        var main = app.Tray.Window!;
        app.Dialogs.When("Sign out of this computer?", MessageBoxResult.OK);
        await app.Press(main, "Sign out of this computer");

        await app.Showing<SettingsWindow>("the sign-in window after signing out");
        Assert.False(Auth(nest).GetDevice(app.Grant!.Id)!.IsActive); // its key no longer works on the nest
        var saved = app.SavedSettings;
        Assert.Equal((null, null, false), (saved.ProtectedToken, saved.DeviceId, saved.FirstRunCompleted));
        Assert.Null(app.Tray.Session);
        Assert.True(File.Exists(Path.Combine(app.Folder, "keep-me.txt")));
        Assert.DoesNotContain(app.Dialogs.Asked.Items, q => q.Text.StartsWith("Signed out here, but your nest could not be told", StringComparison.Ordinal));
    }

    [Fact]
    public async Task Reset_ForgetsEverythingAndShowsSignIn()
    {
        await using var nest = await TestServer.StartWithWebsiteAsync();
        await using var app = await TrayHarness.CreateAsync(nest, settings: s => s.StartWithWindows = true);
        File.WriteAllText(Path.Combine(app.Folder, "keep-me.txt"), "stays");
        await app.StartAsync();
        await app.SyncedOnce();
        var stateDir = PairnetsPaths.StateDirFor(app.Folder, app.Env.LocalDir);
        Assert.True(File.Exists(Path.Combine(stateDir, "state.db")));
        Assert.True(File.Exists(Path.Combine(app.Folder, PathRules.MarkerFileName)));
        var protectedKey = app.SavedSettings.ProtectedToken!;

        await app.Menu("Settings…");
        app.Dialogs.When("Reset Pairnets on this computer?", MessageBoxResult.OK);
        await app.Press(app.Tray.Window!, "Reset this app…");

        await app.Showing<SettingsWindow>("the sign-in window after the reset");
        Assert.False(Auth(nest).GetDevice(app.Grant!.Id)!.IsActive);
        Assert.False(File.Exists(app.Env.SettingsPath));
        Assert.False(Directory.Exists(stateDir));
        Assert.False(File.Exists(Path.Combine(app.Folder, PathRules.MarkerFileName)));
        Assert.True(File.Exists(Path.Combine(app.Folder, "keep-me.txt"))); // the files stay
        Assert.True(Directory.Exists(app.Env.LogsDir)); // and so do the logs
        Assert.Equal([protectedKey], app.Platform.FakeSecrets.Forgotten.Items);
        Assert.False(app.Platform.AutoStartChanges.Items[^1]);
        Assert.DoesNotContain(app.Dialogs.Asked.Items, q => q.Text.StartsWith("Pairnets was reset, with these notes", StringComparison.Ordinal));
    }

    [Fact]
    public async Task UpdateServer_AsksTheNestToUpdate()
    {
        var updateRequests = new Log<string>();
        await using var nest = await TestServer.StartWithWebsiteAsync(configureBuilder: b =>
            b.Services.AddSingleton<IStartupFilter>(new OldServer("0.9.0", updateRequests)));
        await using var app = await TrayHarness.CreateAsync(nest);
        await app.StartAsync();

        // An older server: the app asks by itself.
        var ask = await app.Showing<ServerUpdateWindow>("the app to offer updating the older server");
        await app.Press(ask, "Later");
        Assert.False(await app.Wpf.Ui(() => ask.IsVisible));
        Assert.Equal((null, false), (app.SavedSettings.SkippedServerVersion, app.SavedSettings.AutoUpdateServer));

        // "Update server…" on the overview asks again; this test nest has no updater, so it needs the one-time setup.
        var main = app.Tray.Window!;
        await app.Press(main, "Update server…");
        var update = await app.Showing<ServerUpdateWindow>("the server update window");
        await app.Press(update, "Update server");
        await app.WaitUntil(() => update.CommandPanel.IsVisible, "the one-time setup the nest needs");
        Assert.Equal(["POST /api/update"], updateRequests.Items);
        await app.Press(update, "Copy command");
        Assert.Contains("get.sh | sudo bash", Assert.Single(app.Dialogs.Copied.Items));
        await app.Press(update, "Close");

        await app.Press(main, "Update server…");
        var skip = await app.Showing<ServerUpdateWindow>("the server update window");
        await app.Press(skip, "Don't ask for this version");
        await app.WaitUntil(() => app.SavedSettings.SkippedServerVersion == "0.9.0", "the skipped version to be saved");

        await app.Press(main, "Update server…");
        var always = await app.Showing<ServerUpdateWindow>("the server update window");
        await app.Press(always, "From now on, update the server automatically");
        await app.Press(always, "Later");
        await app.WaitUntil(() => app.SavedSettings.AutoUpdateServer, "automatic server updates to be saved");
    }

    [Fact]
    public async Task AppUpdate_Download_OpensReleasePage()
    {
        await using var nest = await TestServer.StartWithWebsiteAsync();
        await using var feed = await FakeReleaseFeed.StartAsync("99.0.0");
        await using var app = await TrayHarness.CreateAsync(nest, environment: e => e with
        {
            UpdateDownloadBase = feed.DownloadBase,
            UpdateReleasePage = feed.ReleasePage,
        }); // not installed by the installer (a copy from the zip): it cannot update itself
        await app.StartAsync();

        await app.Menu("Settings…");
        var main = app.Tray.Window!;
        await app.Press(main, "Check now");
        await app.WaitUntil(() => main.UpdateCard.IsVisible, "the update card");
        Assert.Equal("Pairnets 99.0.0 is available", await app.Wpf.Ui(() => main.UpdateTitle.Text));
        Assert.Contains(app.Notifications.Items, n => n.Title == "Pairnets update available");

        await app.Press(main, "Download");
        Assert.Equal(feed.ReleasePage.ToString(), Assert.Single(app.Platform.Opened.Items));
        Assert.Empty(app.Platform.Installers.Items);
        Assert.DoesNotContain("/download/" + FakeReleaseFeed.AssetName, feed.Requests.Items);

        await app.Press(main, "Later");
        await app.WaitUntil(() => !main.UpdateCard.IsVisible, "the update card to go away");
    }

    [Fact]
    public async Task AppUpdate_UpdateNow_DownloadsVerifiesAndRunsInstaller()
    {
        await using var nest = await TestServer.StartWithWebsiteAsync();
        await using var feed = await FakeReleaseFeed.StartAsync();
        feed.Publish("99.0.0", tampered: true);
        await using var app = await TrayHarness.CreateAsync(nest, environment: e => e with
        {
            UpdateDownloadBase = feed.DownloadBase,
            UpdateReleasePage = feed.ReleasePage,
            InstallDir = AppContext.BaseDirectory, // put there by the installer
        });
        await app.StartAsync();
        await app.Menu("Settings…");
        var main = app.Tray.Window!;

        // A download that does not match its checksum is never run.
        await app.Press(main, "Check now");
        await app.WaitUntil(() => main.UpdateCard.IsVisible, "the update card");
        await app.Press(main, "Update now");
        await app.WaitUntil(() => main.UpdateTitle.Text == "Pairnets 99.0.0 could not be installed", "the app to refuse the tampered download");
        Assert.Contains("didn't match its checksum", await app.Wpf.Ui(() => main.UpdateDetail.Text));
        Assert.Equal("Open download page", await app.Wpf.Ui(() => main.UpdateButton.Content));
        Assert.Empty(app.Platform.Installers.Items);
        Assert.Equal(0, app.Shutdowns);

        // A good release: downloaded, checked, started, and Pairnets closes so the installer can replace it.
        feed.Publish("99.0.1");
        await app.Press(main, "Check now");
        await app.WaitUntil(() => main.UpdateTitle.Text == "Pairnets 99.0.1 is available", "the next release");
        await app.Press(main, "Update now");
        await app.WaitUntil(() => app.Platform.Installers.Count == 1, "the installer to start");
        var (setup, arguments) = app.Platform.Installers.Items[0];
        try
        {
            Assert.Equal("/SILENT /SUPPRESSMSGBOXES /NORESTART /CLOSEAPPLICATIONS", arguments);
            Assert.Equal(feed.Asset, await File.ReadAllBytesAsync(setup));
            Assert.Equal(1, app.Shutdowns);
            Assert.Contains("/download/" + FakeReleaseFeed.AssetName, feed.Requests.Items);
        }
        finally
        {
            File.Delete(setup);
        }
    }

    [Fact]
    public async Task AddComputer_ManageDevices_ViewLog_ReportBug()
    {
        await using var nest = await TestServer.StartWithWebsiteAsync();
        await using var app = await TrayHarness.CreateAsync(nest, name: "DESKTOP");
        await app.StartAsync();
        await app.SyncedOnce();
        await app.WaitUntil(() => app.Tray.Session!.Status.NestUrl == "https://localhost", "the app to learn the nest's own name");
        var main = app.Tray.Window!;

        await app.Press(main, "Devices");
        await app.WaitUntil(() => main.DevicesList.Items.Cast<DeviceRow>().Any(d => d.Title.Contains("DESKTOP", StringComparison.Ordinal)), "this computer in the Devices list");
        await app.Press(main, "+ Add a computer…");
        var steps = Assert.Single(app.Dialogs.Asked.Items);
        Assert.Contains("type localhost and press \"Sign in with your browser\"", steps.Text);
        await app.Press(main, "Manage devices on your nest");
        Assert.Equal("https://localhost/devices", app.Platform.Opened.Items[^1]);

        await app.Press(main, "More");
        await app.Press(main, "View log");
        var log = app.Platform.Opened.Items[^1];
        Assert.StartsWith(app.Env.LogsDir, log);
        Assert.True(File.Exists(log), "The log the app opens does not exist.");
        await app.Menu("View log");
        Assert.Equal(log, app.Platform.Opened.Items[^1]);

        await app.Press(main, "More");
        await app.Press(main, "Report a bug…");
        var report = await app.Showing<BugReportWindow>("the bug report window");
        await app.WaitUntil(() => report.OpenButton.IsEnabled, "the bug report to be built");
        var copied = Assert.Single(app.Dialogs.Copied.Items);
        Assert.StartsWith("Pairnets bug report", copied);
        Assert.Contains("DESKTOP", copied);
        Assert.DoesNotContain(app.Grant!.Key, copied); // never the key
        await app.Press(report, "Open file");
        var saved = app.Platform.Opened.Items[^1];
        Assert.StartsWith(app.Env.LogsDir, saved);
        Assert.Equal(copied, File.ReadAllText(saved));
        await app.Press(report, "Close");
        Assert.False(await app.Wpf.Ui(() => report.IsVisible));
    }

    [Fact]
    public async Task Quit_ShutsDown()
    {
        await using var nest = await TestServer.StartWithWebsiteAsync();
        await using var app = await TrayHarness.CreateAsync(nest);
        await app.StartAsync();

        await app.ClickIcon();
        var panel = await app.Showing<TrayPanel>("the tray panel");
        await app.Press(panel, "More");
        await app.Press(panel, "Exit Pairnets");
        Assert.Equal(1, app.Shutdowns);
        Assert.False(await app.Wpf.Ui(() => app.Tray.Window!.IsVisible || panel.IsVisible));

        await app.Menu("Exit");
        Assert.Equal(2, app.Shutdowns);
    }

    /// <summary>
    /// Makes the test nest look like an older release (its /api/info reports <paramref name="version"/>) and writes down
    /// every request to update it.
    /// </summary>
    private sealed class OldServer(string version, Log<string> updateRequests) : IStartupFilter
    {
        public Action<IApplicationBuilder> Configure(Action<IApplicationBuilder> next) => app =>
        {
            app.Use(async (ctx, call) =>
            {
                if (ctx.Request.Path.StartsWithSegments("/api/update") && HttpMethods.IsPost(ctx.Request.Method))
                    updateRequests.Add("POST /api/update");
                if (!ctx.Request.Path.Equals("/api/info"))
                {
                    await call(ctx);
                    return;
                }
                var body = ctx.Response.Body;
                using var buffer = new MemoryStream();
                ctx.Response.Body = buffer;
                await call(ctx);
                ctx.Response.Body = body;
                var json = JsonNode.Parse(buffer.ToArray())!;
                json["serverVersion"] = version;
                var bytes = System.Text.Encoding.UTF8.GetBytes(json.ToJsonString());
                ctx.Response.ContentLength = bytes.Length;
                await body.WriteAsync(bytes);
            });
            next(app);
        };
    }
}
