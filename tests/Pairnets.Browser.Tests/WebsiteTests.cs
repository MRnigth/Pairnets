using System.Text.RegularExpressions;
using Microsoft.Playwright;
using Pairnets.Core;
using Pairnets.Server.Web;
using Pairnets.Tests.E2E;
using Pairnets.Tests.Infrastructure;
using Xunit.Abstractions;
using static Microsoft.Playwright.Assertions;

namespace Pairnets.Browser.Tests;

/// <summary>
/// A real Chromium clicks through every page of the nest's website, the way an owner does, and the server is asked
/// after each click whether it really happened. The flows share one server and run in order; the last test checks
/// that every button, link, form and checkbox on the six pages was pressed, and every API call in nest.js was made.
/// </summary>
[TestCaseOrderer(StepOrderer.Name, StepOrderer.Assembly)]
public sealed partial class WebsiteTests(WebsiteFixture site, ITestOutputHelper output) : IClassFixture<WebsiteFixture>
{
    private const string Setup = "setup", SignIn = "sign in", Devices = "devices", Link = "link", Security = "security";
    private static readonly string[] AllFlows = [Setup, SignIn, Devices, Link, Security];
    private static readonly LocatorAssertionsToHaveCountOptions Patiently = new() { Timeout = 30_000 };

    private NestApi Api => site.Api;

    // ------------------------------------------------------------------ setup

    [Fact, Step(1)]
    public Task TheSetupPageTakesTheServersLinkAndSetsAPasswordOrAPasskey() => FlowAsync(Setup, async page =>
    {
        // The first visit, with the link the server prints.
        var link = await Api.OwnerLinkAsync();
        await OpenLinkAsync(page, link);
        await page.VisibleAsync("#step-method");
        Assert.Equal("/setup", new Uri(page.Page.Url).PathAndQuery); // the code leaves the address bar at once
        var password = NestApi.NewPassword();
        await page.FillAsync("#new-password", password);
        await page.FillAsync("#new-password-2", password);
        await page.ClickAsync("#new-password-submit");
        await page.AtAsync("/devices");
        Assert.True(await Api.PasswordWorksAsync(password), "The password chosen on the setup page does not sign in.");
        Api.PasswordChanged(password);

        // The same link again: it worked once.
        page.ExpectFailure(400, "/web/api/setup");
        await OpenLinkAsync(page, link);
        await page.VisibleAsync("#claim-missing");
        await page.ShowsAsync("#error", "already used");

        // A new link, and a passkey this time.
        var passkeys = (await Api.PasskeysAsync()).Count;
        await OpenLinkAsync(page, await Api.OwnerLinkAsync());
        await page.VisibleAsync("#passkey-block");
        await page.ClickAsync("#add-passkey");
        await page.AtAsync("/devices");
        Assert.Equal(passkeys + 1, (await Api.PasskeysAsync()).Count);

        // Signed in already: the page points on, to Security or to the devices.
        await page.GotoAsync("/setup");
        await page.VisibleAsync("#has-methods");
        await page.ClickAsync("#has-methods a[href='/security']");
        await page.AtAsync("/security");
        await page.GotoAsync("/setup");
        await page.ClickAsync("#skip");
        await page.AtAsync("/devices");
    });

    // ------------------------------------------------------------------ sign in

