using System.Globalization;
using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using Pairnets.Core.Api;
using Pairnets.Core.Settings;

namespace Pairnets.Core.Client;

/// <summary>
/// What signing in with a Pairnets account gave this computer: the server it joins, reached through the service
/// (<see cref="ServerUrl"/>, the new ServerUrl), and its own key for that server, handed out exactly once.
/// </summary>
/// <param name="ServerUrl">The server's relay address, <c>https://sync.pairnets.app/n/&lt;nest id&gt;/</c>.</param>
/// <param name="NestLabel">The server's name on the account page ("soro").</param>
/// <param name="Device">This computer's id, its name as the server recorded it (it may add " (2)"), and its key.</param>
/// <param name="Email">The account it signed in with, for showing.</param>
/// <param name="Access">
/// What this computer may do on the nest (friend invites): the owner's own nest, or one a friend shared with it, with
/// whose it is (<see cref="Sync.NestAccess.SharedBy"/>) and the folders it gets.
/// </param>
public sealed record AccountSignInResult(Uri ServerUrl, string NestId, string NestLabel, DeviceKeyGrant Device, string Email, Sync.NestAccess? Access = null)
{
    /// <summary>The nest's access: the owner's when the service said nothing (one from before friends).</summary>
    public Sync.NestAccess ShareAccess => Access ?? Sync.NestAccess.Full;
}

/// <summary>An account sign-in's progress, for the window to draw.</summary>
/// <param name="Code">The code to show ("ABCD-EFGH"); the browser page shows the same one.</param>
/// <param name="VerifyUrl">The page to open in the browser, where the person signs in and presses Allow.</param>
/// <param name="ServerOffline">
/// Allowed in the browser, but the chosen server is not connected right now, so it cannot hand out this computer's key
/// yet. The flow keeps asking; <see cref="AccountSignInState.Message"/> says so.
/// </param>
/// <param name="NoNest">
/// Allowed in the browser, but the account has no nest yet (cloud/RELAY.md §6, "No nest yet"). The flow keeps asking
/// and finishes by itself once the account adds one; <see cref="AccountEmail"/> says which account.
/// </param>
/// <param name="AccountEmail">The account's address, known once the computer was allowed.</param>
/// <param name="Result">Set once <see cref="Stage"/> is <see cref="PairingStage.Approved"/>.</param>
/// <param name="ByLink">
/// A sign-in link (<see cref="AccountSignIn"/> made with byLink): <see cref="VerifyUrl"/> is the long link to copy,
/// there is no <see cref="Code"/>, and only the long code the browser shows, given to
/// <see cref="AccountSignIn.RedeemAsync"/>, finishes it.
/// </param>
public sealed record AccountSignInState(
    PairingStage Stage,
    string? Code = null,
    string? VerifyUrl = null,
    DateTimeOffset? ExpiresAt = null,
    string? Message = null,
    bool ServerOffline = false,
    AccountSignInResult? Result = null,
    bool NoNest = false,
    string? AccountEmail = null,
    bool ByLink = false)
{
    /// <summary>"code expires in 9:41" while waiting.</summary>
    public string ExpiresText(DateTimeOffset now) => PairingState.ExpiresIn(ExpiresAt, now);

    public bool IsFinished => Stage is PairingStage.Approved or PairingStage.Denied or PairingStage.Expired or PairingStage.Failed;
}

/// <summary>
/// "Continue with email" / "Continue with Google": signs this computer in with a Pairnets account (cloud/RELAY.md §6,
/// CONTRACT.md §6.9). Asks the service for a code, the window opens the browser page that shows it, the person signs in
/// there, picks the server and presses Allow; meanwhile this asks every few seconds how it went. Once allowed, the service
/// has the server hand out this computer's own key and passes it on, with the server's relay address. The account's
/// own token in that answer is signed out of straight away: the app never keeps it. A dropped connection on the way is
/// retried quietly; the code keeps working. The windows only draw <see cref="State"/>.
/// </summary>
public sealed class AccountSignIn
{
    /// <summary>The longest wait between two questions, however often the service says "slow down".</summary>
    private static readonly TimeSpan MaxInterval = TimeSpan.FromSeconds(30);

