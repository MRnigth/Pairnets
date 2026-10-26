using Pairnets.Core.Api;
using Pairnets.Core.Settings;

namespace Pairnets.Core.Client;

/// <summary>
/// Beta updates, the way Apple does it (docs/DECISIONS.md, "Beta updates"): the account joins the beta program once on
/// its account page; then each computer picks Off, Public beta or Developer beta in Settings › Updates. The account page
/// shows every computer's choice and can change it too. Both ends meet on the owner's own nest, which keeps the choice
/// per computer and only tells the owner's own computers (checked against each computer's key). The newest choice wins;
/// a nest that says nothing (offline, older) changes nothing.
/// </summary>
public static class BetaUpdates
{
    /// <summary>
    /// Takes what a nest said about the beta program (<c>GET /api/me</c>) into <paramref name="settings"/>. Returns
    /// whether anything changed (save it) and whether this computer's own choice is newer than the nest's (send it).
    /// A friend's nest or one this computer reaches with the shared password never decides anything: its answer is
    /// only kept as the reason the choice is greyed out, while no own nest has spoken.
    /// </summary>
    public static (bool Changed, bool SendOurs) Heard(ClientSettings settings, BetaInfo? info)
    {
        if (info is null)
            return (false, false);
        if (info.Reason is not null)
        {
            if (settings.BetaProgram is { Reason: null })
                return (false, false);
            var reason = info with { Choice = null };
            if (settings.BetaProgram == reason)
                return (false, false);
            settings.BetaProgram = reason;
            return (true, false);
        }
        var changed = false;
        var program = new BetaInfo(info.Member, info.TermsVersion, info.Channels is null ? [] : [.. info.Channels]);
        if (!SameProgram(settings.BetaProgram, program))
        {
            settings.BetaProgram = program;
            changed = true;
        }
        if (info.Choice is { } theirs && (settings.BetaChannelAt is not { } ours || theirs.At > ours))
        {
            var channel = ReleaseChannel.IsBeta(theirs.Channel) ? theirs.Channel : null;
            if (settings.BetaChannel != channel || settings.BetaChannelAt != theirs.At)
            {
                settings.BetaChannel = channel;
                settings.BetaChannelAt = theirs.At;
                changed = true;
            }
            return (changed, false);
        }
        var sendOurs = settings.BetaChannelAt is { } at && (info.Choice is null || at > info.Choice.At);
        return (changed, sendOurs);
    }

    /// <summary>The choice made in Settings (Off is <see cref="ReleaseChannel.Stable"/>).</summary>
    public static void Choose(ClientSettings settings, string channel, DateTimeOffset now)
    {
        settings.BetaChannel = ReleaseChannel.IsBeta(channel) ? channel : null;
        settings.BetaChannelAt = now;
    }

    /// <summary>What to send the nest for this computer's choice.</summary>
    public static BetaChoiceRequest Request(ClientSettings settings) =>
        new(ReleaseChannel.WireName(ReleaseChannel.Normalize(settings.BetaChannel)), settings.BetaChannelAt ?? DateTimeOffset.UnixEpoch);

    /// <summary>
    /// Tells one own nest this computer's choice and takes in what it keeps. Returns true when <paramref name="settings"/>
    /// changed. A nest that cannot be reached, or is too old, is left for the next check-in.
    /// </summary>
    public static async Task<bool> SendAsync(ClientSettings settings, PairnetsApiClient api, CancellationToken ct)
    {
        try
        {
            if (await api.SetBetaChoiceAsync(Request(settings), ct).ConfigureAwait(false) is not { } kept)
                return false;
            return Heard(settings, (settings.BetaProgram ?? new BetaInfo(false)) with { Choice = kept, Reason = null }).Changed;
        }
        catch (Exception ex) when (ex is PairnetsNetworkException or PairnetsAuthException or PairnetsProtocolException or OperationCanceledException)
        {
            return false;
        }
    }

    /// <summary>True when <paramref name="channel"/> can be picked now (Off always can).</summary>
    public static bool CanChoose(ClientSettings settings, string channel) =>
        !ReleaseChannel.IsBeta(channel) || settings.BetaProgram is { Member: true, Reason: null, Channels: { } allowed } && allowed.Contains(channel);

    private static bool SameProgram(BetaInfo? a, BetaInfo b) =>
        a is not null && a.Reason is null && a.Member == b.Member && a.TermsVersion == b.TermsVersion
        && (a.Channels ?? []).SequenceEqual(b.Channels ?? []);
}

/// <summary>The words for Beta updates, the same in both apps.</summary>
public static class BetaTexts
{
    public const string Title = "Beta updates";
    public const string Feedback = "Send beta feedback";
    public const string FeedbackHint = "Found something odd? Tell us.";
    public const string OpenAccountPage = "Open account page";
    public const string DeveloperOnlyInvited = "Developer beta is for invited testers.";

    /// <summary>The line under "Beta updates" in Settings.</summary>
    public static string Explain(ClientSettings settings) => settings.BetaProgram switch
    {
        { Reason: BetaInfo.ReasonFriend } => "Beta updates are chosen through your own nest. This computer only uses nests shared with you.",
        { Reason: BetaInfo.ReasonSharedPassword } =>
            "This computer uses the nest's shared password. Sign in with your Pairnets account to get beta updates.",
        { Reason: not null } => "Beta updates need a nest linked to a Pairnets account.",
        null => "Join the beta program on your account page first. Then choose here. (Your nest may need updating first.)",
        { Member: false } => "Join the beta program on your account page first. Then choose here.",
        _ when ReleaseChannel.IsBeta(settings.EffectiveChannel) =>
            "New versions before everyone else. Public beta is nearly ready; Developer beta is the newest work.",
        _ => "Off. You can turn it on any time: Public beta is nearly ready; Developer beta is the newest work.",
    };

    /// <summary>True when the "Open account page" button belongs under the choice (the account has not joined yet).</summary>
    public static bool NeedsJoin(ClientSettings settings) => settings.BetaProgram is null or { Member: false, Reason: null };
}
