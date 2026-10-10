using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using System.Web;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http.Connections;
using Microsoft.Extensions.DependencyInjection;
using Pairnets.Core;
using Pairnets.Core.Hashing;
using Pairnets.Server.Storage;
using Pairnets.Server.Web;
using Pairnets.Tests.Infrastructure;

namespace Pairnets.Tests.E2E;

public sealed partial class ApiTour
{
    private HttpClient _oldPc = null!;
    private HubProbe _oldHub = null!;
    private HubProbe _laptopHub = null!;
    private HubProbe _desktopHub = null!;
    private Computer _laptop = null!;
    private Computer _desktop = null!;
    private Computer _tablet = null!;
    private HttpClient _laptopApi = null!;
    private HttpClient _desktopApi = null!;
    private string _password = string.Empty;
    private string _passkeyId = string.Empty;
    private SoftwareAuthenticator? _passkey;

    /// <summary>The whole walk, in order.</summary>
    public async Task RunAsync()
    {
        try
        {
            await PublicAsync();
            await FirstVisitAsync();
            await PasswordsAndSessionsAsync();
            await PasskeysAsync();
            await ComputersJoinAsync();
            await FilesAsync();
            await BigBatchesAsync();
            await RenamingAsync();
            await RemovingAsync();
            await SharedTokenAsync();
            await LinkedNestAsync();
            await MaintenanceCommandsAsync();
            await EmailLinksAsync();
            await GoogleAsync();
            await RemovingSignInWaysAsync();
            await UpdateServerAsync();
            await NoSecretsInTheLogAsync();
        }
        finally
        {
            _passkey?.Dispose();
        }
    }

    // ------------------------------------------------------------------ open to anyone

    private async Task PublicAsync()
    {
        Step("Anyone can ask whether the server is up and what it is");
        Record.CliRan([]); // the bare program, "run the server": the target is running it
        var anon = Api();
        Assert.Equal("ok", await (await Get(anon, "api/health")).Content.ReadAsStringAsync());
        var hello = await Read<ServerHello>(Get(anon, "api/hello"));
        Assert.Equal((PairnetsInfo.ProductName, PairnetsInfo.ApiVersion, Origin, true, true),
            (hello.Product, hello.ApiVersion, hello.PublicUrl, hello.DeviceKeys, hello.SignIn));
        Assert.False(string.IsNullOrEmpty(hello.ServerVersion));
        await Refused(HttpMethod.Get, "api/info");

    }

    // ------------------------------------------------------------------ the website, the setup link, the password

    private async Task FirstVisitAsync()
    {
        Step("The website before anyone signs in: pages, assets, and only on the nest's own name");
        var plain = Api();
        Assert.Equal(Origin + "/devices", Location(await Get(plain, "devices", HttpStatusCode.Redirect)));
        Assert.Equal(Origin + "/", Location(await Get(plain, "", HttpStatusCode.Redirect)));
        await Get(plain, "web/api/state", HttpStatusCode.NotFound);
        await Get(plain, "assets/nest.js", HttpStatusCode.NotFound);

        var visitor = Browser();
        var state = await State(visitor);
        Assert.False(state.SignedIn);
        var fresh = !state.HasSignIn;
        Assert.Equal(fresh ? "/setup" : "/signin", Location(await Get(visitor, "", HttpStatusCode.Redirect)));
        foreach (var (path, page) in WebUi.Pages)
        {
            var response = await Get(visitor, path.TrimStart('/'));
            Assert.Equal("text/html", response.Content.Headers.ContentType?.MediaType);
            Assert.Equal(WebUi.ContentSecurityPolicy, response.Headers.GetValues("Content-Security-Policy").Single());
            Assert.Equal("DENY", response.Headers.GetValues("X-Frame-Options").Single());
            Assert.Contains($"data-page=\"{page}\"", await response.Content.ReadAsStringAsync());
        }
        foreach (var (file, type) in new[] { ("nest.js", "text/javascript"), ("nest.css", "text/css"), ("logo.svg", "image/svg+xml") })
        {
            var asset = await Get(visitor, "assets/" + file);
            Assert.Equal(type, asset.Content.Headers.ContentType?.MediaType);
            var etag = asset.Headers.ETag!;
            await Call(visitor, HttpMethod.Get, "assets/" + file, expect: HttpStatusCode.NotModified, tweak: r => r.Headers.IfNoneMatch.Add(etag));
        }
        await Get(visitor, "assets/nope.js", HttpStatusCode.NotFound);

        Step("The setup link the server prints (owner-link --if-new, as install.sh runs it)");
        var ifNew = await Cli("owner-link", "--if-new");
        Assert.True(ifNew.ExitCode == 0, ifNew.ToString());
        string link;
        if (fresh)
        {
            link = ifNew.Output.Trim();
        }
        else
        {
            Assert.Equal(string.Empty, ifNew.Output.Trim()); // a way in exists already: --if-new prints nothing
            var made = await Cli("owner-link");
            Assert.True(made.ExitCode == 0, made.ToString());
            link = made.Output.Trim();
        }
        Assert.StartsWith(Origin + "/setup#code=", link);
        var code = link[(link.IndexOf("#code=", StringComparison.Ordinal) + 6)..];

        _owner = Browser();
        await Post(_owner, "web/api/setup", new { code = "not-the-code" }, HttpStatusCode.BadRequest);
        await Post(Browser(origin: "https://evil.example"), "web/api/setup", new { code }, HttpStatusCode.Forbidden); // another site's page
        await Post(Browser(origin: null), "web/api/setup", new { code }, HttpStatusCode.Forbidden); // no Origin, no Sec-Fetch-Site
        var used = await Post(_owner, "web/api/setup", new { code }, HttpStatusCode.NoContent);
        Assert.StartsWith(OwnerAuthCookie + "=", used.Headers.GetValues("Set-Cookie").Single());
        await Post(Browser(), "web/api/setup", new { code }, HttpStatusCode.BadRequest); // a link works once
        state = await State(_owner);
        Assert.True(state.SignedIn);
        Assert.Equal(WebEndpoints.SetupLinkMethod, state.SessionMethod);
        Assert.Equal("/devices", Location(await Get(_owner, "", HttpStatusCode.Redirect)));

        Step("Choosing a password");
        await Post(_owner, "web/api/password", new { password = "short" }, HttpStatusCode.BadRequest);
        _password = NewPassword();
        await Post(_owner, "web/api/password", new { password = _password }, HttpStatusCode.NoContent);
        Assert.True((await Hello()).Methods!.Password);
        Assert.True((await State(Browser())).HasSignIn);
    }

