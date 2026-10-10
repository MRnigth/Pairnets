namespace Pairnets.Core.Api;

/// <summary>
/// What the Pairnets service (sync.pairnets.app) answers itself when it cannot pass a request on to a nest (cloud/RELAY.md
/// §5). Its error bodies look like <c>{"error":"nest_offline","message":"…"}</c>; a nest's own errors are
/// <c>{"code":"…"}</c> (<see cref="ErrorBody"/>), so one is never read as the other. The apps use their own words for
/// these two, so every app says the same thing whatever the service's message.
/// </summary>
public static class ServiceErrors
{
    /// <summary>503: the service cannot reach the nest right now (it is off, or its tunnel is down). Retried like any network failure.</summary>
    public const string NestOffline = "nest_offline";

    /// <summary>404: the service knows no such nest (it was removed from the account). Only signing in again helps.</summary>
    public const string NestUnknown = "nest_unknown";

    /// <summary>
    /// 429: too many requests from this computer or address for a while (with Retry-After). Not a failure: the app waits
    /// and sends the same request again (<see cref="BackPressure"/>).
    /// </summary>
    public const string RateLimited = "rate_limited";

    public const string NestOfflineMessage ="Your server is not connected right now. Check that it is on.";

    public const string NestUnknownMessage = "This server is not linked to Pairnets any more. Sign in again to choose a server.";

    /// <summary>The exception a service error stands for, or null when <paramref name="code"/> is not one of the two above.</summary>
    internal static Exception? ToException(string? code) => code switch
    {
        NestOffline => new PairnetsNetworkException(NestOfflineMessage, code: NestOffline),
        NestUnknown => new PairnetsAuthException(NestUnknownMessage, NestUnknown),
        _ => null,
    };
}

/// <summary>Body of every error the Pairnets service answers under <c>/v1/*</c> and on relay addresses.</summary>
public sealed record ServiceErrorBody(string? Error, string? Message = null);
