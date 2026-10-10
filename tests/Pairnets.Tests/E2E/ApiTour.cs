using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.AspNetCore.Http.Connections;
using Pairnets.Core;
using Pairnets.Server.Auth.WebAuthn;
using Pairnets.Server.Web;
using Pairnets.Tests.Infrastructure;

namespace Pairnets.Tests.E2E;

/// <summary>
/// One walk through everything the server offers, in the order a real nest sees it: the first visit and the setup
/// link, every way to sign in, computers asking to join, files going up and down, the push channel, renaming and
/// removing computers, the maintenance commands, and last "Update server". It runs against any
/// <see cref="IServerTarget"/> and checks each answer and what it changed.
/// <para>
/// What it covers is written down in <see cref="Routes"/>, <see cref="HubMethods"/>, <see cref="HubEvents"/>,
/// <see cref="CliForms"/>, <see cref="CliWords"/> and <see cref="CliOptions"/>. <see cref="TourCoverageTests"/>
/// holds those lists against the server (so nothing new goes untested), and <see cref="ApiTourTests"/> holds them
/// against what the walk really did.
/// </para>
/// </summary>
public sealed partial class ApiTour : IAsyncDisposable
{
    // ------------------------------------------------------------------ what the tour covers

    public static readonly IReadOnlyList<Route> Routes =
    [
        // The apps' API (Web/Endpoints.cs).
        new("GET", "/api/health"),
        new("GET", "/api/hello"),
        new("GET", "/api/me"),
        new("DELETE", "/api/devices/{id}"),
        new("PATCH", "/api/devices/me"),
        new("GET", "/api/info"),
        new("POST", "/api/update"),
        new("GET", "/api/devices"),
        new("GET", "/api/update/diagnostics"),
        new("GET", "/api/manifest"),
        new("GET", "/api/file"),
        new("PUT", "/api/file"),
        new("POST", "/api/upload"),
        new("PUT", "/api/upload/{id}"),
        new("GET", "/api/upload/{id}"),
        new("POST", "/api/upload/{id}/commit"),
        new("DELETE", "/api/upload/{id}"),
        new("DELETE", "/api/file"),
        new("GET", "/api/history"),
        new("POST", "/api/history/restore"),
        new(Route.AnyMethod, "/hub/negotiate"),
        new(Route.AnyMethod, "/hub"),

        // Adding a computer (Web/PairingEndpoints.cs).
        new("POST", "/api/pair/start"),
        new("POST", "/api/pair/poll"),

        // The Pairnets service's signed calls to a nest linked to an account (Web/RelayEndpoints.cs).
        new("GET", "/api/relay/status"),
        new("GET", "/api/relay/devices"),
        new("POST", "/api/relay/devices"),
        new("DELETE", "/api/relay/devices/{id}"),

        // The website's pages (Web/WebUi.cs).
        new("GET", "/"),
        new("GET", "/setup"),
        new("GET", "/signin"),
        new("GET", "/link"),
        new("GET", "/devices"),
        new("GET", "/security"),
        new("GET", "/email-link"),
        new("GET", "/assets/{file}"),

        // The website's API (Web/WebEndpoints.cs).
        new("GET", "/web/api/state"),
        new("POST", "/web/api/setup"),
        new("POST", "/web/api/signin/password"),
        new("POST", "/web/api/signout"),
        new("GET", "/web/api/devices"),
        new("DELETE", "/web/api/devices/{id}"),
        new("PATCH", "/web/api/devices/{id}"),
        new("POST", "/web/api/devices/forget"),
        new("GET", "/web/api/pair/{code}"),
        new("POST", "/web/api/pair/{code}/approve"),
        new("POST", "/web/api/pair/{code}/deny"),
        new("GET", "/web/api/security"),
        new("POST", "/web/api/password"),
        new("DELETE", "/web/api/password"),
        new("DELETE", "/web/api/sessions/{id}"),
        new("POST", "/web/api/shared-token"),

        // Passkeys (Web/PasskeyEndpoints.cs).
        new("POST", "/web/api/passkeys/register/options"),
        new("POST", "/web/api/passkeys/register"),
        new("GET", "/web/api/passkeys"),
        new("DELETE", "/web/api/passkeys/{id}"),
        new("POST", "/web/api/signin/passkey/options"),
        new("POST", "/web/api/signin/passkey"),

        // Email links and Google (Web/LinkedSignInEndpoints.cs).
        new("POST", "/web/api/signin/email/request"),
        new("POST", "/web/api/signin/email/confirm"),
        new("POST", "/web/api/email"),
        new("DELETE", "/web/api/email"),
        new("DELETE", "/web/api/google"),
        new("GET", "/auth/google/start"),
        new("GET", "/auth/google/callback"),
    ];

