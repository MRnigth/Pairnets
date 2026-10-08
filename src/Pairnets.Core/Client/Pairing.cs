using System.Globalization;
using System.Net;
using System.Text.Json;
using Pairnets.Core.Api;
using Pairnets.Core.Settings;

namespace Pairnets.Core.Client;

/// <summary>What the "Your nest" field found.</summary>
public enum NestCheckStatus
{
    /// <summary>Nothing typed yet.</summary>
    Empty,

    /// <summary>Not an address.</summary>
    Invalid,

    /// <summary>A Pairnets nest that lets computers sign in.</summary>
    Found,

    /// <summary>A Pairnets server without its own name yet: connect with the address and token instead.</summary>
    NoSignIn,

    /// <summary>Something answered, but not a Pairnets server.</summary>
    NotPairnets,

    /// <summary>Nothing answered (wrong name, server down, no internet).</summary>
    Unreachable,
}

/// <summary>The result of checking an address typed in the sign-in window.</summary>
public sealed record NestCheck(NestCheckStatus Status, Uri? Url, ServerHello? Hello, string Message)
{
    public bool CanSignIn => Status == NestCheckStatus.Found;
}

/// <summary>Finding the nest from what someone typed: "nest.pairnets.app", an IP address, or a full URL.</summary>
public static class Nest
{
    /// <summary>
    /// "nest.pairnets.app" → https://nest.pairnets.app/ (names get HTTPS); "100.x.y.z" → http://100.x.y.z:5075/
    /// (a bare IP address is the old plain server address); full URLs stay as typed. Null when it cannot be an address.
    /// </summary>
    public static Uri? ParseAddress(string? text)
    {
        var typed = text?.Trim().TrimEnd('/');
        if (string.IsNullOrEmpty(typed) || typed.Contains(' ', StringComparison.Ordinal))
            return null;
        if (typed.Contains("://", StringComparison.Ordinal))
            return PairnetsApiClient.TryParseServerUrl(typed, out var url) ? url : null;
        var hostPart = typed.Split('/')[0];
        var host = hostPart.Contains(':', StringComparison.Ordinal) && !hostPart.StartsWith('[') ? hostPart[..hostPart.LastIndexOf(':')] : hostPart;
        var isIp = IPAddress.TryParse(host.Trim('[', ']'), out _);
        var withScheme = isIp
            ? "http://" + (hostPart == host ? hostPart + ":5075" : hostPart)
            : "https://" + hostPart;
        if (!host.Contains('.', StringComparison.Ordinal) && !isIp && host != "localhost")
            return null; // "nest" alone is not something the internet's DNS can find
        return PairnetsApiClient.TryParseServerUrl(withScheme, out var parsed) ? parsed : null;
    }

    /// <summary>
    /// The web address (scheme, host, port) behind a link a server gave us, or null when it is not one we would open in a
    /// browser: only https (or http to the very host that was typed), no sign-in details in it, nothing else. The apps hand
    /// these links to the operating system, so a server must not be able to make them open a file, a program or a network share.
    /// </summary>
    public static string? SafeOrigin(string? text, Uri? typed = null)
    {
        if (string.IsNullOrWhiteSpace(text) || !Uri.TryCreate(text.Trim(), UriKind.Absolute, out var uri)
            || uri.HostNameType == UriHostNameType.Unknown || uri.Host.Length == 0 || uri.UserInfo.Length > 0)
            return null;
        var sameHost = typed is not null && string.Equals(uri.Host, typed.Host, StringComparison.OrdinalIgnoreCase);
        var allowed = uri.Scheme == Uri.UriSchemeHttps || (uri.Scheme == Uri.UriSchemeHttp && sameHost);
        return allowed ? uri.GetLeftPart(UriPartial.Authority) : null;
    }

    /// <summary>
    /// The page where a computer's sign-in is approved, built here from the nest's address and the code (never taken
    /// as given). <paramref name="method"/> ("google" or "email") and <paramref name="email"/> say which button was
    /// pressed in the app, so the nest's sign-in page starts that way right away.
    /// </summary>
    public static string ApprovalLink(string origin, string code, string? method = null, string? email = null)
    {
        var link = $"{origin}/link?code={Uri.EscapeDataString(code)}";
        if (method is not null)
            link += "&method=" + Uri.EscapeDataString(method);
        if (email is not null)
            link += "&email=" + Uri.EscapeDataString(email);
        return link;
    }

