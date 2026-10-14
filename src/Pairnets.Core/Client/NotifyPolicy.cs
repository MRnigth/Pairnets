using Pairnets.Core.Settings;

namespace Pairnets.Core.Client;

/// <summary>The kinds of system notifications, each with its own switch on the Account page.</summary>
public enum NoticeKind
{
    /// <summary>Another computer wants to join the nest.</summary>
    JoinRequest,

    /// <summary>Something needs the person: conflicts, blocked syncs, sign-in problems, a full disk.</summary>
    Attention,

    /// <summary>A new Pairnets version, or the server was updated.</summary>
    Update,

    /// <summary>Plain news ("caught up after being away"): always shown.</summary>
    Info,
}

/// <summary>Decides whether a notification (by the key the apps give it) is shown.</summary>
public static class NotifyPolicy
{
    public static NoticeKind KindOf(string key) =>
        key.StartsWith("join:", StringComparison.Ordinal) ? NoticeKind.JoinRequest
        : key.StartsWith("update:", StringComparison.Ordinal) || key == "server-updated" ? NoticeKind.Update
        : key == "catchup" ? NoticeKind.Info
        : NoticeKind.Attention;

    public static bool Allows(ClientSettings settings, string key) => KindOf(key) switch
    {
        NoticeKind.JoinRequest => settings.NotifyJoinRequests,
        NoticeKind.Attention => settings.NotifyAttention,
        NoticeKind.Update => settings.NotifyUpdates,
        _ => true,
    };
}