    /// <summary>The push channel's methods an app calls.</summary>
    public static readonly IReadOnlyList<string> HubMethods = [nameof(SyncHub.BatchStarted), nameof(SyncHub.BatchFinished)];

    /// <summary>The push channel's messages the server sends.</summary>
    public static readonly IReadOnlyList<string> HubEvents =
    [
        SyncHub.ChangedMethod,
        SyncHub.PeerBatchMethod,
        SyncHub.DeviceRemovedMethod,
        SyncHub.DeviceRenamedMethod,
        PairingEndpoints.PairRequestedMethod,
        PairingEndpoints.PairDecidedMethod,
    ];

    /// <summary>The command lines of the server's usage text (see <see cref="CliUsage"/>).</summary>
    public static readonly IReadOnlyList<string> CliForms =
    [
        CliUsage.RunForm,
        "rescan",
        "rescan --dry-run",
        "history list <path>",
        "history restore <path> <id>",
        "history purge",
        "history purge --dry-run",
        "devices list",
        "devices remove <name or id>",
        "owner-link",
        "owner-link --if-new",
        "--version",
    ];

    /// <summary>Every first word the program takes as a command (CliCommands.IsCliCommand).</summary>
    public static readonly IReadOnlyList<string> CliWords = ["rescan", "history", "devices", "owner-link", "--help", "-h", "help", "--version"];

    /// <summary>The options of the usage text.</summary>
    public static readonly IReadOnlyList<string> CliOptions = ["--data-dir"];

    // ------------------------------------------------------------------ the walk's state

    private const string OwnerEmail = FakeGoogle.DefaultEmail;

    private readonly IServerTarget _target;
    private readonly Action<string> _log;
    private readonly List<HttpClient> _clients = [];
    private readonly List<HubProbe> _hubs = [];
    private readonly List<string> _secrets = [];
    private readonly string _tag = Convert.ToHexString(System.Security.Cryptography.RandomNumberGenerator.GetBytes(2));
    private HttpClient _owner = null!;

    public ApiTour(IServerTarget target, Action<string> log)
    {
        _target = target;
        _log = log;
        Record = new TourRecord(Routes);
        _secrets.Add(target.Token);
    }

    /// <summary>What the walk really did.</summary>
    public TourRecord Record { get; }

    /// <summary>The steps that ran, in order (for the report and for a failure).</summary>
    public List<string> Steps { get; } = [];

    /// <summary>"https://localhost:15443": the nest's own name, as links and passkeys see it.</summary>
    private string Origin => _target.WebsiteUrl.GetLeftPart(UriPartial.Authority);

    private readonly System.Diagnostics.Stopwatch _clock = System.Diagnostics.Stopwatch.StartNew();

    private void Step(string what)
    {
        Steps.Add(what);
        _log($"[{_target.Name}] {_clock.Elapsed.TotalSeconds,5:0.0} s  {what}");
    }

    // ------------------------------------------------------------------ clients

    /// <summary>An app talking to the API: with a key or the shared token, or with nothing at all.</summary>
    private HttpClient Api(string? key = null, string? device = null)
    {
        var http = new HttpClient(new RecordingHandler(Record, Localhost.Handler())) { BaseAddress = _target.ApiUrl, Timeout = TimeSpan.FromSeconds(60) };
        if (key is not null)
            http.DefaultRequestHeaders.Add(PairnetsHeaders.Token, key);
        if (device is not null)
            http.DefaultRequestHeaders.Add(PairnetsHeaders.DeviceId, Uri.EscapeDataString(device));
        http.DefaultRequestHeaders.Add(PairnetsHeaders.Client, PairnetsInfo.ClientDescription);
        _clients.Add(http);
        return http;
    }

    /// <summary>A browser on the nest's website: its own cookies, and the Origin header the site's own pages send.</summary>
    private HttpClient Browser(string? origin = "self")
    {
        var http = new HttpClient(new RecordingHandler(Record, Localhost.Handler(new CookieContainer()))) { BaseAddress = _target.WebsiteUrl, Timeout = TimeSpan.FromSeconds(60) };
        if (origin is not null)
            http.DefaultRequestHeaders.Add("Origin", origin == "self" ? Origin : origin);
        http.DefaultRequestHeaders.UserAgent.ParseAdd("Mozilla/5.0 (Windows NT 10.0; Win64; x64) Chrome/130.0 Safari/537.36");
        _clients.Add(http);
        return http;
    }