    /// <summary>
    /// Shaped like an email address (one @ with something on both sides, no spaces). The nest decides
    /// whether it really is the owner's; this only keeps empty or half-typed addresses off the buttons.
    /// </summary>
    public static bool LooksLikeEmail(string? text)
    {
        var typed = text?.Trim() ?? string.Empty;
        var at = typed.IndexOf('@', StringComparison.Ordinal);
        return at > 0 && at == typed.LastIndexOf('@') && at < typed.Length - 1 && !typed.Contains(' ', StringComparison.Ordinal);
    }

    /// <summary>Checks the typed address: is there a Pairnets nest that lets this computer sign in? Never throws.</summary>
    public static async Task<NestCheck> CheckAsync(string? text, HttpMessageHandler? handler = null, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(text))
            return new(NestCheckStatus.Empty, null, null, string.Empty);
        var url = ParseAddress(text);
        if (url is null)
            return new(NestCheckStatus.Invalid, null, null, "That is not an address. Type your nest's name, such as nest.example.com.");
        using var client = new PairnetsApiClient(url, string.Empty, Environment.MachineName, handler);
        try
        {
            var hello = await client.GetHelloAsync(ct).ConfigureAwait(false);
            if (hello is null)
                return new(NestCheckStatus.NoSignIn, url, null, "This server is too old to sign in. Update it, or use the address and token instead.");
            if (!hello.SignIn)
                return new(NestCheckStatus.NoSignIn, url, hello, "This server has no name of its own yet, so it cannot approve sign-ins. Use the address and token instead.");
            return new(NestCheckStatus.Found, url, hello, $"Found it (Pairnets server {hello.ServerVersion ?? "?"})");
        }
        catch (PairnetsNetworkException)
        {
            return new(NestCheckStatus.Unreachable, url, null, "Can't reach it. Check the name, and that this computer is online.");
        }
        catch (Exception ex) when (ex is PairnetsProtocolException or PairnetsAuthException or JsonException)
        {
            return new(NestCheckStatus.NotPairnets, url, null, "Something answered, but it is not a Pairnets server.");
        }
    }

    /// <summary>
    /// The settings after signing in: this computer's own key (to protect and keep), its name as the nest
    /// recorded it, the address that worked, and the chosen folder. Everything else carries over.
    /// </summary>
    public static ClientSettings SettingsAfterSignIn(ClientSettings? previous, Uri server, DeviceKeyGrant grant, string folder,
        bool startAtLogin, string protectedKey)
    {
        var next = previous?.Clone() ?? new ClientSettings();
        next.ServerUrl = PairnetsApiClient.NormalizeBase(server).ToString();
        next.ProtectedToken = protectedKey;
        next.DeviceId = grant.Id;
        next.DeviceName = grant.Name;
        next.Folder = folder;
        next.StartWithWindows = startAtLogin;
        next.FirstRunCompleted = true;
        next.Paused = false;
        return next;
    }
}

/// <summary>Where a sign-in stands.</summary>
public enum PairingStage
{
    /// <summary>Asking the nest for a code.</summary>
    Starting,

    /// <summary>Showing the code until someone approves it on the nest.</summary>
    Waiting,

    Approved,
    Denied,
    Expired,

    /// <summary>The nest could not be asked at all (see the message).</summary>
    Failed,
}

/// <summary>A sign-in's progress, for the window to draw.</summary>
public sealed record PairingState(
    PairingStage Stage,
    string? Code = null,
    string? VerifyUrl = null,
    DateTimeOffset? ExpiresAt = null,
    string? Message = null,
    DeviceKeyGrant? Grant = null)
{
    /// <summary>"code expires in 9:41" while waiting.</summary>
    public string ExpiresText(DateTimeOffset now)
    {
        if (ExpiresAt is not { } at)
            return string.Empty;
        var left = at - now;
        if (left <= TimeSpan.Zero)
            return "the code has expired";
        return "code expires in " + ((int)left.TotalMinutes).ToString(CultureInfo.InvariantCulture) + ":" + left.Seconds.ToString("00", CultureInfo.InvariantCulture);
    }

    public bool IsFinished => Stage is PairingStage.Approved or PairingStage.Denied or PairingStage.Expired or PairingStage.Failed;
}