    private static readonly TimeSpan RequestTimeout = TimeSpan.FromSeconds(30);

    /// <summary>Signing the account token out is a courtesy (it lapses within the hour anyway): never wait long for it.</summary>
    private static readonly TimeSpan LogoutTimeout = TimeSpan.FromSeconds(5);

    private readonly string _name;
    private readonly string? _method;
    private readonly string? _email;
    private readonly Uri _service;
    private readonly TimeProvider _clock;
    private readonly HttpMessageHandler? _handler;
    private readonly TimeSpan? _interval;
    private readonly IReadOnlyList<string> _joined;
    private readonly bool _byLink;

    /// <summary>A link sign-in, while it waits for its code: the connection and the sign-in it was started as.</summary>
    private HttpClient? _http;
    private string? _deviceCode;
    private readonly TaskCompletionSource _done = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private int _redeeming;

    /// <param name="name">This computer's name, as the browser page and later the account page show it.</param>
    /// <param name="method">"email" or "google": the button pressed in the app, so the page starts that way (anything else is left out).</param>
    /// <param name="service">The Pairnets service (by default <see cref="Relay.DefaultServiceUrl"/>; tests use a stand-in).</param>
    /// <param name="interval">How often to ask (tests); by default what the service says.</param>
    /// <param name="email">
    /// The address typed in the app next to "Continue with email", passed on to the page like the nest's own sign-in
    /// does (<see cref="Nest.ApprovalLink"/>), so it need not be typed twice. Only with <paramref name="method"/> "email"
    /// and only when it looks like an address.
    /// </param>
    /// <param name="joined">
    /// The nests this computer already syncs (their nest ids): the browser page shows them as already here, so the
    /// person picks another one when adding a nest (docs/DECISIONS.md, "Several nests at once").
    /// </param>
    /// <param name="byLink">
    /// Sign in with a link (CONTRACT.md §6.9): for when the browser didn't open here, or the sign-in happens on another
    /// computer or a phone. The service gives a long one-time link instead of a code; it is opened anywhere, and after
    /// Allow the page there shows a long code, pasted here (<see cref="RedeemAsync"/>). Nothing is polled.
    /// </param>
    public AccountSignIn(string name, string? method = null, Uri? service = null, TimeProvider? clock = null,
        HttpMessageHandler? handler = null, TimeSpan? interval = null, string? email = null, IEnumerable<string>? joined = null,
        bool byLink = false)
    {
        _byLink = byLink;
        _joined = joined?.Where(Relay.IsNestId).Distinct(StringComparer.Ordinal).Take(20).ToList() ?? [];
        _name = CleanName(name);
        _method = method is "email" or "google" ? method : null;
        _email = _method == "email" && Nest.LooksLikeEmail(email) ? email!.Trim() : null;
        _service = PairnetsApiClient.NormalizeBase(service ?? Relay.DefaultServiceUrl);
        _clock = clock ?? TimeProvider.System;
        _handler = handler;
        _interval = interval;
    }

    /// <summary>"you@example.com" → "y•••@example.com": the account, as the "no nest yet" screen shows it. Null when there is none.</summary>
    public static string? MaskEmail(string? address)
    {
        var trimmed = address?.Trim();
        if (string.IsNullOrEmpty(trimmed))
            return null;
        var at = trimmed.IndexOf('@', StringComparison.Ordinal);
        return at <= 0 ? "•••" : trimmed[0] + "•••" + trimmed[at..];
    }

    public AccountSignInState State { get; private set; } = new(PairingStage.Starting);

    /// <summary>How long the flow waits between two questions right now (it grows when the service says "slow down").</summary>
    public TimeSpan PollInterval { get; private set; }

    /// <summary>Raised whenever <see cref="State"/> changes (on a background thread).</summary>
    public event Action<AccountSignInState>? Changed;