    [Fact, Step(2)]
    public Task TheSignInPageLetsTheOwnerInEveryWay() => FlowAsync(SignIn, async page =>
    {
        var password = NestApi.NewPassword();
        await Api.SetPasswordAsync(password);

        Step("with the password");
        await page.GotoAsync("/signin");
        await page.FillAsync("#password", password);
        await page.ClickAsync("#password-submit");
        await page.AtAsync("/devices");
        Assert.Equal("password", (await Api.StateOfAsync(page.Context)).SessionMethod);

        Step("with a passkey made on this computer");
        await page.GotoAsync("/security");
        await page.ShowsAsync("#password-state", "Set");
        var keys = page.Page.Locator("#methods li.passkey");
        var count = await keys.CountAsync();
        await page.ClickAsync("#add-passkey");
        await Expect(keys).ToHaveCountAsync(count + 1, Patiently);
        await page.ClickAsync("#signout");
        await page.AtAsync("/signin");
        Assert.False((await Api.StateOfAsync(page.Context)).SignedIn);
        await page.ClickAsync("#passkey-signin");
        await page.AtAsync("/devices");
        Assert.Equal("passkey", (await Api.StateOfAsync(page.Context)).SessionMethod);

        Step("with a link sent by email");
        await Api.LinkEmailAsync(NestApi.OwnerEmail);
        await page.ClickAsync("#signout");
        await page.AtAsync("/signin");
        var before = await Api.MailCountAsync();
        await page.ClickAsync("#email-signin");
        await page.ShowsAsync("#notice", "Check your inbox");
        var mail = await Api.MailAfterAsync(before, NestApi.OwnerEmail);
        await OpenLinkAsync(page, mail.Link);
        await page.ClickAsync("#link-signin");
        await page.AtAsync("/devices");
        Assert.Equal("email link", (await Api.StateOfAsync(page.Context)).SessionMethod);

        Step("the same email link again, and the page without a link");
        page.ExpectFailure(400, "/web/api/signin/email/confirm");
        await OpenLinkAsync(page, mail.Link);
        await page.ClickAsync("#link-signin");
        await page.ShowsAsync("#error", "already used");
        await page.GotoAsync("/email-link");
        await page.VisibleAsync("#link-missing");
        Assert.False(await page.Page.Locator("#link-signin").IsVisibleAsync());

        Step("with Google");
        await Api.ConnectGoogleAsync();
        await page.GotoAsync("/devices");
        await page.ClickAsync("#signout");
        await page.AtAsync("/signin");
        await page.ClickAsync("#google-signin");
        await page.AtAsync("/devices");
        Assert.Equal("google", (await Api.StateOfAsync(page.Context)).SessionMethod);
    });

    // ------------------------------------------------------------------ devices