    private const string OwnerAuthCookie = Pairnets.Server.Auth.OwnerAuth.CookieName;

    private string NewPassword()
    {
        var password = $"tour password {_tag} {Guid.NewGuid():N}"[..36];
        _secrets.Add(password);
        return password;
    }

    private async Task PasswordsAndSessionsAsync()
    {
        Step("Signing in with the password, and signing another browser out");
        var phone = Browser();
        await Post(phone, "web/api/signin/password", new { password = "not the password" }, HttpStatusCode.BadRequest);
        await Post(phone, "web/api/signin/password", new { password = _password }, HttpStatusCode.NoContent);
        Assert.Equal("password", (await State(phone)).SessionMethod);
        var phoneSession = (await Security(phone)).Sessions.Single(s => s.Current).Id;
        Assert.Contains((await Security(_owner)).Sessions, s => s.Id == phoneSession && !s.Current);
        await Refused(HttpMethod.Delete, $"web/api/sessions/{phoneSession}", Browser());
        await Delete(_owner, $"web/api/sessions/{phoneSession}");
        Assert.False((await State(phone)).SignedIn);
        await Get(phone, "web/api/security", HttpStatusCode.Unauthorized);
        await Delete(_owner, $"web/api/sessions/{phoneSession}", HttpStatusCode.NotFound);

        Step("Changing the password needs the current one; signing out");
        var next = NewPassword();
        await Post(_owner, "web/api/password", new { password = next, current = "not the current one" }, HttpStatusCode.BadRequest);
        await Post(_owner, "web/api/password", new { password = next, current = _password }, HttpStatusCode.NoContent);
        var laptop = Browser();
        await Post(laptop, "web/api/signin/password", new { password = _password }, HttpStatusCode.BadRequest);
        _password = next;
        await Post(laptop, "web/api/signin/password", new { password = _password }, HttpStatusCode.NoContent);
        await Post(laptop, "web/api/signout", expect: HttpStatusCode.NoContent);
        Assert.False((await State(laptop)).SignedIn);
        await Refused(HttpMethod.Post, "web/api/signout", laptop);
    }

    private async Task PasskeysAsync()
    {
        Step("Adding a passkey while signed in, then signing in with it");
        _passkey = new SoftwareAuthenticator("localhost", Origin);
        await Refused(HttpMethod.Post, "web/api/passkeys/register/options", Browser());
        await Refused(HttpMethod.Get, "web/api/passkeys", Browser());
        var options = await Read<JsonElement>(Post(_owner, "web/api/passkeys/register/options"));
        Assert.Equal("localhost", PublicKeyOf(options).GetProperty("rp").GetProperty("id").GetString());
        var made = _passkey.MakeCredential(Challenge(options));
        var name = $"Tour key {_tag}";
        await Post(_owner, "web/api/passkeys/register", new
        {
            challengeId = options.GetProperty("challengeId").GetString(), name, id = B64(made.CredentialId),
            clientDataJson = B64(made.ClientDataJson), attestationObject = B64(made.AttestationObject),
        }, HttpStatusCode.NoContent);
        var mine = (await Read<List<PasskeyEndpoints.PasskeyView>>(Get(_owner, "web/api/passkeys"))).Single(k => k.Name == name);
        _passkeyId = mine.Id;
        Assert.Null(mine.LastUsed);

        var tablet = Browser();
        var signIn = await Read<JsonElement>(Post(tablet, "web/api/signin/passkey/options"));
        Assert.Contains(PublicKeyOf(signIn).GetProperty("allowCredentials").EnumerateArray(), c => c.GetProperty("id").GetString() == B64(_passkey.CredentialId));
        var assertion = _passkey.GetAssertion(Challenge(signIn), signCount: 1);
        await Post(tablet, "web/api/signin/passkey", new
        {
            challengeId = signIn.GetProperty("challengeId").GetString(), id = B64(assertion.CredentialId), clientDataJson = B64(assertion.ClientDataJson),
            authenticatorData = B64(assertion.AuthenticatorData), signature = B64(assertion.Signature),
        }, HttpStatusCode.NoContent);
        Assert.Equal("passkey", (await State(tablet)).SessionMethod);
        Assert.NotNull((await Read<List<PasskeyEndpoints.PasskeyView>>(Get(_owner, "web/api/passkeys"))).Single(k => k.Id == _passkeyId).LastUsed);
        Assert.True((await Hello()).Methods!.Passkeys);
    }

    private static byte[] Challenge(JsonElement options) =>
        Pairnets.Server.Auth.WebAuthn.WebAuthnVerifier.FromBase64Url(PublicKeyOf(options).GetProperty("challenge").GetString())!;

    // ------------------------------------------------------------------ computers

    private async Task ComputersJoinAsync()
    {
        Step("An old app on the shared token, and its push channel");
        var oldName = $"OLD-PC {_tag}";
        _oldPc = Api(_target.Token, oldName);
        Assert.Equal(new DeviceMe(null, oldName, DeviceMe.KindSharedToken), await Read<DeviceMe>(Get(_oldPc, "api/me")));
        await Refused(HttpMethod.Get, "api/me");
        await Refused(HttpMethod.Post, "hub/negotiate?negotiateVersion=1");
        _oldHub = await Listen($"{oldName}'s push channel", _target.Token, oldName);

        Step("A computer asks to join, the owner allows it on the website, and it collects its own key");
        _laptop = await JoinAsync($"LAPTOP {_tag}", approve: true, [_oldHub]);
        _laptopApi = Api(_laptop.Key, "whatever the app sends");
        Assert.Equal(new DeviceMe(_laptop.Id, _laptop.Name, DeviceMe.KindDeviceKey), await Read<DeviceMe>(Get(_laptopApi, "api/me")));
        _laptopHub = await Listen($"{_laptop.Name}'s push channel", _laptop.Key, _laptop.Name);

        Step("One the owner turns away");
        await JoinAsync($"INTRUDER {_tag}", approve: false, [_oldHub, _laptopHub]);
        await Post(Api(), "api/pair/poll", new PairPollRequest("not-a-poll-token"), HttpStatusCode.NotFound);
        await Get(_owner, "web/api/pair/ZZZZ-ZZZZ", HttpStatusCode.NotFound);

        Step("Two more computers; one listens by long polling");
        _desktop = await JoinAsync($"DESKTOP {_tag}", approve: true, [_laptopHub]);
        _desktopApi = Api(_desktop.Key, _desktop.Name);
        _desktopHub = await Listen($"{_desktop.Name}'s push channel", _desktop.Key, _desktop.Name, HttpTransportType.LongPolling);
        _tablet = await JoinAsync($"TABLET {_tag}", approve: true, [_laptopHub, _desktopHub]);

        await Refused(HttpMethod.Get, "api/devices");
        var devices = await Read<List<DeviceInfo>>(Get(_laptopApi, "api/devices"));
        Assert.Contains(devices, d => d.Id == _laptop.Id && d.Name == _laptop.Name && d.Online);
        Assert.Contains(devices, d => d.Id == _desktop.Id);
        Assert.Contains(devices, d => d.Id == _tablet.Id);
        Assert.Contains(devices, d => d.Id is null && d.Name == oldName);
        Assert.DoesNotContain(devices, d => d.Name == $"INTRUDER {_tag}");
    }

