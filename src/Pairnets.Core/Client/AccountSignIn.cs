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
public sealed record AccountSignInResult(Uri ServerUrl, string NestId, string NestLabel, DeviceKeyGrant Device, string Email);

/// <summary>An account sign-in's progress, for the window to draw.</summary>
/// <param name="Code">The code to show ("ABCD-EFGH"); the browser page shows the same one.</param>
/// <param name="VerifyUrl">The page to open in the browser, where the person signs in and presses Allow.</param>
/// <param name="ServerOffline">
/// Allowed in the browser, but the chosen server is not connected right now, so it cannot hand out this computer's key
/// yet. The flow keeps asking; <see cref="AccountSignInState.Message"/> says so.
/// </param>
/// <param name="Result">Set once <see cref="Stage"/> is <see cref="PairingStage.Approved"/>.</param>
public sealed record AccountSignInState(
    PairingStage Stage,
    string? Code = null,
    string? VerifyUrl = null,
    DateTimeOffset? ExpiresAt = null,
    string? Message = null,
    bool ServerOffline = false,
    AccountSignInResult? Result = null)
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
    private readonly Uri _service;
    private readonly TimeProvider _clock;
    private readonly HttpMessageHandler? _handler;
    private readonly TimeSpan? _interval;

    /// <param name="name">This computer's name, as the browser page and later the account page show it.</param>
    /// <param name="method">"email" or "google": the button pressed in the app, so the page starts that way (anything else is left out).</param>
    /// <param name="service">The Pairnets service (by default <see cref="Relay.DefaultServiceUrl"/>; tests use a stand-in).</param>
    /// <param name="interval">How often to ask (tests); by default what the service says.</param>
    public AccountSignIn(string name, string? method = null, Uri? service = null, TimeProvider? clock = null,
        HttpMessageHandler? handler = null, TimeSpan? interval = null)
    {
        _name = CleanName(name);
        _method = method is "email" or "google" ? method : null;
        _service = PairnetsApiClient.NormalizeBase(service ?? Relay.DefaultServiceUrl);
        _clock = clock ?? TimeProvider.System;
        _handler = handler;
        _interval = interval;
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
        bool startAtLogin, string protectedKey) =>
        Nest.SettingsAfterSignIn(previous, result.ServerUrl, result.Device, folder, startAtLogin, protectedKey, result.Email);

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
            var result = new AccountSignInResult(serverUrl, nestId, string.IsNullOrWhiteSpace(nest.Label) ? "Your server" : nest.Label.Trim(),
                new DeviceKeyGrant(device.Id, device.Name, device.Key), answer.Email?.Trim() ?? string.Empty);
            return Set(State with { Stage = PairingStage.Approved, Message = null, ServerOffline = false, Result = result });
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
        return link;
    }

    private static string StartRefused(Answer asked) => asked.Status switch
    {
        HttpStatusCode.TooManyRequests => "Too many sign-ins from this network. Wait a while, then try again.",
        >= HttpStatusCode.InternalServerError => "Pairnets is having trouble right now. Try again in a few minutes.",
        _ => $"Pairnets could not start a sign-in ({asked.Error ?? ((int)asked.Status).ToString(CultureInfo.InvariantCulture)}). Update Pairnets, then try again.",
    };

    /// <summary>The service takes names of at most 64 characters, without control characters.</summary>
    private static string CleanName(string? name)
    {
        var clean = new string((name ?? string.Empty).Where(c => !char.IsControl(c)).ToArray()).Trim();
        if (clean.Length == 0)
            clean = Environment.MachineName;
        var info = new StringInfo(clean);
        return info.LengthInTextElements > 64 ? info.SubstringByTextElements(0, 64).Trim() : clean;
    }

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
            throw new PairnetsNetworkException("The request timed out.", ex);
        }
        catch (Exception ex) when (ex is HttpRequestException or IOException)
        {
            throw new PairnetsNetworkException(ex.Message, ex);
        }
    }

    private sealed record StartRequest(string Name, string? System, string? AppVersion);

    private sealed record StartAnswer(string? DeviceCode, string? UserCode, string? VerificationUri, string? VerificationUriComplete, int? ExpiresIn, int? Interval);

    private sealed record PollRequest(string? DeviceCode);

    private sealed record PollAnswer(string? Status, string? AppToken, string? Email, PollNest? Nest, PollDevice? Device);

    private sealed record PollNest(string? Id, string? Label, string? ServerUrl);

    private sealed record PollDevice(string? Id, string? Name, string? Key);
}