    [Fact, Step(3)]
    public Task TheDevicesPageAddsRenamesRemovesForgetsAndReviews() => FlowAsync(Devices, async page =>
    {
        var tag = NestApi.Tag();
        var laptop = await Api.AddComputerAsync($"LAPTOP {tag}");
        var desktop = await Api.AddComputerAsync($"DESKTOP {tag}");
        var oldPc = $"OLD-PC {tag}";
        await Api.OldAppSeenAsync(oldPc);
        await SignInAsync(page);
        await page.GotoAsync("/devices");
        await page.ShowsAsync("#devices .title", laptop.Name);

        Step("the bar at the top");
        await page.ClickAsync("header a.brand");
        await page.AtAsync("/devices");
        await page.ClickAsync("header a[data-nav='devices']");
        await page.AtAsync("/devices");

        Step("Add a computer: the steps, and the download page in a new tab");
        await page.ClickAsync("#add");
        await page.VisibleAsync("#add-dialog");
        var download = await page.Context.RunAndWaitForPageAsync(() => page.ClickAsync("#add-dialog a[href='https://pairnets.app/add']"));
        await download.WaitForLoadStateAsync();
        Assert.Equal("https://pairnets.app/add", download.Url);
        await download.CloseAsync();
        await page.ClickAsync("#add-dialog button[data-close]");
        await page.HiddenAsync("#add-dialog");

        Step("renaming: Cancel changes nothing, Rename does");
        await page.DeviceMenuAsync(laptop.Name, "Rename…");
        await page.VisibleAsync("#rename-dialog");
        Assert.Equal(laptop.Name, await page.Page.Locator("#rename-input").InputValueAsync());
        await page.ClickAsync("#rename-form button[data-close]");
        await page.HiddenAsync("#rename-dialog");
        Assert.Contains(await Api.DevicesAsync(), d => d.Id == laptop.Id && d.Name == laptop.Name);
        var kitchen = $"KITCHEN {tag}";
        await page.DeviceMenuAsync(laptop.Name, "Rename…");
        await page.FillAsync("#rename-input", kitchen);
        await page.ClickAsync("#rename-form button[type='submit']");
        await page.ShowsAsync("#devices .title", kitchen);
        Assert.Contains(await Api.DevicesAsync(), d => d.Id == laptop.Id && d.Name == kitchen);

        Step("removing: Cancel keeps it, Remove takes its key away");
        await page.DeviceMenuAsync(desktop.Name, "Remove from Pairnets…");
        await page.ShowsAsync("#remove-name", desktop.Name);
        await page.ClickAsync("#remove-dialog button[data-close]");
        await page.HiddenAsync("#remove-dialog");
        Assert.Equal("ok", await Api.AnswerToAsync(desktop.Key, desktop.Name));
        await page.DeviceMenuAsync(desktop.Name, "Remove from Pairnets…");
        await page.ClickAsync("#remove-confirm");
        await Expect(Title(page, desktop.Name)).ToHaveCountAsync(0, Patiently);
        Assert.Equal(ErrorCodes.DeviceRemoved, await Api.AnswerToAsync(desktop.Key, desktop.Name));

        Step("forgetting an old app that only had the shared token");
        await page.DeviceMenuAsync(oldPc, "Forget this computer…");
        await page.ShowsAsync("#forget-name", oldPc);
        await page.ClickAsync("#forget-dialog button[data-close]");
        await page.HiddenAsync("#forget-dialog");
        Assert.Contains((await Api.WebDevicesAsync()).Devices, d => d.Name == oldPc);
        await page.DeviceMenuAsync(oldPc, "Forget this computer…");
        await page.ClickAsync("#forget-confirm");
        await Expect(Title(page, oldPc)).ToHaveCountAsync(0, Patiently);
        Assert.DoesNotContain((await Api.WebDevicesAsync()).Devices, d => d.Name == oldPc);

        Step("a computer asking to join shows up by itself, and is reviewed and let in");
        var tablet = await Api.AskToJoinAsync($"TABLET {tag}");
        var waiting = page.Page.Locator("#pending li", new() { HasText = $"TABLET {tag}" });
        await waiting.WaitForAsync(); // the list refreshes every five seconds
        await waiting.Locator("button", new() { HasText = "Review" }).ClickAsync();
        await page.AtAsync($"/link?code={tablet.Code}");
        await page.ShowsAsync("#request-name", $"TABLET {tag}");
        await page.ClickAsync("#allow");
        await page.ShowsAsync("#outcome", "is let in");
        var grant = await Api.PollAsync(tablet.PollToken);
        Assert.Equal(PairPollResponse.Approved, grant.Status);
        Assert.Equal("ok", await Api.AnswerToAsync(grant.Key!, grant.Name!)); // the app uses its new key, and so shows up in the list
        var backToApp = page.Page.Locator("#outcome-links a[href='pairnets://signed-in']");
        if (await backToApp.CountAsync() > 0)
            await backToApp.ClickAsync(); // only on the computer that asked: nothing here answers pairnets://
        await page.ClickAsync("#outcome-links a[href='/devices']");
        await page.AtAsync("/devices");
        await page.ShowsAsync("#devices .title", $"TABLET {tag}");

        Step("on to Security, and signing out");
        await page.ClickAsync("header a[data-nav='security']");
        await page.AtAsync("/security");
        await page.GotoAsync("/devices");
        await page.ClickAsync("#signout");
        await page.AtAsync("/signin");
        Assert.False((await Api.StateOfAsync(page.Context)).SignedIn);
    });

    // ------------------------------------------------------------------ approving a computer

