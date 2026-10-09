using System.Net;
using System.Net.Http.Json;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Threading;
using Microsoft.AspNetCore.Builder;
using Microsoft.Extensions.DependencyInjection;
using Pairnets.Core;
using Pairnets.Core.Api;
using Pairnets.Core.Client;
using Pairnets.Core.Settings;
using Pairnets.Core.Sync;
using Pairnets.Desktop;
using Pairnets.Desktop.Views;
using Pairnets.Server.Auth;
using Pairnets.Server.Storage;
using Pairnets.Tests.Infrastructure;

namespace Pairnets.Tests.Ui;

/// <summary>
/// The real Mac/Linux app (<see cref="DesktopController"/>) on a test desktop: folders, settings, logs and a "come
/// to the front" pipe of its own, secrets in memory, a real nest with its website, and a local release feed.
/// Disposing it closes the app and fails when its log shows an unhandled error.
/// </summary>
public sealed class AppUnderTest : IAsyncDisposable
{
    private AppUnderTest(TestServer nest, FakeReleaseFeed feed, TempDir root)
    {
        Nest = nest;
        Feed = feed;
        Root = root;
        Env = ClientEnvironment.Isolated(root.Path, feed.DownloadBase) with { UpdateReleasePage = feed.ReleasePage };
        Folder = root.Combine("Work");
    }

    public TestServer Nest { get; }

    public FakeReleaseFeed Feed { get; }

    public TempDir Root { get; }

    public ClientEnvironment Env { get; }

    public FakeLifetime Desktop { get; } = new();

    public FakePlatform Platform { get; } = new();

    public DesktopController App { get; private set; } = null!;

    /// <summary>This computer on the nest, when the app starts signed in.</summary>
    public DeviceKeyGrant? Device { get; private set; }

    /// <summary>The synced folder (created when the app starts signed in).</summary>
    public string Folder { get; }

    /// <summary>
    /// Starts the app. Signed in, it syncs <see cref="Folder"/> with the nest as "MACBOOK" with a key of its own;
    /// otherwise it starts at the sign-in window, as on a new computer.
    /// </summary>
    public static async Task<AppUnderTest> StartAsync(bool signedIn = true, Dictionary<string, string?>? nestConfig = null,
        Action<WebApplicationBuilder>? configureNest = null, string? releaseVersion = null)
    {
        var nest = await TestServer.StartWithWebsiteAsync(nestConfig, configureNest);
        var feed = await FakeReleaseFeed.StartAsync(releaseVersion ?? PairnetsInfo.ProductVersion);
        var app = new AppUnderTest(nest, feed, new TempDir("app"));
        if (signedIn)
        {
            app.Device = nest.MintKey("MACBOOK");
            Directory.CreateDirectory(app.Folder);
            SettingsStore.Save(app.Env.SettingsPath, new ClientSettings
            {
                ServerUrl = nest.Url.ToString(),
                ProtectedToken = app.Platform.Secrets.Protect(app.Device.Key),
                DeviceId = app.Device.Id,
                DeviceName = app.Device.Name,
                Folder = app.Folder,
                FirstRunCompleted = true,
            });
        }
        app.App = new DesktopController(Application.Current!, app.Desktop, app.Platform, app.Env);
        app.App.Start([]);
        Dispatcher.UIThread.RunJobs();
        return app;
    }

    /// <summary>The main window, which must be open.</summary>
    public MainWindow MainWindow => App.Window is { IsVisible: true } window ? window : throw new Xunit.Sdk.XunitException("The main window is not open.");

    /// <summary>Waits for a window with this title (a new one, when <paramref name="not"/> is the one shown before).</summary>
    public Task<Window> WindowAsync(string title, Window? not = null) =>
        Wait.For(() => Desktop.Windows.LastOrDefault(w => w.IsVisible && w.Title == title && w != not), $"the window \"{title}\" to open");

    /// <summary>Waits for one of the app's questions or messages (a plain window with buttons).</summary>
    public Task<Window> DialogAsync(string title) =>
        Wait.For(() => Desktop.Windows.LastOrDefault(w => w.IsVisible && w.Title == title && w.GetType() == typeof(Window)), $"the question \"{title}\" to show");

    /// <summary>Answers a question by clicking its button, and waits for it to close.</summary>
    public async Task AnswerAsync(string title, string button)
    {
        var dialog = await DialogAsync(title);
        Person.Click(Screen.Find<Button>(dialog, button));
        await Wait.Until(() => !dialog.IsVisible, $"the question \"{title}\" to close after \"{button}\"");
    }

    /// <summary>What a question or message says.</summary>
    public static string TextOf(Window dialog) => string.Join(Environment.NewLine, Screen.All<SelectableTextBlock>(dialog).Select(t => t.Text));

    public Task WaitUntilIdleAsync() =>
        Wait.Until(() => App.Session?.Status.Status == RunnerStatus.Idle, "syncing to settle (\"Up to date\")", seconds: 30);

    /// <summary>A sidebar page of the main window, clicked as a person would.</summary>
    public void Open(string page) => Person.Click(Screen.Find<RadioButton>(MainWindow, page));

    /// <summary>The Settings page's form (opens it).</summary>
    public async Task<SettingsView> SettingsPageAsync()
    {
        Open("Settings");
        return await Wait.For(() => Screen.All<SettingsView>(MainWindow).FirstOrDefault(), "the Settings page to show");
    }