    /// <summary>
    /// The settings after an account sign-in: like <see cref="Nest.SettingsAfterSignIn"/> with the server's relay address,
    /// plus the account's email for showing. Everything else carries over.
    /// </summary>
    public static ClientSettings SettingsAfterSignIn(ClientSettings? previous, AccountSignInResult result, string folder,
        bool startAtLogin, string protectedKey)
    {
        var next = Nest.SettingsAfterSignIn(previous, result.ServerUrl, result.Device, folder, startAtLogin, protectedKey, result.Email);
        next.NestLabel = ComputerName.Clean(result.NestLabel) is { Length: > 0 } label ? label : null;
        // A nest a friend shared with this account: what it may do there and whose it is (the nest keeps it up to date).
        var access = result.ShareAccess;
        next.Access = access.IsOwner ? null : access.Level;
        next.SharedFolders = access.Folders is null ? null : [.. access.Folders];
        next.SharedBy = access.SharedBy;
        return next;
    }

    /// <summary>Runs the whole sign-in. Returns the final state; cancelling returns quietly.</summary>
    public async Task<AccountSignInState> RunAsync(CancellationToken ct)
    {
        // Connected like the sync client (no proxy): what works for signing in then works for syncing too.
        var handler = _handler ?? new SocketsHttpHandler { ConnectTimeout = TimeSpan.FromSeconds(15), UseProxy = false };
        using var http = new HttpClient(handler, disposeHandler: _handler is null)
        {
            BaseAddress = _service,
            Timeout = Timeout.InfiniteTimeSpan,
        };
        http.DefaultRequestHeaders.UserAgent.Add(new ProductInfoHeaderValue("Pairnets", PairnetsInfo.ProductVersion));

        if (_byLink)
            return await RunLinkAsync(http, ct).ConfigureAwait(false);

        StartAnswer start;
        try
        {
            var asked = await SendAsync(http, "v1/app/start", new StartRequest(_name, PairingFlow.SystemName(), PairnetsInfo.ProductVersion), ct).ConfigureAwait(false);
            if (asked.Status != HttpStatusCode.OK || asked.Read<StartAnswer>() is not { DeviceCode.Length: > 0, UserCode.Length: > 0 } answer)
                return Set(new(PairingStage.Failed, Message: StartRefused(asked)));
            start = answer;
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            return State;
        }
        catch (PairnetsNetworkException)
        {
            return Set(new(PairingStage.Failed, Message: "Can't reach Pairnets. Check that this computer is online, then try again."));
        }

        var expires = _clock.GetUtcNow().AddSeconds(start.ExpiresIn is > 0 ? start.ExpiresIn.Value : 600);
        Set(new(PairingStage.Waiting, start.UserCode, BrowserLink(start), expires));
        PollInterval = _interval ?? TimeSpan.FromSeconds(Math.Clamp(start.Interval ?? 3, 1, 30));
        while (!ct.IsCancellationRequested)
        {
            Answer poll;
            try
            {
                await Task.Delay(PollInterval, _clock, ct).ConfigureAwait(false);
                poll = await SendAsync(http, "v1/app/poll", new PollRequest(start.DeviceCode), ct).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                break;
            }
            catch (PairnetsNetworkException)
            {
                if (_clock.GetUtcNow() >= expires)
                    return Set(State with { Stage = PairingStage.Expired, Message = "The code expired.", ServerOffline = false });
                Set(State with { Message = "Lost the connection to Pairnets; still trying…" });
                continue;
            }

            switch (poll.Status, poll.Error)
            {
                case (HttpStatusCode.OK, _) when poll.Read<PollAnswer>() is { } answer:
                    switch (answer.Status)
                    {
                        case "approved":
                            return await FinishAsync(http, answer).ConfigureAwait(false);
                        case "denied":
                            return Set(State with { Stage = PairingStage.Denied, Message = "This computer was turned down in the browser.", ServerOffline = false });
                        case "no_nest":
                            // Allowed, but the account has no nest yet: the service holds the sign-in for half an hour
                            // and this computer joins the nest as soon as the account adds one.
                            if (!State.NoNest)
                            {
                                expires = _clock.GetUtcNow() + NoNestWait;
                                Set(State with { NoNest = true, ServerOffline = false, AccountEmail = answer.Email?.Trim(), ExpiresAt = expires, Message = NoNestMessage });
                            }
                            break;
                        default: // pending
                            if (State.Message is not null || State.ServerOffline)
                                Set(State with { Message = null, ServerOffline = false });
                            break;
                    }
                    break;
                case (_, "expired"):
                    return Set(State with { Stage = PairingStage.Expired, Message = "The code expired.", ServerOffline = false });
                case (_, ServiceErrors.NestOffline):
                    // Allowed, but the server cannot hand out the key right now. The sign-in stays allowed; keep asking.
                    if (!State.ServerOffline || State.Message != ServerOfflineMessage)
                        Set(State with { Message = ServerOfflineMessage, ServerOffline = true });
                    break;
                case (HttpStatusCode.TooManyRequests, _):
                    // "slow_down" (asked too soon) or the service's general limit: wait longer from now on.
                    PollInterval = TimeSpan.FromTicks(Math.Min(MaxInterval.Ticks, Math.Max(PollInterval.Ticks * 3 / 2, (poll.RetryAfter ?? TimeSpan.Zero).Ticks)));
                    break;
                case (>= HttpStatusCode.InternalServerError, _):
                    if (_clock.GetUtcNow() >= expires)
                        return Set(State with { Stage = PairingStage.Expired, Message = "The code expired.", ServerOffline = false });
                    Set(State with { Message = "Pairnets is having trouble; still trying…" });
                    break;
                default:
                    return Set(State with { Stage = PairingStage.Failed, Message = $"Pairnets refused this sign-in ({poll.Error ?? ((int)poll.Status).ToString(CultureInfo.InvariantCulture)}). Get a new code and try again.", ServerOffline = false });
            }
        }
        return State;
    }