    [Fact, Step(4)]
    public Task TheLinkPageTurnsAwayAndLetsIn() => FlowAsync(Link, async page =>
    {
        var tag = NestApi.Tag();
        await SignInAsync(page);

        Step("typing the code shown on the computer, then turning it away");
        var intruder = await Api.AskToJoinAsync($"INTRUDER {tag}");
        await page.GotoAsync("/link");
        await page.VisibleAsync("#enter-code");
        await page.FillAsync("#code", intruder.Code.ToLowerInvariant());
        await page.ClickAsync("#code-form button[type='submit']");
        await page.AtAsync($"/link?code={intruder.Code}");
        await page.ShowsAsync("#request-name", $"INTRUDER {tag}");
        await page.ClickAsync("#deny");
        await page.ShowsAsync("#outcome", "was not let in");
        Assert.Equal(PairPollResponse.Denied, (await Api.PollAsync(intruder.PollToken)).Status);

        Step("a code nobody is waiting with");
        page.ExpectFailure(404, "/web/api/pair/ZZZZ-ZZZZ");
        await page.GotoAsync("/link?code=ZZZZ-ZZZZ");
        await page.ShowsAsync("#error", "no computer waiting");
        await page.VisibleAsync("#enter-code");

        Step("letting one in, then back to the list");
        var phone = await Api.AskToJoinAsync($"PHONE {tag}");
        await page.GotoAsync($"/link?code={phone.Code}");
        await page.ClickAsync("#allow");
        await page.ShowsAsync("#outcome", "is let in");
        Assert.Equal(PairPollResponse.Approved, (await Api.PollAsync(phone.PollToken)).Status);
        await page.ClickAsync("#outcome-links a[href='/devices']");
        await page.AtAsync("/devices");

        Step("the bar at the top of this page");
        foreach (var (target, path) in new[] { ("header a.brand", "/devices"), ("header a[data-nav='devices']", "/devices"), ("header a[data-nav='security']", "/security") })
        {
            await page.GotoAsync("/link");
            await page.VisibleAsync("#enter-code");
            await page.ClickAsync(target);
            await page.AtAsync(path);
        }
        await page.GotoAsync("/link");
        await page.VisibleAsync("#enter-code");
        await page.ClickAsync("#signout");
        await page.AtAsync("/signin");
        Assert.False((await Api.StateOfAsync(page.Context)).SignedIn);
    });

    // ------------------------------------------------------------------ security

