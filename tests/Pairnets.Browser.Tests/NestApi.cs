using System.Net;
using System.Net.Http.Json;
using Microsoft.Playwright;
using Pairnets.Core;
using Pairnets.Server.Auth;
using Pairnets.Server.Web;
using Pairnets.Tests.E2E;
using Pairnets.Tests.Infrastructure;

namespace Pairnets.Browser.Tests;

/// <summary>
/// The tests' own way into the nest, beside the browser: it sets up what a page needs (computers, a password, a linked
/// email address or Google account) and checks through the API what each click changed on the server.
/// </summary>
public sealed class NestApi(IServerTarget target) : IDisposable
{
    public const string OwnerEmail = FakeGoogle.DefaultEmail;

    private readonly List<HttpClient> _clients = [];

    /// <summary>A computer with its own key.</summary>
    public sealed record Computer(string Id, string Name, string Key);

    public string Origin => target.WebsiteUrl.GetLeftPart(UriPartial.Authority);

    /// <summary>The owner's password as the tests last set it.</summary>
    public string Password { get; private set; } = string.Empty;

    /// <summary>A computer the tests read the device list with (it shows on the Devices page too).</summary>
    public Computer Watcher { get; private set; } = null!;

    public async Task InitializeAsync() => Watcher = await AddComputerAsync("WEBSITE-CHECK " + Tag());

    // ------------------------------------------------------------------ clients

    /// <summary>A website client of its own: cookies, and the Origin header the site's pages send.</summary>
    public HttpClient Web()
    {
        var http = new HttpClient(Localhost.Handler(new CookieContainer())) { BaseAddress = target.WebsiteUrl, Timeout = TimeSpan.FromSeconds(60) };
        http.DefaultRequestHeaders.Add("Origin", Origin);
        _clients.Add(http);
        return http;
    }

    /// <summary>An app on the API, with a key, the shared token, or nothing.</summary>
    public HttpClient App(string? key = null, string? device = null)
    {
        var http = new HttpClient(Localhost.Handler()) { BaseAddress = target.ApiUrl, Timeout = TimeSpan.FromSeconds(60) };
        if (key is not null)
            http.DefaultRequestHeaders.Add(PairnetsHeaders.Token, key);
        if (device is not null)
            http.DefaultRequestHeaders.Add(PairnetsHeaders.DeviceId, Uri.EscapeDataString(device));
        _clients.Add(http);
        return http;
    }

    // ------------------------------------------------------------------ signing in

    /// <summary>A fresh setup link from the server's command line (it cancels older unused ones).</summary>
    public async Task<string> OwnerLinkAsync()
    {
        var made = await target.RunCliAsync("owner-link");
        Assert.True(made.ExitCode == 0, "owner-link failed: " + made);
        var link = made.Output.Trim();
        Assert.StartsWith(Origin + "/setup#code=", link);
        return link;
    }

    /// <summary>A website client signed in with a setup link of its own.</summary>
    public async Task<HttpClient> SignedInAsync()
    {
        var link = await OwnerLinkAsync();
        var web = Web();
        await Expect(web.PostAsJsonAsync("web/api/setup", new { code = link[(link.IndexOf("#code=", StringComparison.Ordinal) + 6)..] }), HttpStatusCode.NoContent);
        return web;
    }

    /// <summary>Sets the owner's password the way a forgotten one is replaced: a fresh setup link, then a new password.</summary>
    public async Task SetPasswordAsync(string password)
    {
        await Expect((await SignedInAsync()).PostAsJsonAsync("web/api/password", new { password }), HttpStatusCode.NoContent);
        Password = password;
    }

    public void PasswordChanged(string password) => Password = password;

    public async Task<bool> PasswordWorksAsync(string password)
    {
        var response = await Web().PostAsJsonAsync("web/api/signin/password", new { password });
        return response.StatusCode == HttpStatusCode.NoContent;
    }

    /// <summary>What the server thinks of a browser: its session cookie, asked from outside the browser.</summary>
    public async Task<WebEndpoints.StateView> StateOfAsync(IBrowserContext context) =>
        (await (await AsBrowserAsync(context)).GetFromJsonAsync<WebEndpoints.StateView>("web/api/state", PairnetsJson.Options))!;

    /// <summary>The Security page's data as that browser sees it (its own session marked as current).</summary>
    public async Task<WebEndpoints.SecurityView> SecurityOfAsync(IBrowserContext context) =>
        (await (await AsBrowserAsync(context)).GetFromJsonAsync<WebEndpoints.SecurityView>("web/api/security", PairnetsJson.Options))!;

    /// <summary>A client that carries the browser's session cookie.</summary>
    private async Task<HttpClient> AsBrowserAsync(IBrowserContext context)
    {
        var cookie = (await context.CookiesAsync([target.WebsiteUrl.ToString()])).FirstOrDefault(c => c.Name == OwnerAuth.CookieName);
        var http = new HttpClient(Localhost.Handler()) { BaseAddress = target.WebsiteUrl, Timeout = TimeSpan.FromSeconds(60) };
        _clients.Add(http);
        if (cookie is not null)
            http.DefaultRequestHeaders.Add("Cookie", $"{cookie.Name}={cookie.Value}");
        return http;
    }

    public async Task<WebEndpoints.SecurityView> SecurityAsync() =>
        (await (await SignedInAsync()).GetFromJsonAsync<WebEndpoints.SecurityView>("web/api/security", PairnetsJson.Options))!;

    public async Task<WebEndpoints.DevicesView> WebDevicesAsync() =>
        (await (await SignedInAsync()).GetFromJsonAsync<WebEndpoints.DevicesView>("web/api/devices", PairnetsJson.Options))!;