    /// <summary>The whole join: ask, everyone hears it, the owner looks and decides on the website, the computer polls.</summary>
    private async Task<Computer> JoinAsync(string name, bool approve, IReadOnlyList<HubProbe> listeners)
    {
        var app = Api();
        var start = await Read<PairStartResponse>(Post(app, "api/pair/start", new PairStartRequest(name, "Windows", "1.0.99")));
        Assert.Matches("^[A-Z2-9]{4}-[A-Z2-9]{4}$", start.Code);
        Assert.Equal($"{Origin}/link?code={start.Code}", start.VerifyUrl);
        foreach (var hub in listeners)
            await hub.WaitForAsync(PairingEndpoints.PairRequestedMethod, a => Equals(a[0], start.Code) && Equals(a[1], name) && Equals(a[2], "Windows"));
        Assert.Equal(PairPollResponse.Pending, (await Read<PairPollResponse>(Post(app, "api/pair/poll", new PairPollRequest(start.PollToken)))).Status);

        var waiting = await Read<WebEndpoints.DevicesView>(Get(_owner, "web/api/devices"));
        Assert.Contains(waiting.Pending, p => p.Code == start.Code && p.Name == name && p.Status == "pending");
        await Refused(HttpMethod.Get, $"web/api/pair/{start.Code}", Browser());
        var view = await Read<WebEndpoints.PairView>(Get(_owner, $"web/api/pair/{start.Code}"));
        Assert.Equal((name, "Windows", "1.0.99", "pending"), (view.Name, view.System, view.AppVersion, view.Status));
        await Refused(HttpMethod.Post, $"web/api/pair/{start.Code}/{(approve ? "approve" : "deny")}", Browser());

        var decided = await Read<WebEndpoints.PairView>(Post(_owner, $"web/api/pair/{start.Code}/{(approve ? "approve" : "deny")}"));
        Assert.Equal(approve ? "approved" : "denied", decided.Status);
        foreach (var hub in listeners)
            await hub.WaitForAsync(PairingEndpoints.PairDecidedMethod, a => Equals(a[0], start.Code) && Equals(a[1], approve));
        await Post(_owner, $"web/api/pair/{start.Code}/approve", expect: HttpStatusCode.Conflict); // decided once

        var outcome = await Read<PairPollResponse>(Post(app, "api/pair/poll", new PairPollRequest(start.PollToken)));
        if (!approve)
        {
            Assert.Equal(PairPollResponse.Denied, outcome.Status);
            Assert.Null(outcome.Key);
            return new Computer(string.Empty, name, string.Empty);
        }
        Assert.Equal((PairPollResponse.Approved, name), (outcome.Status, outcome.Name));
        Assert.StartsWith("pn_", outcome.Key);
        _secrets.Add(outcome.Key!);
        Assert.Equal(PairPollResponse.Used, (await Read<PairPollResponse>(Post(app, "api/pair/poll", new PairPollRequest(start.PollToken)))).Status);
        // Like the app, it uses its key at once (and so shows up in the list of computers).
        Assert.Equal(new DeviceMe(outcome.Id, name, DeviceMe.KindDeviceKey), await Read<DeviceMe>(Get(Api(outcome.Key, name), "api/me")));
        return new Computer(outcome.Id!, outcome.Name!, outcome.Key!);
    }

    // ------------------------------------------------------------------ files