    [Fact, Step(5)]
    public Task TheSecurityPageChangesEveryWayIn() => FlowAsync(Security, async page =>
    {
        var password = NestApi.NewPassword();
        await Api.SetPasswordAsync(password);
        await SignInAsync(page);
        await page.GotoAsync("/security");
        await page.ShowsAsync("#password-state", "Set");

        Step("changing the password: Cancel, then for real");
        await page.ClickAsync("#password-change");
        await page.VisibleAsync("#password-dialog");
        await page.ClickAsync("#password-form button[data-close]");
        await page.HiddenAsync("#password-dialog");
        var changed = NestApi.NewPassword();
        await page.ClickAsync("#password-change");
        await page.FillAsync("#current-password", password);
        await page.FillAsync("#new-password", changed);
        await page.FillAsync("#new-password-2", changed);
        await page.ClickAsync("#password-form button[type='submit']");
        await page.HiddenAsync("#password-dialog");
        Assert.True(await Api.PasswordWorksAsync(changed), "The password changed on the Security page does not sign in.");
        Api.PasswordChanged(changed);

        Step("Google: disconnect, connect, and the way back to this page");
        if ((await Api.SecurityAsync()).GoogleEmail is null)
            await Api.ConnectGoogleAsync();
        await page.Page.ReloadAsync();
        await page.ClickAsync("#google-remove");
        await page.VisibleAsync("#google-connect");
        Assert.Null((await Api.SecurityAsync()).GoogleEmail);
        await page.ClickAsync("#google-connect");
        await page.AtAsync("/security");
        await page.ShowsAsync("#notice", "Google is connected");
        Assert.Equal(FakeGoogle.DefaultEmail, (await Api.SecurityAsync()).GoogleEmail);

        Step("email links: turn off, add an address, change it, turn off");
        if ((await Api.SecurityAsync()).EmailAddress is null)
            await Api.LinkEmailAsync(NestApi.OwnerEmail);
        await page.Page.ReloadAsync();
        await page.ClickAsync("#email-remove");
        await page.VisibleAsync("#email-form");
        Assert.Null((await Api.SecurityAsync()).EmailAddress);
        var before = await Api.MailCountAsync();
        await page.FillAsync("#email-input", NestApi.OwnerEmail);
        await page.ClickAsync("#email-send");
        await page.ShowsAsync("#notice", "We sent a confirmation link");
        await Api.ConfirmAsync(await Api.MailAfterAsync(before, NestApi.OwnerEmail));
        await page.Page.ReloadAsync();
        await page.ShowsAsync("#email-state", "o•••@example.com");
        await page.ClickAsync("#email-change");
        before = await Api.MailCountAsync();
        await page.FillAsync("#email-input", "second@example.com");
        await page.ClickAsync("#email-send");
        await page.ShowsAsync("#notice", "second@example.com");
        await Api.ConfirmAsync(await Api.MailAfterAsync(before, "second@example.com"));
        Assert.Equal("s•••@example.com", (await Api.SecurityAsync()).EmailAddress);
        await page.Page.ReloadAsync();
        await page.ClickAsync("#email-remove");
        await page.VisibleAsync("#email-form");
        Assert.Null((await Api.SecurityAsync()).EmailAddress);

        Step("a passkey: added, then removed");
        var keys = page.Page.Locator("#methods li.passkey");
        var count = await keys.CountAsync();
        await page.ClickAsync("#add-passkey");
        await Expect(keys).ToHaveCountAsync(count + 1, Patiently);
        var stored = (await Api.PasskeysAsync()).Count;
        await keys.First.Locator("button", new() { HasText = "Remove" }).ClickAsync();
        await Expect(keys).ToHaveCountAsync(count, Patiently);
        Assert.Equal(stored - 1, (await Api.PasskeysAsync()).Count);
        Assert.Contains(page.Dialogs, q => q.StartsWith("Remove the passkey", StringComparison.Ordinal));

        Step("removing the password (Google still lets the owner in), then setting one again");
        await page.ClickAsync("#password-remove");
        await page.ShowsAsync("#password-state", "Not set");
        Assert.False((await Api.HelloAsync()).Methods!.Password);
        await page.ClickAsync("#password-change");
        await page.HiddenAsync("#current-field");
        var again = NestApi.NewPassword();
        await page.FillAsync("#new-password", again);
        await page.FillAsync("#new-password-2", again);
        await page.ClickAsync("#password-form button[type='submit']");
        await page.HiddenAsync("#password-dialog");
        Assert.True(await Api.PasswordWorksAsync(again), "The password set again on the Security page does not sign in.");
        Api.PasswordChanged(again);
        await page.ClickAsync("#google-remove");
        await page.VisibleAsync("#google-connect");
        Assert.Null((await Api.SecurityAsync()).GoogleEmail);

        Step("the old shared token: Cancel keeps it, OK turns it off, and on again");
        var shared = page.Page.Locator("#shared-token");
        await Expect(shared).ToBeCheckedAsync();
        page.DismissNextQuestion();
        await page.ClickAsync("label.switch .track");
        await Expect(shared).ToBeCheckedAsync();
        Assert.Equal("ok", await Api.AnswerToAsync(site.Target.Token, "SHARED-CHECK"));
        await page.ClickAsync("label.switch .track");
        await Expect(shared).Not.ToBeCheckedAsync();
        Assert.Equal(ErrorCodes.SharedTokenOff, await Api.AnswerToAsync(site.Target.Token, "SHARED-CHECK"));
        await page.ClickAsync("label.switch .track");
        await Expect(shared).ToBeCheckedAsync();
        Assert.Equal("ok", await Api.AnswerToAsync(site.Target.Token, "SHARED-CHECK"));
        Assert.Contains(page.Dialogs, q => q.StartsWith("Turn off the old shared token", StringComparison.Ordinal));

        Step("signing out another browser");
        await Api.SignedInAsync(); // that other browser
        await page.Page.ReloadAsync();
        var others = page.Page.Locator("#sessions li button", new() { HasText = "Sign out" });
        await others.First.WaitForAsync();
        var sessions = (await Api.SecurityOfAsync(page.Context)).Sessions.Count;
        await others.First.ClickAsync();
        await Expect(page.Page.Locator("#sessions li")).ToHaveCountAsync(sessions - 1, Patiently);
        Assert.Equal(sessions - 1, (await Api.SecurityOfAsync(page.Context)).Sessions.Count);

        Step("the bar at the top of this page");
        foreach (var (target, path) in new[] { ("header a.brand", "/devices"), ("header a[data-nav='devices']", "/devices"), ("header a[data-nav='security']", "/security") })
        {
            await page.GotoAsync("/security");
            await page.ShowsAsync("#password-state", "Set");
            await page.ClickAsync(target);
            await page.AtAsync(path);
        }
        await page.ClickAsync("#signout");
        await page.AtAsync("/signin");
        Assert.False((await Api.StateOfAsync(page.Context)).SignedIn);
    });