    /// <summary>
    /// A link sign-in: asks for the link, shows it, and then waits (without asking anything) until <see cref="RedeemAsync"/>
    /// finishes it, it is stopped, or the link runs out.
    /// </summary>
    private async Task<AccountSignInState> RunLinkAsync(HttpClient http, CancellationToken ct)
    {
        LinkStartAnswer start;
        try
        {
            var asked = await SendAsync(http, "v1/app/start", new LinkStartRequest(_name, PairingFlow.SystemName(), PairnetsInfo.ProductVersion, "code"), ct).ConfigureAwait(false);
            // The link must be the service's own sign-in-link page, so copying it never sends anyone elsewhere.
            if (asked.Status != HttpStatusCode.OK || asked.Read<LinkStartAnswer>() is not { DeviceCode.Length: > 0, Link.Length: > 0 } answer
                || !Uri.TryCreate(answer.Link, UriKind.Absolute, out var link) || !Relay.IsServiceAddress(link, _service)
                || link.AbsolutePath != "/app/link" || link.UserInfo.Length != 0 || link.Fragment.Length != 0)
                return Set(new(PairingStage.Failed, Message: asked.Status == HttpStatusCode.OK
                    ? "Pairnets answered with a link this app can't use. Update Pairnets, then try again."
                    : StartRefused(asked)));
            start = answer;
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            return State;
        }
        catch (PairnetsNetworkException)
        {
            return Set(new(PairingStage.Failed, Message: "Can't reach Pairnets. Check that this computer is online, then try again."));
        }

        var lifetime = TimeSpan.FromSeconds(start.ExpiresIn is > 0 ? start.ExpiresIn.Value : 900);
        Set(new(PairingStage.Waiting, VerifyUrl: start.Link, ExpiresAt: _clock.GetUtcNow() + lifetime, ByLink: true));
        _deviceCode = start.DeviceCode;
        _http = http;
        try
        {
            // Allow (and the 5 minutes for the code) can't outlast the link by much: wait a little past it, then stop.
            await _done.Task.WaitAsync(lifetime + LinkCodeTime, _clock, ct).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
        }
        catch (TimeoutException)
        {
            if (!State.IsFinished)
                Set(State with { Stage = PairingStage.Expired, Message = LinkRanOut });
        }
        finally
        {
            // A redeem on its way finishes first: it uses this connection.
            while (Volatile.Read(ref _redeeming) != 0)
                await Task.Delay(20, CancellationToken.None).ConfigureAwait(false);
            _http = null;
        }
        return State;
    }

    /// <summary>How long the code works after Allow (the service says the same).</summary>
    private static readonly TimeSpan LinkCodeTime = TimeSpan.FromMinutes(5);