    private async Task FilesAsync()
    {
        var notes = $"tour-{_tag}/notes.txt";
        var first = Encoding.UTF8.GetBytes("first version");
        var second = Encoding.UTF8.GetBytes("second version, longer");

        Step("A file goes up, everyone hears it, and it comes down again");
        await Refused(HttpMethod.Put, $"api/file?path={Uri.EscapeDataString(notes)}&base=none");
        var v1 = await Put(_desktopApi, notes, "none", first);
        Assert.Equal((ContentHash.Of(first), (long)first.Length, false), (v1.Hash, v1.Size, v1.Deleted));
        await _laptopHub.WaitForAsync(SyncHub.ChangedMethod, a => Equals(a[0], _desktop.Name) && Equals(a[1], notes));
        await Refused(HttpMethod.Get, $"api/file?path={Uri.EscapeDataString(notes)}");
        var download = await Get(_laptopApi, $"api/file?path={Uri.EscapeDataString(notes)}");
        Assert.Equal(first, await download.Content.ReadAsByteArrayAsync());
        Assert.Equal(v1.Hash, download.Headers.GetValues(PairnetsHeaders.Hash).Single());
        var part = await Call(_laptopApi, HttpMethod.Get, $"api/file?path={Uri.EscapeDataString(notes)}", expect: HttpStatusCode.PartialContent,
            tweak: r => r.Headers.Range = new RangeHeaderValue(0, 4));
        Assert.Equal("first", await part.Content.ReadAsStringAsync());
        await Get(_laptopApi, $"api/file?path={Uri.EscapeDataString($"tour-{_tag}/missing.txt")}", HttpStatusCode.NotFound);

        Step("Changing it needs the version it is based on");
        var v2 = await Put(_laptopApi, notes, v1.Hash!, second);
        Assert.True(v2.Version > v1.Version);
        await Call(_desktopApi, HttpMethod.Put, $"api/file?path={Uri.EscapeDataString(notes)}&base={v1.Hash}", expect: HttpStatusCode.Conflict,
            tweak: r => r.Content = new ByteArrayContent(first));

        Step("The list of everything, and changes since a version");
        await Refused(HttpMethod.Get, "api/manifest");
        var manifest = await Get(_laptopApi, "api/manifest");
        Assert.Contains(await manifest.Content.ReadFromJsonAsync<List<ManifestEntry>>(PairnetsJson.Options) ?? [], e => e.Path == notes && e.Hash == v2.Hash);
        Assert.NotEmpty(manifest.Headers.GetValues(PairnetsHeaders.ServerId).Single());
        Assert.Equal(v2.Version.ToString(System.Globalization.CultureInfo.InvariantCulture), manifest.Headers.GetValues(PairnetsHeaders.Version).Single());
        var since = await Read<List<ManifestEntry>>(Get(_laptopApi, $"api/manifest?since={v1.Version}"));
        Assert.Equal([notes], since.Select(e => e.Path));
        await Get(_laptopApi, "api/manifest?since=yesterday", HttpStatusCode.BadRequest);

        Step("Older versions, and bringing one back");
        await Refused(HttpMethod.Get, $"api/history?path={Uri.EscapeDataString(notes)}");
        var history = await Read<List<HistoryVersion>>(Get(_laptopApi, $"api/history?path={Uri.EscapeDataString(notes)}"));
        var older = history.Single(h => h.Hash8 == ContentHash.Short(v1.Hash!));
        await Refused(HttpMethod.Post, $"api/history/restore?path={Uri.EscapeDataString(notes)}&id={older.Id}");
        var restored = await Read<ManifestEntry>(Post(_laptopApi, $"api/history/restore?path={Uri.EscapeDataString(notes)}&id={older.Id}"));
        Assert.Equal(v1.Hash, restored.Hash);
        Assert.Equal(first, await (await Get(_desktopApi, $"api/file?path={Uri.EscapeDataString(notes)}")).Content.ReadAsByteArrayAsync());
        await _desktopHub.WaitForAsync(SyncHub.ChangedMethod, a => Equals(a[0], _laptop.Name) && Equals(a[1], notes));

        Step("A big file in pieces, and an upload given up half-way");
        var big = $"tour-{_tag}/big.bin";
        var data = new byte[300 * 1024];
        new Random(7).NextBytes(data);
        var piece = 100 * 1024;
        await Refused(HttpMethod.Post, $"api/upload?path={Uri.EscapeDataString(big)}&base=none&size={data.Length}");
        var upload = await Read<UploadStatus>(Call(_laptopApi, HttpMethod.Post, $"api/upload?path={Uri.EscapeDataString(big)}&base=none&size={data.Length}&mtime=1700000000000",
            expect: HttpStatusCode.Created));
        Assert.Equal(0, upload.Received);
        await Refused(HttpMethod.Put, $"api/upload/{upload.Id}?offset=0");
        Assert.Equal(piece, (await Read<UploadStatus>(PutPiece(upload.Id, 0, data[..piece]))).Received);
        await PutPiece(upload.Id, 0, data[..piece], HttpStatusCode.Conflict); // the server already has that part: continue from where it is
        await Refused(HttpMethod.Get, $"api/upload/{upload.Id}");
        Assert.Equal(piece, (await Read<UploadStatus>(Get(_laptopApi, $"api/upload/{upload.Id}"))).Received);
        await PutPiece(upload.Id, piece, data[piece..(2 * piece)]);
        Assert.Equal(data.Length, (await Read<UploadStatus>(PutPiece(upload.Id, 2 * piece, data[(2 * piece)..]))).Received);
        await Refused(HttpMethod.Post, $"api/upload/{upload.Id}/commit?hash={ContentHash.Of(data)}");
        var committed = await Read<ManifestEntry>(Post(_laptopApi, $"api/upload/{upload.Id}/commit?hash={ContentHash.Of(data)}"));
        Assert.Equal((ContentHash.Of(data), (long)data.Length), (committed.Hash, committed.Size));
        Assert.Equal(data, await (await Get(_desktopApi, $"api/file?path={Uri.EscapeDataString(big)}")).Content.ReadAsByteArrayAsync());

        var dropped = await Read<UploadStatus>(Call(_laptopApi, HttpMethod.Post, $"api/upload?path={Uri.EscapeDataString($"tour-{_tag}/dropped.bin")}&base=none&size=10",
            expect: HttpStatusCode.Created));
        await Refused(HttpMethod.Delete, $"api/upload/{dropped.Id}");
        await Delete(_laptopApi, $"api/upload/{dropped.Id}");
        await Get(_laptopApi, $"api/upload/{dropped.Id}", HttpStatusCode.NotFound);

        Step("Deleting a file");
        await Refused(HttpMethod.Delete, $"api/file?path={Uri.EscapeDataString(notes)}&base={restored.Hash}");
        var gone = await Read<ManifestEntry>(Call(_desktopApi, HttpMethod.Delete, $"api/file?path={Uri.EscapeDataString(notes)}&base={restored.Hash}"));
        Assert.True(gone.Deleted);
        await Get(_laptopApi, $"api/file?path={Uri.EscapeDataString(notes)}", HttpStatusCode.NotFound);
        Assert.Contains(await Read<List<ManifestEntry>>(Get(_laptopApi, $"api/manifest?since={restored.Version}")), e => e.Path == notes && e.Deleted);
    }

    private async Task<ManifestEntry> Put(HttpClient api, string path, string @base, byte[] content) =>
        await Read<ManifestEntry>(Call(api, HttpMethod.Put, $"api/file?path={Uri.EscapeDataString(path)}&base={@base}&mtime=1700000000000",
            tweak: r => r.Content = new ByteArrayContent(content)));

    private Task<HttpResponseMessage> PutPiece(string id, long offset, byte[] bytes, HttpStatusCode expect = HttpStatusCode.OK) =>
        Call(_laptopApi, HttpMethod.Put, $"api/upload/{id}?offset={offset}", expect: expect, tweak: r => r.Content = new ByteArrayContent(bytes));

    // ------------------------------------------------------------------ the push channel

    private async Task BigBatchesAsync()
    {
        Step("Big batches: the others wait, and one that connects during a batch hears about it");
        await _laptopHub.InvokeAsync(nameof(SyncHub.BatchStarted), 3);
        foreach (var hub in new[] { _desktopHub, _oldHub })
            await hub.WaitForAsync(SyncHub.PeerBatchMethod, a => Equals(a[0], _laptop.Name) && Equals(a[1], 3) && Equals(a[2], true));
        var late = await Listen($"{_tablet.Name}'s push channel", _tablet.Key, _tablet.Name);
        await late.WaitForAsync(SyncHub.PeerBatchMethod, a => Equals(a[0], _laptop.Name) && Equals(a[1], 3) && Equals(a[2], true));
        await late.StopAsync();
        await _laptopHub.InvokeAsync(nameof(SyncHub.BatchFinished));
        await _desktopHub.WaitForAsync(SyncHub.PeerBatchMethod, a => Equals(a[0], _laptop.Name) && Equals(a[1], 0) && Equals(a[2], false));
    }