    // ------------------------------------------------------------------ the guard

    [Fact, Step(99)]
    public async Task EveryButtonLinkFormAndApiCallOnTheWebsiteWasUsed()
    {
        if (site.Target is ProcessTarget process)
            process.Server.AssertNeverPrinted([site.Target.Token, Api.Watcher.Key, Api.Password]);

        var notRun = AllFlows.Where(f => !site.Log.FlowsDone.Contains(f)).ToList();
        if (notRun.Count > 0)
        {
            // Run alone, or after a flow failed (that failure is the one to fix first): nothing to compare yet.
            output.WriteLine("Not checked: these flows did not finish in this run: " + string.Join(", ", notRun));
            return;
        }

        var sources = WebUi.Pages.Values.Select(p => RepoPaths.Read("src", "Pairnets.Server", "WebUi", "pages", p + ".html")).ToArray();
        string[] inPages;
        await using (var page = await site.OpenAsync("inventory"))
        {
            await page.GotoAsync("/email-link");
            inPages = await page.Page.EvaluateAsync<string[]>(ClickLog.InventoryScript, sources);
        }
        Assert.True(inPages.Length > 30, $"Only {inPages.Length} buttons, links and forms were found in the pages; the inventory script is broken.");

        var used = site.Log.Used;
        var problems = new List<string>();
        foreach (var key in inPages.Distinct().Where(k => !used.Contains(k)).Order(StringComparer.Ordinal))
            problems.Add($"Nobody pressed this on the website: {key} (in the page's HTML). Add it to WebsiteTests.");
        foreach (var key in site.Log.Seen.Except(inPages).Where(k => !used.Contains(k) && !k.StartsWith("? ", StringComparison.Ordinal)).Order(StringComparer.Ordinal))
            problems.Add($"Nobody pressed this on the website: {key} (made by nest.js while the tests ran). Add it to WebsiteTests.");

        var requests = site.Log.ApiRequests;
        foreach (var (method, path, pattern) in ApiCalls())
        {
            if (!requests.Any(r => r.Method == method && pattern.IsMatch(r.Path)))
                problems.Add($"nest.js calls api(\"{method}\", \"{path}\"), but the browser never made that request. Add it to WebsiteTests.");
        }
        Assert.True(problems.Count == 0, string.Join('\n', problems));
        output.WriteLine($"{inPages.Distinct().Count()} elements in the pages, {site.Log.Seen.Count} seen, {used.Count} used, {ApiCalls().Count} API calls, {requests.Count} requests.");
    }