    /// <summary>Sends a request and insists on the answer's status; the failure message says what was asked and what came back.</summary>
    private static async Task<HttpResponseMessage> Call(HttpClient http, HttpMethod method, string url, object? json = null,
        HttpStatusCode expect = HttpStatusCode.OK, Action<HttpRequestMessage>? tweak = null)
    {
        using var request = new HttpRequestMessage(method, url);
        if (json is not null)
            request.Content = JsonContent.Create(json, json.GetType(), options: PairnetsJson.Options);
        tweak?.Invoke(request);
        var response = await http.SendAsync(request);
        if (response.StatusCode != expect)
        {
            var body = await response.Content.ReadAsStringAsync();
            Assert.Fail($"{method} {url} answered {(int)response.StatusCode} {response.StatusCode} instead of {(int)expect} {expect}. {Shorten(body)}");
        }
        return response;
    }

    private static Task<HttpResponseMessage> Get(HttpClient http, string url, HttpStatusCode expect = HttpStatusCode.OK) =>
        Call(http, HttpMethod.Get, url, expect: expect);

    private static Task<HttpResponseMessage> Post(HttpClient http, string url, object? json = null, HttpStatusCode expect = HttpStatusCode.OK) =>
        Call(http, HttpMethod.Post, url, json, expect);

    private static Task<HttpResponseMessage> Delete(HttpClient http, string url, HttpStatusCode expect = HttpStatusCode.NoContent) =>
        Call(http, HttpMethod.Delete, url, expect: expect);

    private static async Task<T> Read<T>(Task<HttpResponseMessage> call) =>
        (await (await call).Content.ReadFromJsonAsync<T>(PairnetsJson.Options))!;

    /// <summary>A request without a key, token or sign-in: turned away, with the given error code.</summary>
    private async Task Refused(HttpMethod method, string url, HttpClient? from = null, string code = ErrorCodes.Unauthorized, object? json = null)
    {
        var response = await Call(from ?? Api(), method, url, json, HttpStatusCode.Unauthorized);
        var error = await response.Content.ReadFromJsonAsync<ErrorBody>(PairnetsJson.Options);
        Assert.True(error?.Code == code, $"{method} {url} was refused with code {error?.Code} instead of {code}.");
    }

    private static string Location(HttpResponseMessage response) => response.Headers.Location?.ToString() ?? string.Empty;

    private async Task<WebEndpoints.StateView> State(HttpClient browser) => await Read<WebEndpoints.StateView>(Get(browser, "web/api/state"));

    private async Task<WebEndpoints.SecurityView> Security(HttpClient browser) => await Read<WebEndpoints.SecurityView>(Get(browser, "web/api/security"));

    private async Task<ServerHello> Hello() => await Read<ServerHello>(Get(Api(), "api/hello"));

    private async Task<CliResult> Cli(params string[] args)
    {
        var result = await _target.RunCliAsync(args);
        Record.CliRan([.. args, "--data-dir", "<data>"]);
        _log($"[{_target.Name}]   pairnets-server {string.Join(' ', args)} -> exit {result.ExitCode}");
        return result;
    }

    private static string B64(byte[] bytes) => WebAuthnVerifier.ToBase64Url(bytes);

    private static string Shorten(string text) => text.Length <= 400 ? text : text[..400] + "…";

    /// <summary>A computer that joined: its id, name and own key.</summary>
    private sealed record Computer(string Id, string Name, string Key);

    /// <summary>Opens a push channel the way the apps do and keeps it for the end of the walk.</summary>
    private async Task<HubProbe> Listen(string who, string key, string device, HttpTransportType transport = HttpTransportType.WebSockets)
    {
        var hub = await HubProbe.ConnectAsync(who, _target.ApiUrl, key, device, Record, transport);
        _hubs.Add(hub);
        return hub;
    }

    /// <summary>Waits for a mail to <paramref name="to"/> that arrived after the first <paramref name="before"/> ones and has a sign-in link.</summary>
    private async Task<RawMail> MailAfter(int before, string to)
    {
        var deadline = DateTime.UtcNow.AddSeconds(30);
        while (true)
        {
            var mails = await _target.MailsAsync();
            if (mails.Skip(before).LastOrDefault(m => m.Link.Length > 0) is { } mail)
            {
                Assert.Equal(to, mail.To);
                return mail;
            }
            if (DateTime.UtcNow > deadline)
                throw new TimeoutException($"No sign-in mail to {to} arrived within 30 s ({mails.Count - before} other mail(s)).");
            await Task.Delay(100);
        }
    }

    private static JsonElement PublicKeyOf(JsonElement options) => options.GetProperty("publicKey");

    public async ValueTask DisposeAsync()
    {
        foreach (var hub in _hubs)
            await hub.DisposeAsync();
        foreach (var client in _clients)
            client.Dispose();
    }
}