    // ------------------------------------------------------------------ renaming and removing computers

    private async Task RenamingAsync()
    {
        Step("A computer renames itself; names stay unique and everyone hears it");
        await Refused(HttpMethod.Patch, "api/devices/me", json: new DeviceNameRequest("x"));
        await Call(_oldPc, HttpMethod.Patch, "api/devices/me", new DeviceNameRequest("x"), HttpStatusCode.Conflict); // the shared token has no computer to rename
        await Call(_desktopApi, HttpMethod.Patch, "api/devices/me", new DeviceNameRequest("   "), HttpStatusCode.BadRequest);
        var renamed = await Read<DeviceMe>(Call(_desktopApi, HttpMethod.Patch, "api/devices/me", new DeviceNameRequest(_laptop.Name)));
        Assert.Equal((_desktop.Id, $"{_laptop.Name} (2)"), (renamed.Id, renamed.Name));
        await _laptopHub.WaitForAsync(SyncHub.DeviceRenamedMethod, a => Equals(a[0], _desktop.Id) && Equals(a[1], renamed.Name));
        Assert.Equal(renamed.Name, (await Read<DeviceMe>(Get(_desktopApi, "api/me"))).Name);
        _desktop = _desktop with { Name = renamed.Name };

        Step("The owner renames one on the website");
        var kitchen = $"KITCHEN {_tag}";
        await Refused(HttpMethod.Patch, $"web/api/devices/{_tablet.Id}", Browser(), json: new { name = kitchen });
        await Call(_owner, HttpMethod.Patch, $"web/api/devices/{_tablet.Id}", new { name = "" }, HttpStatusCode.BadRequest);
        await Call(_owner, HttpMethod.Patch, "web/api/devices/no-such-computer", new { name = kitchen }, HttpStatusCode.NotFound);
        var named = await Read<JsonElement>(Call(_owner, HttpMethod.Patch, $"web/api/devices/{_tablet.Id}", new { name = kitchen }));
        Assert.Equal((_tablet.Id, kitchen), (named.GetProperty("id").GetString(), named.GetProperty("name").GetString()));
        await _laptopHub.WaitForAsync(SyncHub.DeviceRenamedMethod, a => Equals(a[0], _tablet.Id) && Equals(a[1], kitchen));
        Assert.Contains(await Read<List<DeviceInfo>>(Get(_laptopApi, "api/devices")), d => d.Id == _tablet.Id && d.Name == kitchen);
        _tablet = _tablet with { Name = kitchen };
    }

    private async Task RemovingAsync()
    {
        Step("One computer removes another: its key stops and its push channel closes");
        await Refused(HttpMethod.Delete, $"api/devices/{_desktop.Id}");
        await Delete(_laptopApi, "api/devices/no-such-computer", HttpStatusCode.NotFound);
        await Delete(_laptopApi, $"api/devices/{_desktop.Id}");
        await _laptopHub.WaitForAsync(SyncHub.DeviceRemovedMethod, a => Equals(a[0], _desktop.Id) && Equals(a[1], _desktop.Name));
        await _desktopHub.WaitClosedAsync("its computer was removed");
        await Refused(HttpMethod.Get, "api/info", _desktopApi, ErrorCodes.DeviceRemoved);

        Step("The owner removes one on the website");
        await Refused(HttpMethod.Delete, $"web/api/devices/{_tablet.Id}", Browser());
        await Delete(_owner, $"web/api/devices/{_tablet.Id}");
        await _laptopHub.WaitForAsync(SyncHub.DeviceRemovedMethod, a => Equals(a[0], _tablet.Id) && Equals(a[1], _tablet.Name));
        await Delete(_owner, $"web/api/devices/{_tablet.Id}", HttpStatusCode.NotFound);
        var list = await Read<WebEndpoints.DevicesView>(Get(_owner, "web/api/devices"));
        Assert.Contains(list.Devices, d => d.Id == _laptop.Id && d.OwnKey);
        Assert.DoesNotContain(list.Devices, d => d.Id == _tablet.Id || d.Id == _desktop.Id);
        await Refused(HttpMethod.Get, "api/info", Api(_tablet.Key, _tablet.Name), ErrorCodes.DeviceRemoved);

        Step("Forgetting a computer that only ever used the shared token");
        var retired = $"RETIRED-PC {_tag}";
        await Get(Api(_target.Token, retired), "api/info");
        Assert.Contains((await Read<WebEndpoints.DevicesView>(Get(_owner, "web/api/devices"))).Devices, d => d.Name == retired && d.Id is null && !d.OwnKey);
        await Refused(HttpMethod.Post, "web/api/devices/forget", Browser(), json: new { name = retired });
        await Post(_owner, "web/api/devices/forget", new { name = retired }, HttpStatusCode.NoContent);
        Assert.DoesNotContain((await Read<WebEndpoints.DevicesView>(Get(_owner, "web/api/devices"))).Devices, d => d.Name == retired);
        await Post(_owner, "web/api/devices/forget", new { name = retired }, HttpStatusCode.NotFound);
    }

    private async Task SharedTokenAsync()
    {
        Step("Turning the old shared token off closes its push channel; turning it on lets it back in");
        await Refused(HttpMethod.Post, "web/api/shared-token", Browser(), json: new { allowed = false });
        await Post(_owner, "web/api/shared-token", new { allowed = false }, HttpStatusCode.NoContent);
        await _oldHub.WaitClosedAsync("the shared token was turned off");
        await Refused(HttpMethod.Get, "api/info", _oldPc, ErrorCodes.SharedTokenOff);
        await Get(_laptopApi, "api/info"); // a computer with its own key does not notice
        Assert.False((await Read<WebEndpoints.DevicesView>(Get(_owner, "web/api/devices"))).SharedTokenAllowed);
        Assert.False((await Security(_owner)).SharedTokenAllowed);
        await Post(_owner, "web/api/shared-token", new { allowed = true }, HttpStatusCode.NoContent);
        await Get(_oldPc, "api/info");

        // The push channels are done; the maintenance commands below stop the server anyway.
        await _laptopHub.StopAsync();
    }