    /// <summary>
    /// Every <c>api("METHOD", path)</c> in nest.js, with the path as a pattern: a <c>"/devices/" + id</c> or a
    /// <c>`/pair/${code}`</c> part matches one path segment, and <c>${approve ? "approve" : "deny"}</c> gives one call each.
    /// </summary>
    private static List<(string Method, string Path, Regex Pattern)> ApiCalls()
    {
        var script = RepoPaths.Read("src", "Pairnets.Server", "WebUi", "assets", "nest.js");
        var calls = new List<(string, string, Regex)>();
        foreach (Match call in ApiCall().Matches(script))
        {
            var method = call.Groups["method"].Value;
            var path = call.Groups["literal"].Success
                ? call.Groups["literal"].Value + (call.Groups["joined"].Success ? "{…}" : string.Empty)
                : call.Groups["template"].Value;
            foreach (var variant in Choices(path))
            {
                var pattern = "^" + string.Join("[^/]+", Placeholder().Split(variant).Select(Regex.Escape)) + "$";
                calls.Add((method, variant, new Regex(pattern)));
            }
        }
        Assert.True(calls.Count > 20, $"Only {calls.Count} api(...) calls were found in nest.js; the pattern that reads it is broken.");
        return calls.DistinctBy(c => (c.Item1, c.Item2)).ToList();
    }

    /// <summary>`${x ? "a" : "b"}` becomes two paths, one with "a" and one with "b".</summary>
    private static IEnumerable<string> Choices(string path)
    {
        var choice = Ternary().Match(path);
        if (!choice.Success)
            return [Placeholder().Replace(path, "{…}")];
        return Choices(path[..choice.Index] + choice.Groups[1].Value + path[(choice.Index + choice.Length)..])
            .Concat(Choices(path[..choice.Index] + choice.Groups[2].Value + path[(choice.Index + choice.Length)..]));
    }

    [GeneratedRegex(@"api\(\s*""(?<method>GET|POST|PUT|PATCH|DELETE)""\s*,\s*(?:""(?<literal>[^""]*)""(?<joined>\s*\+)?|`(?<template>[^`]*)`)")]
    private static partial Regex ApiCall();

    [GeneratedRegex(@"\$\{[^}]*\?\s*""([^""]*)""\s*:\s*""([^""]*)""\s*\}")]
    private static partial Regex Ternary();

    [GeneratedRegex(@"\$\{[^}]*\}|\{…\}")]
    private static partial Regex Placeholder();

    // ------------------------------------------------------------------ helpers

    /// <summary>Runs a flow in a window of its own; on failure its screenshot and trace are kept.</summary>
    private async Task FlowAsync(string flow, Func<NestPage, Task> steps)
    {
        await using var page = await site.OpenAsync(flow);
        Step($"{flow} (on {site.Target.Name})");
        try
        {
            await steps(page);
            page.AssertNoProblems();
        }
        catch
        {
            await page.KeepEvidenceAsync();
            output.WriteLine($"The screenshot and trace of \"{flow}\" are in {site.Artifacts}");
            throw;
        }
        site.Log.FlowDone(flow);
    }

    private void Step(string what) => output.WriteLine($"{DateTime.Now:HH:mm:ss.fff}  {what}");

    /// <summary>Signs the window in the way the owner gets back in: a fresh setup link from the server.</summary>
    private async Task SignInAsync(NestPage page)
    {
        await OpenLinkAsync(page, await Api.OwnerLinkAsync());
        await page.VisibleAsync("#step-method");
    }

    /// <summary>Opens a link from a mail or the command line as a fresh page load (a link to the same page would only change the #part).</summary>
    private static async Task OpenLinkAsync(NestPage page, string link)
    {
        await page.Page.GotoAsync("about:blank");
        await page.Page.GotoAsync(link);
    }

    private static ILocator Title(NestPage page, string name) =>
        page.Page.Locator("#devices .title", new() { HasTextRegex = new Regex("^" + Regex.Escape(name) + "$") });
}
