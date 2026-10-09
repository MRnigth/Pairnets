using System.Text.RegularExpressions;
using Microsoft.Playwright;

namespace Pairnets.Browser.Tests;

/// <summary>
/// One browser window on the nest's website, for one flow. It keeps every console error and uncaught page error (a
/// content-security-policy violation shows up as one too), answers the pages' confirm() questions, and has a passkey
/// authenticator built in (Chrome's virtual one: like Windows Hello, always says yes).
/// </summary>
public sealed partial class NestPage : IAsyncDisposable
{
    private readonly List<string> _problems = [];
    private readonly List<(int Status, string Path)> _allowedFailures = [];
    private readonly WebsiteFixture _site;
    private bool _dismissNext;

    private NestPage(string flow, IBrowserContext context, IPage page, WebsiteFixture site)
    {
        Flow = flow;
        Context = context;
        Page = page;
        _site = site;
        page.Console += (_, message) =>
        {
            if (message.Type == "error" && !Allowed(message))
                Problem($"console.error on {Path(page.Url)}: {message.Text} ({message.Location})");
        };
        page.PageError += (_, error) => Problem($"Uncaught error on {Path(page.Url)}: {error}");
        page.Dialog += async (_, dialog) =>
        {
            lock (Dialogs)
                Dialogs.Add(dialog.Message);
            if (_dismissNext)
            {
                _dismissNext = false;
                await dialog.DismissAsync();
            }
            else
            {
                await dialog.AcceptAsync();
            }
        };
    }

    public string Flow { get; }

    public IBrowserContext Context { get; }

    public IPage Page { get; }

    /// <summary>The questions the pages asked with confirm(), oldest first.</summary>
    public List<string> Dialogs { get; } = [];

    public static async Task<NestPage> CreateAsync(string flow, IBrowserContext context, IPage page, WebsiteFixture site)
    {
        var nest = new NestPage(flow, context, page, site);
        var cdp = await context.NewCDPSessionAsync(page);
        await cdp.SendAsync("WebAuthn.enable", new Dictionary<string, object> { ["enableUI"] = false });
        await cdp.SendAsync("WebAuthn.addVirtualAuthenticator", new Dictionary<string, object>
        {
            ["options"] = new Dictionary<string, object>
            {
                ["protocol"] = "ctap2",
                ["transport"] = "internal",
                ["hasResidentKey"] = true,
                ["hasUserVerification"] = true,
                ["isUserVerified"] = true,
                ["automaticPresenceSimulation"] = true,
            },
        });
        return nest;
    }

    /// <summary>A request that is meant to fail in this flow: the browser's "Failed to load resource" line for it is not an error.</summary>
    public void ExpectFailure(int status, string path)
    {
        lock (_allowedFailures)
            _allowedFailures.Add((status, path));
    }

    /// <summary>The next confirm() question is answered "Cancel".</summary>
    public void DismissNextQuestion() => _dismissNext = true;

    public async Task GotoAsync(string pathOrUrl) => await Page.GotoAsync(pathOrUrl);

    public Task ClickAsync(string selector) => Page.Locator(selector).ClickAsync();

    public Task FillAsync(string selector, string text) => Page.Locator(selector).FillAsync(text);

    public Task VisibleAsync(string selector) => Page.Locator(selector).WaitForAsync(new() { State = WaitForSelectorState.Visible });

    public Task HiddenAsync(string selector) => Page.Locator(selector).WaitForAsync(new() { State = WaitForSelectorState.Hidden });

    /// <summary>Waits until the address is <paramref name="pathAndQuery"/> ("/devices", "/link?code=ABCD-EFGH").</summary>
    public Task AtAsync(string pathAndQuery) =>
        Page.WaitForURLAsync(url => Uri.TryCreate(url, UriKind.Absolute, out var u) && u.PathAndQuery == pathAndQuery);

    /// <summary>Waits until <paramref name="selector"/> shows <paramref name="text"/> somewhere in it.</summary>
    public Task ShowsAsync(string selector, string text) =>
        Page.Locator(selector, new() { HasText = text }).First.WaitForAsync(new() { State = WaitForSelectorState.Visible });

    /// <summary>Opens a row's "⋯" menu on the Devices page and picks an item (again if the list refreshed itself meanwhile).</summary>
    public async Task DeviceMenuAsync(string device, string item)
    {
        for (var attempt = 1; ; attempt++)
        {
            var row = Page.Locator("#devices li").Filter(new() { Has = Page.Locator(".title", new() { HasTextRegex = new Regex("^" + Regex.Escape(device) + "$") }) });
            await row.Locator("button.icon").ClickAsync();
            try
            {
                await row.Locator(".items button", new() { HasText = item }).ClickAsync(new() { Timeout = 3_000 });
                return;
            }
            catch (Exception ex) when (ex is TimeoutException or PlaywrightException && attempt < 4)
            {
                // The page reloads its list every five seconds, which closes an open menu: open it again.
            }
        }
    }

    /// <summary>Fails the flow when anything went wrong in the browser.</summary>
    public void AssertNoProblems()
    {
        lock (_problems)
            Assert.True(_problems.Count == 0, $"The browser reported problems in \"{Flow}\":\n" + string.Join('\n', _problems));
    }

    /// <summary>Keeps a screenshot and the full trace (open it with: pwsh bin/…/playwright.ps1 show-trace &lt;file&gt;).</summary>
    public async Task KeepEvidenceAsync()
    {
        Directory.CreateDirectory(_site.Artifacts);
        var name = Regex.Replace(Flow, "[^A-Za-z0-9]+", "-") + "-" + DateTime.UtcNow.ToString("yyyyMMdd-HHmmss", System.Globalization.CultureInfo.InvariantCulture);
        try
        {
            await Page.ScreenshotAsync(new() { Path = System.IO.Path.Combine(_site.Artifacts, name + ".png"), FullPage = true });
        }
        catch (PlaywrightException)
        {
            // the page may be gone
        }
        await Context.Tracing.StopAsync(new() { Path = System.IO.Path.Combine(_site.Artifacts, name + "-trace.zip") });
        _traceStopped = true;
    }

    private bool _traceStopped;

    private bool Allowed(IConsoleMessage message)
    {
        var failed = FailedLoad().Match(message.Text);
        if (!failed.Success || !Uri.TryCreate(LineAndColumn().Replace(message.Location, string.Empty), UriKind.Absolute, out var url))
            return false;
        var status = int.Parse(failed.Groups[1].Value, System.Globalization.CultureInfo.InvariantCulture);
        lock (_allowedFailures)
            return _allowedFailures.Any(a => a.Status == status && url.AbsolutePath == a.Path);
    }

    private void Problem(string text)
    {
        lock (_problems)
            _problems.Add(text);
    }

    private static string Path(string url) => Uri.TryCreate(url, UriKind.Absolute, out var u) ? u.PathAndQuery : url;

    [GeneratedRegex(@"^Failed to load resource: the server responded with a status of (\d{3})")]
    private static partial Regex FailedLoad();

    /// <summary>The ":line:column" after the address in a console message's location.</summary>
    [GeneratedRegex(@":\d+:\d+$")]
    private static partial Regex LineAndColumn();

    public async ValueTask DisposeAsync()
    {
        if (!_traceStopped)
            await Context.Tracing.StopAsync();
        await Context.DisposeAsync();
    }
}