    private const string LinkRanOut = "The sign-in link ran out. Start again to get a new one.";

    /// <summary>What <see cref="RedeemAsync"/> made of a pasted code.</summary>
    /// <param name="Done">Signed in: <see cref="State"/> is approved.</param>
    /// <param name="Message">What to say under the box (a typo, a wrong code and the tries left, not allowed yet, ...).</param>
    /// <param name="Over">
    /// This sign-in can't be finished any more (stopped, run out): <see cref="State"/> says why and the window goes back.
    /// </param>
    public sealed record LinkRedeem(bool Done, string? Message, bool Over);

    /// <summary>
    /// The long code from the browser, pasted in the app. A typo is caught here (nothing is sent). The service checks
    /// the code; the right one hands over this computer's key once, like an allowed poll does.
    /// </summary>
    public async Task<LinkRedeem> RedeemAsync(string? typed, CancellationToken ct = default)
    {
        if (!_byLink)
            throw new InvalidOperationException("Only a sign-in link takes a code.");
        if (LinkCode.Read(typed) is not { } code)
            return new(false, string.IsNullOrWhiteSpace(typed) ? "Paste the code the browser shows after you press Allow." : CodeTypo, false);
        if (Interlocked.Exchange(ref _redeeming, 1) != 0)
            return new(false, null, false); // one at a time: the first one's answer counts
        try
        {
            var http = _http;
            if (http is null || State.IsFinished || _deviceCode is null)
                return new(false, State.IsFinished ? State.Message : "Wait until the link shows, then try again.", State.IsFinished);
            Answer answer;
            try
            {
                answer = await SendAsync(http, "v1/app/redeem", new RedeemRequest(_deviceCode, code), ct).ConfigureAwait(false);
            }
            catch (PairnetsNetworkException)
            {
                return new(false, "Can't reach Pairnets right now. Check that this computer is online, then press Continue again.", false);
            }

            switch (answer.Status, answer.Error)
            {
                case (HttpStatusCode.OK, _) when answer.Read<PollAnswer>() is { Status: "approved" } approved:
                    var done = await FinishAsync(http, approved).ConfigureAwait(false);
                    _done.TrySetResult();
                    return done.Stage == PairingStage.Approved ? new(true, null, false) : new(false, done.Message, true);
                case (_, "invalid_code"):
                    var left = answer.Read<WrongCodeAnswer>()?.TriesLeft;
                    return new(false, left switch
                    {
                        1 => "That code doesn't match. Check you copied all of it. 1 try left.",
                        > 1 => $"That code doesn't match. Check you copied all of it. {left} tries left.",
                        _ => "That code doesn't match. Check you copied all of it.",
                    }, false);
                case (_, "code_typo"):
                    return new(false, CodeTypo, false);
                case (_, "not_allowed_yet"):
                    return new(false, "Open the link, sign in there and press Allow first. Then paste the code it shows.", false);
                case (_, ServiceErrors.NestOffline):
                    return new(false, "Allowed, but your nest isn't connected right now. Check that it is on, then press Continue again.", false);
                case (HttpStatusCode.TooManyRequests, _):
                    return new(false, "Wait a few seconds, then press Continue again.", false);
                case (>= HttpStatusCode.InternalServerError, _):
                    return new(false, "Pairnets is having trouble right now. Press Continue again in a moment.", false);
                case (_, "stopped"):
                    return End(PairingStage.Denied, "This sign-in was stopped: No was pressed in the browser, or the code was wrong 5 times. Start again with a new link.");
                case (_, "code_expired"):
                    return End(PairingStage.Expired, "The code ran out (codes work for 5 minutes after Allow). Start again with a new link.");
                case (_, "expired"):
                    return End(PairingStage.Expired, LinkRanOut);
                default:
                    return End(PairingStage.Failed, $"Pairnets refused this sign-in ({answer.Error ?? ((int)answer.Status).ToString(CultureInfo.InvariantCulture)}). Start again with a new link.");
            }
        }
        finally
        {
            Volatile.Write(ref _redeeming, 0);
        }

        LinkRedeem End(PairingStage stage, string message)
        {
            Set(State with { Stage = stage, Message = message });
            _done.TrySetResult();
            return new(false, message, true);
        }
    }