/// <summary>
/// "Sign in with your browser": asks the nest for a code, then asks every couple of seconds whether
/// it was approved, until it is, is turned down, or expires. A dropped connection on the way is
/// retried quietly; the code keeps working. The windows only draw <see cref="State"/>.
/// </summary>
public sealed class PairingFlow
{
    private readonly Uri _server;
    private readonly string _name;
    private readonly string? _method;
    private readonly string? _email;
    private readonly TimeProvider _clock;
    private readonly HttpMessageHandler? _handler;
    private readonly TimeSpan? _interval;

    public PairingFlow(Uri server, string name, string? method = null, string? email = null,
        TimeProvider? clock = null, HttpMessageHandler? handler = null, TimeSpan? interval = null)
    {
        _server = server;
        _name = name;
        _method = method;
        _email = email;
        _clock = clock ?? TimeProvider.System;
        _handler = handler;
        _interval = interval;
    }

    public PairingState State { get; private set; } = new(PairingStage.Starting);

    /// <summary>Raised whenever <see cref="State"/> changes (on a background thread).</summary>
    public event Action<PairingState>? Changed;

    /// <summary>Runs the whole sign-in. Returns the final state; cancelling returns quietly.</summary>
    public async Task<PairingState> RunAsync(CancellationToken ct)
    {
        using var api = new PairnetsApiClient(_server, string.Empty, _name, _handler);
        PairStartResponse? start;
        try
        {
            start = await api.StartPairingAsync(new PairStartRequest(_name, SystemName(), PairnetsInfo.ProductVersion), ct).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            return State;
        }
        catch (PairnetsNetworkException ex)
        {
            return Set(new(PairingStage.Failed, Message: "Can't reach your nest: " + ex.Message));
        }
        catch (PairnetsProtocolException ex)
        {
            return Set(new(PairingStage.Failed, Message: ex.Message));
        }
        if (start is null)
            return Set(new(PairingStage.Failed, Message: "This server cannot approve sign-ins. Update it, or connect with the address and token instead."));

        var expires = _clock.GetUtcNow().AddSeconds(start.ExpiresInSeconds);
        // The link comes from the nest we were pointed at, but it is rebuilt from its address and the code: whatever else a
        // server puts in "verifyUrl" never reaches the operating system.
        var origin = Nest.SafeOrigin(start.VerifyUrl, _server) ?? Nest.SafeOrigin(_server.ToString(), _server);
        Set(new(PairingStage.Waiting, start.Code, origin is null ? null : Nest.ApprovalLink(origin, start.Code, _method, _email), expires));
        var interval = _interval ?? TimeSpan.FromSeconds(Math.Clamp(start.IntervalSeconds, 1, 30));
        while (!ct.IsCancellationRequested)
        {
            try
            {
                await Task.Delay(interval, _clock, ct).ConfigureAwait(false);
                var answer = await api.PollPairingAsync(start.PollToken, ct).ConfigureAwait(false);
                switch (answer.Status)
                {
                    case PairPollResponse.Approved when answer is { Id: { } id, Name: { } name, Key: { } key }:
                        return Set(State with { Stage = PairingStage.Approved, Message = null, Grant = new DeviceKeyGrant(id, name, key) });
                    case PairPollResponse.Denied:
                        return Set(State with { Stage = PairingStage.Denied, Message = "Your nest said no." });
                    case PairPollResponse.Expired:
                    case PairPollResponse.Used:
                        return Set(State with { Stage = PairingStage.Expired, Message = "The code expired." });
                    default:
                        if (State.Message is not null)
                            Set(State with { Message = null });
                        break;
                }
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                break;
            }
            catch (Exception ex) when (ex is PairnetsNetworkException or PairnetsProtocolException)
            {
                if (_clock.GetUtcNow() >= expires)
                    return Set(State with { Stage = PairingStage.Expired, Message = "The code expired." });
                Set(State with { Message = "Lost the connection to your nest; still trying…" });
            }
        }
        return State;
    }

    private PairingState Set(PairingState state)
    {
        State = state;
        Changed?.Invoke(state);
        return state;
    }

    private static string SystemName() =>
        OperatingSystem.IsWindows() ? "Windows" : OperatingSystem.IsMacOS() ? "macOS" : OperatingSystem.IsLinux() ? "Linux" : "other";
}