    public async Task<IReadOnlyList<PasskeyEndpoints.PasskeyView>> PasskeysAsync() =>
        (await (await SignedInAsync()).GetFromJsonAsync<List<PasskeyEndpoints.PasskeyView>>("web/api/passkeys", PairnetsJson.Options))!;

    public async Task<ServerHello> HelloAsync() => (await App().GetFromJsonAsync<ServerHello>("api/hello", PairnetsJson.Options))!;

    // ------------------------------------------------------------------ computers

    /// <summary>A computer asks to join (no decision yet).</summary>
    public async Task<PairStartResponse> AskToJoinAsync(string name)
    {
        var response = await Expect(App().PostAsJsonAsync("api/pair/start", new PairStartRequest(name, "Windows", "1.0.99"), PairnetsJson.Options), HttpStatusCode.OK);
        return (await response.Content.ReadFromJsonAsync<PairStartResponse>(PairnetsJson.Options))!;
    }

    public async Task<PairPollResponse> PollAsync(string pollToken)
    {
        var response = await Expect(App().PostAsJsonAsync("api/pair/poll", new PairPollRequest(pollToken), PairnetsJson.Options), HttpStatusCode.OK);
        return (await response.Content.ReadFromJsonAsync<PairPollResponse>(PairnetsJson.Options))!;
    }

    /// <summary>A computer that asked, was allowed, collected its key and used it once (so it is listed).</summary>
    public async Task<Computer> AddComputerAsync(string name)
    {
        var start = await AskToJoinAsync(name);
        await Expect((await SignedInAsync()).PostAsync($"web/api/pair/{start.Code}/approve", null), HttpStatusCode.OK);
        var grant = await PollAsync(start.PollToken);
        Assert.Equal(PairPollResponse.Approved, grant.Status);
        await Expect(App(grant.Key, name).GetAsync("api/me"), HttpStatusCode.OK);
        return new Computer(grant.Id!, grant.Name!, grant.Key!);
    }

    /// <summary>An old app that only knows the shared token, seen once by the nest.</summary>
    public async Task OldAppSeenAsync(string name) => await Expect(App(target.Token, name).GetAsync("api/info"), HttpStatusCode.OK);

    public async Task<IReadOnlyList<DeviceInfo>> DevicesAsync() =>
        (await App(Watcher.Key, Watcher.Name).GetFromJsonAsync<List<DeviceInfo>>("api/devices", PairnetsJson.Options))!;

    /// <summary>How the API answers a key or the shared token: OK, or the error code it was turned away with.</summary>
    public async Task<string> AnswerToAsync(string key, string device)
    {
        var response = await App(key, device).GetAsync("api/info");
        return response.IsSuccessStatusCode ? "ok" : (await response.Content.ReadFromJsonAsync<ErrorBody>(PairnetsJson.Options))?.Code ?? response.StatusCode.ToString();
    }

    // ------------------------------------------------------------------ email and Google

    public async Task<int> MailCountAsync() => (await target.MailsAsync()).Count;

    /// <summary>Waits for a mail with a sign-in link to <paramref name="to"/> after the first <paramref name="before"/> mails.</summary>
    public async Task<RawMail> MailAfterAsync(int before, string to)
    {
        var deadline = DateTime.UtcNow.AddSeconds(30);
        while (true)
        {
            var mails = await target.MailsAsync();
            if (mails.Skip(before).LastOrDefault(m => m.Link.Length > 0 && m.To == to) is { } mail)
                return mail;
            if (DateTime.UtcNow > deadline)
                throw new TimeoutException($"No sign-in mail to {to} arrived within 30 s.");
            await Task.Delay(100);
        }
    }

    /// <summary>Confirms an address for email links the way the Security page does, with the link opened elsewhere.</summary>
    public async Task LinkEmailAsync(string address)
    {
        var before = await MailCountAsync();
        await Expect((await SignedInAsync()).PostAsJsonAsync("web/api/email", new { email = address }), HttpStatusCode.NoContent);
        await ConfirmAsync(await MailAfterAsync(before, address));
    }

    public async Task ConfirmAsync(RawMail mail) =>
        await Expect(Web().PostAsJsonAsync("web/api/signin/email/confirm", new { code = mail.Code }), HttpStatusCode.NoContent);

    /// <summary>Connects (the fake) Google the way the Security page does, following the redirects by hand.</summary>
    public async Task ConnectGoogleAsync()
    {
        var owner = await SignedInAsync();
        var toGoogle = (await Expect(owner.GetAsync("auth/google/start?purpose=connect"), HttpStatusCode.Redirect)).Headers.Location!;
        using var google = new HttpClient(new SocketsHttpHandler { UseProxy = false, AllowAutoRedirect = false });
        var back = (await Expect(google.GetAsync(toGoogle), HttpStatusCode.Redirect)).Headers.Location!;
        var done = await Expect(owner.GetAsync(back), HttpStatusCode.Redirect);
        Assert.Equal("/security?connected=google", done.Headers.Location!.ToString());
    }

    // ------------------------------------------------------------------ helpers

    public static string Tag() => Convert.ToHexString(System.Security.Cryptography.RandomNumberGenerator.GetBytes(2));

    public static string NewPassword() => $"browser password {Guid.NewGuid():N}"[..34];

    private static async Task<HttpResponseMessage> Expect(Task<HttpResponseMessage> call, HttpStatusCode status)
    {
        var response = await call;
        if (response.StatusCode != status)
            Assert.Fail($"{response.RequestMessage?.Method} {response.RequestMessage?.RequestUri?.AbsolutePath} answered {(int)response.StatusCode} instead of {(int)status}: {await response.Content.ReadAsStringAsync()}");
        return response;
    }

    public void Dispose()
    {
        foreach (var client in _clients)
            client.Dispose();
    }
}