    private const string CodeTypo = "That isn't the whole code, or it has a typo. Copy all of it from the browser and try again.";

    /// <summary>How long the service holds a sign-in allowed for an account with no nest (cloud/RELAY.md §6).</summary>
    public static readonly TimeSpan NoNestWait = TimeSpan.FromMinutes(30);

    /// <summary>What the window says while the account has no nest yet.</summary>
    public const string NoNestMessage = "Your account has no nest yet. Set one up, and this computer finishes signing in by itself.";

    /// <summary>What the window says while the server cannot be reached to hand out the key.</summary>
    public const string ServerOfflineMessage = "Allowed. " + ServiceErrors.NestOfflineMessage + " Pairnets keeps trying, so this finishes by itself once it answers.";

    /// <summary>The answer to an allowed sign-in: checked, the account token signed out of, and the result made.</summary>
    private async Task<AccountSignInState> FinishAsync(HttpClient http, PollAnswer answer)
    {
        try
        {
            if (answer.Nest is not { Id: { } nestId, ServerUrl: { } address } nest || answer.Device is not { Id.Length: > 0, Name.Length: > 0, Key.Length: > 0 } device)
                return Set(State with { Stage = PairingStage.Failed, ServerOffline = false, Message = "This computer was allowed, but Pairnets did not hand out a key for it. Update Pairnets, then try again." });
            // The address goes into the settings and every request after: only a relay address of this very service, for the
            // server the answer names, is taken.
            if (!Relay.IsNestId(nestId) || Relay.NestId(address, _service) != nestId || !PairnetsApiClient.TryParseServerUrl(address, out var serverUrl) || serverUrl is null)
                return Set(State with { Stage = PairingStage.Failed, ServerOffline = false, Message = "Pairnets answered with a server address this app cannot use. Update Pairnets, then try again." });
            var label = string.IsNullOrWhiteSpace(nest.Label) ? "Your server" : nest.Label.Trim();
            var result = new AccountSignInResult(serverUrl, nestId, label,
                new DeviceKeyGrant(device.Id, device.Name, device.Key), answer.Email?.Trim() ?? string.Empty,
                Sync.NestAccess.Read(nest.Access, nest.Folders, nest.SharedBy, label));
            return Set(State with { Stage = PairingStage.Approved, Message = null, ServerOffline = false, NoNest = false, Result = result });
        }
        finally
        {
            await LogoutAsync(http, answer.AppToken).ConfigureAwait(false);
        }
    }

    /// <summary>Signs the account token out of the service (best effort: it lapses within the hour anyway).</summary>
    private static async Task LogoutAsync(HttpClient http, string? appToken)
    {
        if (string.IsNullOrEmpty(appToken))
            return;
        try
        {
            using var timeout = new CancellationTokenSource(LogoutTimeout);
            using var req = new HttpRequestMessage(HttpMethod.Post, "v1/app/logout");
            req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", appToken);
            using var resp = await http.SendAsync(req, timeout.Token).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is HttpRequestException or IOException or OperationCanceledException)
        {
            // Nothing to do: the token is forgotten here either way.
        }
    }

    /// <summary>
    /// The page to open: the service's own link when it is on the service (anything else is rebuilt from the service's
    /// address and the code, so a link never takes the browser elsewhere), plus the button pressed in the app.
    /// </summary>
    private string BrowserLink(StartAnswer start)
    {
        var link = Uri.TryCreate(start.VerificationUriComplete, UriKind.Absolute, out var given) && Relay.IsServiceAddress(given, _service)
            && given.UserInfo.Length == 0 && given.Fragment.Length == 0
            ? given.AbsoluteUri
            : $"{Relay.Origin(_service)}/app?code={Uri.EscapeDataString(start.UserCode!)}";
        if (_method is not null)
            link += (link.Contains('?', StringComparison.Ordinal) ? "&" : "?") + "method=" + _method;
        if (_email is not null)
            link += (link.Contains('?', StringComparison.Ordinal) ? "&" : "?") + "email=" + Uri.EscapeDataString(_email);
        if (_joined.Count > 0)
            link += (link.Contains('?', StringComparison.Ordinal) ? "&" : "?") + "have=" + string.Join(",", _joined);
        return link;
    }