    // ------------------------------------------------------------------ the maintenance commands

    // ------------------------------------------------------------------ a nest linked to a Pairnets account

    /// <summary>
    /// The Pairnets service's signed calls (Web/RelayEndpoints.cs) only answer on a nest linked to an account, and such a
    /// nest has no website, so they cannot run on the tour's own nest. That nest must turn every one of them away; then
    /// they run for real on a second nest, linked, inside the test process whatever the target (what it answered counts
    /// as the server's own account). RelayModeTests covers every way a call can be wrongly signed.
    /// </summary>
    private async Task LinkedNestAsync()
    {
        Step("A nest that is not linked to a Pairnets account turns away the service's calls");
        var anon = Api();
        var shared = Api(_target.Token);
        foreach (var (method, url, body) in new (HttpMethod, string, object?)[]
                 {
                     (HttpMethod.Get, "api/relay/status", null),
                     (HttpMethod.Get, "api/relay/devices", null),
                     (HttpMethod.Post, "api/relay/devices", new { name = "LAPTOP", system = "Windows" }),
                     (HttpMethod.Delete, "api/relay/devices/dev-1", null),
                 })
        {
            foreach (var who in new[] { anon, shared })
            {
                var refused = await Call(who, method, url, body, HttpStatusCode.Unauthorized);
                Assert.Equal("bad_signature", (await refused.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("error").GetString());
            }
        }

        Step("A nest linked to a Pairnets account: the service's signed calls add, list and remove a computer");
        await using var linked = await TestServer.StartAsync(config: RelayFixtures.Config(), configureBuilder: builder =>
            builder.Services.AddSingleton<IStartupFilter>(new AnsweredRoutesFilter(Record.ServerSaw)));
        using var service = new HttpClient(new RecordingHandler(Record, new SocketsHttpHandler { UseProxy = false })) { BaseAddress = linked.Url };
        async Task<JsonElement> Signed(HttpMethod method, string path, string? json = null)
        {
            using var response = await service.SendAsync(RelayFixtures.Signed(method, path, json));
            var text = await response.Content.ReadAsStringAsync();
            Assert.True(response.StatusCode == HttpStatusCode.OK, $"{method} {path} answered {(int)response.StatusCode}. {Shorten(text)}");
            using var doc = JsonDocument.Parse(text);
            return doc.RootElement.Clone();
        }

        Assert.Equal(0, (await Signed(HttpMethod.Get, "/api/relay/status")).GetProperty("devices").GetInt32());
        var added = await Signed(HttpMethod.Post, "/api/relay/devices", """{"name":"LAPTOP","system":"Windows 11","approvedBy":"o***@example.com"}""");
        var id = added.GetProperty("id").GetString()!;
        Assert.Equal("LAPTOP", added.GetProperty("name").GetString());
        Assert.StartsWith(AuthStore.KeyPrefix, added.GetProperty("key").GetString());
        Assert.Equal(1, (await Signed(HttpMethod.Get, "/api/relay/status")).GetProperty("devices").GetInt32());
        var listed = (await Signed(HttpMethod.Get, "/api/relay/devices")).GetProperty("devices").EnumerateArray().Single();
        Assert.Equal((id, "LAPTOP", "Windows 11"), (listed.GetProperty("id").GetString(), listed.GetProperty("name").GetString(), listed.GetProperty("system").GetString()));
        Assert.True((await Signed(HttpMethod.Delete, $"/api/relay/devices/{Uri.EscapeDataString(id)}")).GetProperty("removed").GetBoolean());
        Assert.Equal(0, (await Signed(HttpMethod.Get, "/api/relay/status")).GetProperty("devices").GetInt32());
    }

    private async Task MaintenanceCommandsAsync()
    {
        Step("The program's version and help");
        var info = await Read<ServerInfo>(Get(_laptopApi, "api/info"));
        var version = await Cli("--version");
        Assert.True(version.ExitCode == 0, version.ToString());
        Assert.Equal(info.ServerVersion, version.Output.Trim());
        foreach (var help in new[] { "help", "--help", "-h" })
        {
            var usage = await Cli(help);
            Assert.True(usage.ExitCode == 0 && usage.Output.Contains("Usage:", StringComparison.Ordinal), usage.ToString());
        }

        Step("devices list and devices remove (by name and by id), while the server runs");
        var one = await JoinAsync($"CLI-ONE {_tag}", approve: true, []);
        var two = await JoinAsync($"CLI-TWO {_tag}", approve: true, []);
        var list = await Cli("devices", "list");
        Assert.True(list.ExitCode == 0, list.ToString());
        foreach (var computer in new[] { _laptop, one, two })
            Assert.Contains(list.Output.Split('\n'), l => l.StartsWith(computer.Id, StringComparison.Ordinal) && l.Contains(computer.Name, StringComparison.Ordinal));
        Assert.DoesNotContain(_desktop.Id, list.Output, StringComparison.Ordinal);
        Assert.Contains("Shared token: still accepted", list.Output);
        var byName = await Cli("devices", "remove", one.Name);
        Assert.True(byName.ExitCode == 0 && byName.Output.Contains($"Removed {one.Name}.", StringComparison.Ordinal), byName.ToString());
        var byId = await Cli("devices", "remove", two.Id);
        Assert.True(byId.ExitCode == 0 && byId.Output.Contains($"Removed {two.Name}.", StringComparison.Ordinal), byId.ToString());
        Assert.Equal(1, (await Cli("devices", "remove", $"NOBODY {_tag}")).ExitCode);

        Step("Getting ready for rescan and history purge: a file put in by hand, and an old version");
        var hand = $"tour-{_tag}/by-hand.txt";
        await _target.WriteDataFileAsync("files/" + hand, Encoding.UTF8.GetBytes("put here by hand"));
        var purge = $"tour-{_tag}/purge.txt";
        var @base = "none";
        var versions = new List<ManifestEntry>();
        for (var i = 1; i <= 7; i++)
        {
            var entry = await Put(_laptopApi, purge, @base, Encoding.UTF8.GetBytes($"purge version {i}"));
            versions.Add(entry);
            @base = entry.Hash!;
        }
        var history = await Read<List<HistoryVersion>>(Get(_laptopApi, $"api/history?path={Uri.EscapeDataString(purge)}"));
        Assert.Equal(6, history.Count);
        var oldest = history.MinBy(h => h.StoredAtUtc)!;
        var aged = HistoryNames.MakeId(new DateTimeOffset(2020, 1, 2, 3, 4, 5, TimeSpan.Zero), oldest.Hash8);
        await _target.MoveDataFileAsync($"history/{purge}/{oldest.Id}", $"history/{purge}/{aged}");

        Step("With the server stopped: the dry runs change nothing");
        await _target.StopServerAsync();
        var dry = await Cli("rescan", "--dry-run");
        Assert.True(dry.ExitCode == 0 && dry.Output.Contains($"would add: {hand}", StringComparison.Ordinal), dry.ToString());
        var listed = await Cli("history", "list", purge);
        Assert.True(listed.ExitCode == 0, listed.ToString());
        Assert.Equal(history.Select(h => h.Id == oldest.Id ? aged : h.Id).Order(), listed.Output.Split('\n', StringSplitOptions.RemoveEmptyEntries).Select(l => l.Split(' ')[0]).Order());
        var purgeDry = await Cli("history", "purge", "--dry-run");
        Assert.True(purgeDry.ExitCode == 0 && purgeDry.Output.Contains("Would delete 1 version(s)", StringComparison.Ordinal), purgeDry.ToString());
        await _target.StartServerAsync();
        Assert.DoesNotContain(await Read<List<ManifestEntry>>(Get(_laptopApi, "api/manifest")), e => e.Path == hand);
        Assert.Contains(await Read<List<HistoryVersion>>(Get(_laptopApi, $"api/history?path={Uri.EscapeDataString(purge)}")), h => h.Id == aged);

        Step("With the server stopped: rescan, purge and restore for real");
        await _target.StopServerAsync();
        var rescan = await Cli("rescan");
        Assert.True(rescan.ExitCode == 0 && rescan.Output.Contains($"added: {hand}", StringComparison.Ordinal), rescan.ToString());
        var purged = await Cli("history", "purge");
        Assert.True(purged.ExitCode == 0 && purged.Output.Contains("Deleted 1 version(s)", StringComparison.Ordinal), purged.ToString());
        var sixth = history.Single(h => h.Hash8 == ContentHash.Short(versions[5].Hash!));
        var restore = await Cli("history", "restore", purge, sixth.Id);
        Assert.True(restore.ExitCode == 0 && restore.Output.Contains($"Restored {purge} from {sixth.Id}", StringComparison.Ordinal), restore.ToString());
        await _target.StartServerAsync();

        Step("After the restart the API shows what the commands did");
        Assert.Equal("put here by hand", await (await Get(_laptopApi, $"api/file?path={Uri.EscapeDataString(hand)}")).Content.ReadAsStringAsync());
        var after = await Read<List<HistoryVersion>>(Get(_laptopApi, $"api/history?path={Uri.EscapeDataString(purge)}"));
        Assert.DoesNotContain(after, h => h.Id == aged);
        Assert.Contains(after, h => h.Hash8 == ContentHash.Short(versions[6].Hash!)); // the replaced current version is kept
        Assert.Equal("purge version 6", await (await Get(_laptopApi, $"api/file?path={Uri.EscapeDataString(purge)}")).Content.ReadAsStringAsync());
        var devices = await Read<List<DeviceInfo>>(Get(_laptopApi, "api/devices"));
        Assert.DoesNotContain(devices, d => d.Id == one.Id || d.Id == two.Id);
        await Refused(HttpMethod.Get, "api/info", Api(one.Key, one.Name), ErrorCodes.DeviceRemoved);
        await Refused(HttpMethod.Get, "api/info", Api(two.Key, two.Name), ErrorCodes.DeviceRemoved);
        Assert.True((await State(_owner)).SignedIn); // browsers stay signed in across a restart

        Step("owner-link: nothing with --if-new once a way in exists, otherwise a fresh link that works");
        var quiet = await Cli("owner-link", "--if-new");
        Assert.True(quiet.ExitCode == 0 && quiet.Output.Trim().Length == 0, quiet.ToString());
        var made = await Cli("owner-link");
        Assert.True(made.ExitCode == 0, made.ToString());
        var link = made.Output.Trim();
        Assert.StartsWith(Origin + "/setup#code=", link);
        var back = Browser();
        await Post(back, "web/api/setup", new { code = link[(link.IndexOf("#code=", StringComparison.Ordinal) + 6)..] }, HttpStatusCode.NoContent);
        var state = await State(back);
        Assert.Equal((true, WebEndpoints.SetupLinkMethod, true), (state.SignedIn, state.SessionMethod, state.CanResetPassword));
    }

    // ------------------------------------------------------------------ email links and Google

    private async Task EmailLinksAsync()
    {
        Step("Adding an email address: the confirmation link signs in and turns email links on");
        await Refused(HttpMethod.Post, "web/api/email", Browser(), json: new { email = OwnerEmail });
        await Post(_owner, "web/api/email", new { email = "not an address" }, HttpStatusCode.BadRequest);
        var before = (await _target.MailsAsync()).Count;
        await Post(_owner, "web/api/email", new { email = OwnerEmail }, HttpStatusCode.NoContent);
        var confirm = await MailAfter(before, OwnerEmail);
        Assert.StartsWith(Origin + "/email-link#code=", confirm.Link);
        var inbox = Browser();
        await Post(inbox, "web/api/signin/email/confirm", new { code = confirm.Code }, HttpStatusCode.NoContent);
        Assert.Equal("email link", (await State(inbox)).SessionMethod);
        await Post(Browser(), "web/api/signin/email/confirm", new { code = confirm.Code }, HttpStatusCode.BadRequest); // once only
        Assert.Equal("o•••@example.com", (await Security(_owner)).EmailAddress);
        Assert.True((await Hello()).Methods!.Email);

        Step("Asking for a sign-in link: only the owner's own address gets one");
        var visitor = Browser();
        before = (await _target.MailsAsync()).Count;
        await Post(visitor, "web/api/signin/email/request", new { email = "stranger@example.com" }, HttpStatusCode.NoContent);
        await Post(visitor, "web/api/signin/email/request", new { email = OwnerEmail, next = "/devices" }, HttpStatusCode.NoContent);
        var signIn = await MailAfter(before, OwnerEmail);
        Assert.DoesNotContain(await _target.MailsAsync(), m => m.To == "stranger@example.com");
        Assert.EndsWith("&next=%2Fdevices", signIn.Link);
        Assert.False((await State(visitor)).SignedIn); // asking is not signing in
        await Post(visitor, "web/api/signin/email/confirm", new { code = signIn.Code }, HttpStatusCode.NoContent);
        Assert.Equal("email link", (await State(visitor)).SessionMethod);
    }

    private async Task GoogleAsync()
    {
        Step("Google: only on the nest's name, and only once connected");
        if ((await Security(_owner)).GoogleEmail is not null)
            await Delete(_owner, "web/api/google"); // left connected by an earlier run on this server
        await Get(Api(), "auth/google/start", HttpStatusCode.NotFound);
        var stranger = Browser();
        Assert.Equal("/signin?error=google-none", Location(await Get(stranger, "auth/google/start", HttpStatusCode.Redirect)));
        Assert.Equal("/signin", Location(await Get(stranger, "auth/google/start?purpose=connect", HttpStatusCode.Redirect))); // connecting needs a signed-in browser

        Step("Connecting Google on the Security page");
        var toGoogle = Location(await Get(_owner, "auth/google/start?purpose=connect", HttpStatusCode.Redirect));
        var query = HttpUtility.ParseQueryString(new Uri(toGoogle).Query);
        Assert.Equal((Origin + "/auth/google/callback", "S256", "code"), (query["redirect_uri"], query["code_challenge_method"], query["response_type"]));
        Assert.Equal("/security?connected=google", Location(await Get(_owner, await AtGoogle(toGoogle), HttpStatusCode.Redirect)));
        Assert.Equal(FakeGoogle.DefaultEmail, (await Security(_owner)).GoogleEmail);
        Assert.True((await Hello()).Methods!.Google);

        Step("Signing in with Google comes back to the page it left");
        var phone = Browser();
        var start = Location(await Get(phone, "auth/google/start?next=" + Uri.EscapeDataString("/link?code=ABCD-EFGH"), HttpStatusCode.Redirect));
        Assert.Equal("/link?code=ABCD-EFGH", Location(await Get(phone, await AtGoogle(start), HttpStatusCode.Redirect)));
        Assert.Equal("google", (await State(phone)).SessionMethod);
        Assert.Equal("/signin?error=google-expired", Location(await Get(Browser(), "auth/google/callback?state=made-up&code=x", HttpStatusCode.Redirect)));
    }

    /// <summary>The browser's visit to (the fake) Google: it answers with the way back to the nest.</summary>
    private static async Task<string> AtGoogle(string url)
    {
        using var http = new HttpClient(new SocketsHttpHandler { UseProxy = false, AllowAutoRedirect = false });
        var response = await http.GetAsync(url);
        Assert.True(response.StatusCode == HttpStatusCode.Redirect, $"Google's page answered {(int)response.StatusCode} instead of sending the browser back.");
        return Location(response);
    }

    private async Task RemovingSignInWaysAsync()
    {
        Step("Removing sign-in ways: any of them, except the last one");
        await Refused(HttpMethod.Delete, "web/api/google", Browser());
        await Delete(_owner, "web/api/google");
        Assert.Null((await Security(_owner)).GoogleEmail);
        await Refused(HttpMethod.Delete, "web/api/email", Browser());
        await Delete(_owner, "web/api/email");
        Assert.Null((await Security(_owner)).EmailAddress);
        Assert.False((await Hello()).Methods!.Email);
        await Refused(HttpMethod.Delete, "web/api/password", Browser());
        await Delete(_owner, "web/api/password");
        await Post(Browser(), "web/api/signin/password", new { password = _password }, HttpStatusCode.BadRequest);
        Assert.False((await Hello()).Methods!.Password);

        var security = await Security(_owner);
        await Refused(HttpMethod.Delete, $"web/api/passkeys/{_passkeyId}", Browser());
        if (security.Passkeys.Count == 1)
            await Delete(_owner, $"web/api/passkeys/{_passkeyId}", HttpStatusCode.Conflict); // the last way in stays
        _password = NewPassword();
        await Post(_owner, "web/api/password", new { password = _password }, HttpStatusCode.NoContent); // none to give as the current one
        await Delete(_owner, $"web/api/passkeys/{_passkeyId}");
        Assert.DoesNotContain(await Read<List<PasskeyEndpoints.PasskeyView>>(Get(_owner, "web/api/passkeys")), k => k.Id == _passkeyId);
        await Delete(_owner, $"web/api/passkeys/{_passkeyId}", HttpStatusCode.NotFound);
        if ((await Security(_owner)).Methods is { Passkeys: 0, Email: false, Google: false })
            await Delete(_owner, "web/api/password", HttpStatusCode.Conflict); // now the password is the last way in
        await Post(Browser(), "web/api/signin/password", new { password = _password }, HttpStatusCode.NoContent);
    }

    // ------------------------------------------------------------------ the last step

    private async Task UpdateServerAsync()
    {
        Step("Update server (on the installed server this really runs the updater)");
        await Refused(HttpMethod.Get, "api/update/diagnostics");
        var diagnostics = await Get(_laptopApi, "api/update/diagnostics");
        var text = await diagnostics.Content.ReadAsStringAsync();
        Assert.DoesNotContain(_target.Token, text, StringComparison.Ordinal);
        var report = JsonSerializer.Deserialize<UpdaterDiagnostics>(text, PairnetsJson.Options)!;
        Assert.True(report.UpdaterInstalled);
        Assert.Equal((await Read<ServerInfo>(Get(_laptopApi, "api/info"))).ServerVersion, report.ServerVersion);

        await Refused(HttpMethod.Post, "api/update");
        var requested = await Read<UpdaterStatus>(Post(_laptopApi, "api/update", expect: HttpStatusCode.Accepted));
        Assert.True(requested.Installed);
        Assert.True(await _target.UpdateRequestedAsync(), "Update server was accepted, but no request reached the updater.");
        if (!_target.RealUpdater)
        {
            Assert.Equal("requested", requested.State);
            await Post(_laptopApi, "api/update", expect: HttpStatusCode.TooManyRequests); // once a minute is enough
            Assert.Equal("requested", (await Read<ServerInfo>(Get(_laptopApi, "api/info"))).Updater!.State);
        }
    }

    private async Task NoSecretsInTheLogAsync()
    {
        Step("No token, key or password ever reached the server's log");
        var log = await _target.ServerLogAsync();
        foreach (var secret in _secrets)
        {
            var line = log.FirstOrDefault(l => l.Contains(secret, StringComparison.Ordinal));
            Assert.True(line is null, $"The server logged a secret: {line?.Replace(secret, "<secret>", StringComparison.Ordinal)}");
        }
    }
}