    /// <summary>An item of the tray / menu-bar menu, as it reads now.</summary>
    public NativeMenuItem TrayItem(string header)
    {
        App.RefreshNow();
        return App.TrayMenu?.Items.OfType<NativeMenuItem>().FirstOrDefault(i => i.Header == header)
            ?? throw new CannotPressException($"The tray menu has no \"{header}\". It has: "
                + string.Join(", ", App.TrayMenu?.Items.OfType<NativeMenuItem>().Select(i => "\"" + i.Header + "\"") ?? []));
    }

    /// <summary>The files on the nest that are not deleted, with their sizes.</summary>
    public async Task<Dictionary<string, long>> NestFilesAsync()
    {
        using var api = Nest.Client("observer");
        var manifest = await api.GetManifestAsync(null, CancellationToken.None);
        return manifest.Entries.Where(e => !e.Deleted).ToDictionary(e => e.Path, e => e.Size);
    }

    public async Task WaitForNestAsync(Func<Dictionary<string, long>, bool> condition, string what)
    {
        var deadline = DateTime.UtcNow.AddSeconds(30);
        Dictionary<string, long> files;
        while (!condition(files = await NestFilesAsync()))
        {
            if (DateTime.UtcNow > deadline)
                throw new Xunit.Sdk.XunitException($"Waited 30 seconds, but {what} did not happen. The nest has: {string.Join(", ", files.Keys)}");
            await Task.Delay(100);
        }
    }

    /// <summary>The computers on the nest (removed ones left out).</summary>
    public IReadOnlyList<PairedDevice> NestDevices => Nest.Services.GetRequiredService<DeviceKeys>().Store.ListDevices();

    /// <summary>The nest's owner, signed in on the nest's website.</summary>
    public async Task<HttpClient> OwnerAsync()
    {
        var web = Nest.WebBrowser();
        var code = Nest.Services.GetRequiredService<AuthStore>().CreateSetupCode();
        var setup = await web.PostAsJsonAsync("web/api/setup", new { code });
        Assert.Equal(HttpStatusCode.NoContent, setup.StatusCode);
        return web;
    }

    /// <summary>
    /// The owner allows a computer on the nest's website (HTTPS on the nest's name). Some Windows machines cannot
    /// open the test website's TLS at all (the HTTPS website tests fail there too); only then is the same decision
    /// made in the nest's store, which is what the website's Allow button does.
    /// </summary>
    public async Task ApproveOnNestAsync(string code)
    {
        try
        {
            using var owner = await OwnerAsync();
            Assert.Equal(HttpStatusCode.OK, (await owner.PostAsync($"web/api/pair/{code}/approve", null)).StatusCode);
        }
        catch (HttpRequestException ex) when (ex.InnerException is IOException or System.Security.Authentication.AuthenticationException)
        {
            var auth = Nest.Services.GetRequiredService<AuthStore>();
            var request = auth.FindPairRequestByCode(code) ?? throw new Xunit.Sdk.XunitException($"No computer waits on the nest with the code {code}.");
            Assert.True(auth.DecidePairRequest(request.Id, approve: true, "owner"));
        }
    }

    /// <summary>The owner's email and Google account, so the sign-in window offers those buttons.</summary>
    public void OwnerUsesGoogleAndEmail()
    {
        var auth = Nest.Services.GetRequiredService<AuthStore>();
        auth.SetOwnerEmail("owner@example.com");
        auth.SetGoogleAccount("e2e-google-user", "owner@example.com");
    }

    /// <summary>The nest configuration that turns on Google and email sign-in (with the owner's accounts set).</summary>
    public static Dictionary<string, string?> GoogleAndEmail => new()
    {
        ["Sync:GoogleClientId"] = "client-123.apps.googleusercontent.com",
        ["Sync:GoogleClientSecret"] = "test-oauth-secret",
        ["Sync:SmtpHost"] = "127.0.0.1",
        ["Sync:SmtpFrom"] = "nest@example.com",
    };

    /// <summary>Answers the folder picker with <paramref name="folder"/> until disposed.</summary>
    public static IDisposable Picking(string folder)
    {
        var before = Dialogs.FolderPicker;
        Dialogs.FolderPicker = (_, _) => Task.FromResult<string?>(folder);
        return new Restore(() => Dialogs.FolderPicker = before);
    }

    private sealed class Restore(Action undo) : IDisposable
    {
        public void Dispose() => undo();
    }

    public async ValueTask DisposeAsync()
    {
        foreach (var window in Desktop.Windows.Reverse())
        {
            if (window is MainWindow main)
                main.AllowClose = true;
            window.Close();
        }
        Dispatcher.UIThread.RunJobs();
        App.Dispose();
        Desktop.Dispose();
        var crashes = Directory.Exists(Env.LogsDir)
            ? Directory.EnumerateFiles(Env.LogsDir, "pairnets-*.log").SelectMany(File.ReadAllLines).Where(l => l.Contains("Unhandled UI exception", StringComparison.Ordinal)).ToList()
            : [];
        var log = crashes.Count > 0 ? string.Join(Environment.NewLine, Directory.EnumerateFiles(Env.LogsDir, "pairnets-*.log").Select(File.ReadAllText)) : null;
        await Nest.DisposeAsync();
        await Feed.DisposeAsync();
        Root.Dispose();
        if (log is not null)
            throw new Xunit.Sdk.XunitException("The app hit an unhandled error (see \"Unhandled UI exception\"):" + Environment.NewLine + log);
    }
}