    private static string StartRefused(Answer asked) => asked.Status switch
    {
        HttpStatusCode.TooManyRequests => "Too many sign-ins from this network. Wait a while, then try again.",
        >= HttpStatusCode.InternalServerError => "Pairnets is having trouble right now. Try again in a few minutes.",
        _ => $"Pairnets could not start a sign-in ({asked.Error ?? ((int)asked.Status).ToString(CultureInfo.InvariantCulture)}). Update Pairnets, then try again.",
    };

    /// <summary>The service takes names of at most 64 characters, without control characters (this computer's own name when empty).</summary>
    private static string CleanName(string? name) => ComputerName.Clean(name) is { Length: > 0 } clean ? clean : ComputerName.Default();

    private AccountSignInState Set(AccountSignInState state)
    {
        State = state;
        Changed?.Invoke(state);
        return state;
    }

    // ------------------------------------------------------------------ talking to the service

    /// <summary>One answer of the service, read whole (they are all short JSON).</summary>
    private sealed record Answer(HttpStatusCode Status, string Body, string? Error, TimeSpan? RetryAfter)
    {
        public T? Read<T>() where T : class
        {
            try
            {
                return JsonSerializer.Deserialize<T>(Body, PairnetsJson.Options);
            }
            catch (JsonException)
            {
                return null;
            }
        }
    }

    /// <summary>POSTs JSON; network trouble of any kind becomes <see cref="PairnetsNetworkException"/>.</summary>
    private static async Task<Answer> SendAsync<T>(HttpClient http, string path, T body, CancellationToken ct)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeout.CancelAfter(RequestTimeout);
        try
        {
            using var resp = await http.PostAsJsonAsync(path, body, PairnetsJson.Options, timeout.Token).ConfigureAwait(false);
            var text = await resp.Content.ReadAsStringAsync(timeout.Token).ConfigureAwait(false);
            string? error = null;
            if (!resp.IsSuccessStatusCode && resp.Content.Headers.ContentType?.MediaType?.EndsWith("json", StringComparison.OrdinalIgnoreCase) == true)
            {
                try
                {
                    error = JsonSerializer.Deserialize<ServiceErrorBody>(text, PairnetsJson.Options)?.Error;
                }
                catch (JsonException)
                {
                }
            }
            var retryAfter = resp.Headers.RetryAfter?.Delta;
            return new Answer(resp.StatusCode, text, error, retryAfter);
        }
        catch (OperationCanceledException ex) when (!ct.IsCancellationRequested)
        {
            throw new PairnetsNetworkException(FriendlyErrors.TimedOut, ex);
        }
        catch (Exception ex) when (ex is HttpRequestException or IOException)
        {
            throw new PairnetsNetworkException(FriendlyErrors.Offline, ex, details: ex.Message);
        }
    }

    private sealed record StartRequest(string Name, string? System, string? AppVersion);

    private sealed record StartAnswer(string? DeviceCode, string? UserCode, string? VerificationUri, string? VerificationUriComplete, int? ExpiresIn, int? Interval);

    private sealed record LinkStartRequest(string Name, string? System, string? AppVersion, string Mode);

    private sealed record LinkStartAnswer(string? DeviceCode, string? Link, int? ExpiresIn);

    private sealed record RedeemRequest(string DeviceCode, string Code);

    private sealed record WrongCodeAnswer(int? TriesLeft);

    private sealed record PollRequest(string? DeviceCode);

    private sealed record PollAnswer(string? Status, string? AppToken, string? Email, PollNest? Nest, PollDevice? Device);

    /// <summary>The nest in an allowed sign-in. On a nest a friend shared: whose it is, "edit" or "view", and the folders (null: whole).</summary>
    private sealed record PollNest(string? Id, string? Label, string? ServerUrl, string? SharedBy = null, string? Access = null, List<string?>? Folders = null);

    private sealed record PollDevice(string? Id, string? Name, string? Key);
}
