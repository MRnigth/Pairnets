using System.Globalization;
using Pairnets.Core.Sync;

namespace Pairnets.Core.Client;

/// <summary>The states the Overview and the nest picture show.</summary>
public enum NestMapState
{
    Syncing,
    UpToDate,
    /// <summary>Waiting for another computer to finish a big upload.</summary>
    Waiting,
    /// <summary>Stopped before deleting (or another decision the person has to make).</summary>
    Stopped,
    SignIn,
    SignedOut,
    /// <summary>Signed out with this computer's own Sign out button: grey, nothing is wrong.</summary>
    SignedOutByYou,
    Offline,
    Paused,
    FolderMissing,
}

/// <summary>How a line to the nest is drawn.</summary>
public enum NestLineKind
{
    /// <summary>Blue, with a speed pill.</summary>
    Active,
    /// <summary>Green: in sync.</summary>
    Ok,
    /// <summary>Grey, solid: online and quiet.</summary>
    Idle,
    /// <summary>Grey, dashed: next in line, paused, switched off ("off since 10:45"), or last seen.</summary>
    Next,
    /// <summary>Amber, dashed.</summary>
    Warn,
    /// <summary>Red, dashed.</summary>
    Bad,
    /// <summary>Red, dashed, with a red cross in the middle: can't reach (a real problem, <see cref="Presence.Lost"/>).</summary>
    Unreachable,
}

/// <summary>The colour of the small dot on an icon.</summary>
public enum NestDot
{
    Ok,
    Warn,
    Bad,
    Off,
}

/// <summary>This computer's line to the nest. <see cref="Label"/> is the pill text for <see cref="NestLineKind.Active"/> ("↑ 4.90 MB/s"), otherwise the word on the line.</summary>
public sealed record NestMapThis(NestLineKind Kind, string Label, NestDot Dot, DeviceIconKind Icon)
{
    public bool HasPill => Kind == NestLineKind.Active && Label.Length > 0;

    /// <summary>True while the files go towards the nest (the pill starts with an up arrow).</summary>
    public bool GoesUp => Kind == NestLineKind.Active && Label.StartsWith('↑');
    public bool HasCross => Kind == NestLineKind.Unreachable;
}

/// <summary>One other computer's line. <see cref="Speed"/> is set only while it really moves files ("↓ 4.10 MB/s").</summary>
public sealed record NestMapOther(string Name, DeviceIconKind Icon, NestLineKind Kind, string? Speed, string Status, NestDot Dot, bool Online)
{
    public bool HasPill => Kind == NestLineKind.Active && !string.IsNullOrEmpty(Speed);

    /// <summary>True while this computer sends to the nest (its status says "uploading").</summary>
    public bool GoesUp => Kind == NestLineKind.Active && Status.StartsWith("uploading", StringComparison.Ordinal);
    public bool HasCross => Kind == NestLineKind.Unreachable;
}

/// <summary>
/// What the nest picture on the Overview shows, as plain values (no UI types): this computer's line, the nest, and a line
/// per other computer. Built from <see cref="StatusSnapshot"/>; the wording follows the "Hub" design.
/// </summary>
public sealed record NestMapModel(
    NestMapState State,
    NestMapThis This,
    NestDot NestDot,
    string NestSub,
    IReadOnlyList<NestMapOther> Others,
    int MoreCount)
{
    /// <summary>At most this many other computers are drawn; the rest are "+N more".</summary>
    public const int MaxOthers = 5;

    /// <summary>"+3 more" (opens the Devices page), or null when every computer is drawn.</summary>
    public string? MoreText => MoreCount > 0 ? "+" + MoreCount.ToString(CultureInfo.InvariantCulture) + " more" : null;

    /// <summary>Which state applies. Same decisions as the Overview's status card.</summary>
    public static NestMapState StateOf(StatusSnapshot s)
    {
        if (s.Status == RunnerStatus.Paused)
            return NestMapState.Paused;
        if (s.Status == RunnerStatus.Offline)
            return NestMapState.Offline;
        if (s.Status == RunnerStatus.Blocked)
        {
            return s.BlockReason switch
            {
                BlockReason.SignedOut => NestMapState.SignedOut,
                BlockReason.SignedOutByYou => NestMapState.SignedOutByYou,
                BlockReason.SignInRequired => NestMapState.SignIn,
                BlockReason.FolderMissing or BlockReason.MarkerMissing or BlockReason.MarkerMismatch => NestMapState.FolderMissing,
                _ => NestMapState.Stopped,
            };
        }
        if (s.Status == RunnerStatus.Error)
            return NestMapState.Stopped;
        if (s.IsWaiting)
            return NestMapState.Waiting;
        return s.Status == RunnerStatus.Syncing ? NestMapState.Syncing : NestMapState.UpToDate;
    }

    public static NestMapModel Build(StatusSnapshot s, string? thisDevice, DateTimeOffset now)
    {
        var state = StateOf(s);
        var supportsIcons = s.DeviceIconsSupported;
        var devices = s.Devices ?? [];

        var own = devices.FirstOrDefault(d => string.Equals(d.Name, thisDevice, StringComparison.OrdinalIgnoreCase));
        var ownIcon = own is not null ? DeviceIcon.For(own, supportsIcons) : DeviceIcon.Guess(thisDevice);

        var (kind, label, thisDot, nestDot) = state switch
        {
            NestMapState.Syncing => (NestLineKind.Active, OwnSpeed(s), NestDot.Ok, NestDot.Ok),
            NestMapState.UpToDate => (NestLineKind.Ok, "in sync", NestDot.Ok, NestDot.Ok),
            NestMapState.Waiting => (NestLineKind.Next, "next", NestDot.Ok, NestDot.Ok),
            NestMapState.Stopped => (NestLineKind.Warn, "stopped", NestDot.Warn, NestDot.Ok),
            NestMapState.SignIn => (NestLineKind.Warn, "sign in first", NestDot.Warn, NestDot.Warn),
            NestMapState.SignedOut => (NestLineKind.Bad, "signed out", NestDot.Bad, NestDot.Bad),
            NestMapState.SignedOutByYou => (NestLineKind.Next, "signed out", NestDot.Off, NestDot.Off),
            NestMapState.Offline => (NestLineKind.Unreachable, "can’t reach", NestDot.Off, NestDot.Bad),
            NestMapState.Paused => (NestLineKind.Next, "paused", NestDot.Off, NestDot.Ok),
            _ => (NestLineKind.Warn, "folder missing", NestDot.Warn, NestDot.Ok),
        };

        var sub = state switch
        {
            NestMapState.Offline => "can’t reach it",
            NestMapState.SignedOut => "removed this one",
            NestMapState.SignedOutByYou => "signed out",
            _ => s.Server?.DiskFreeBytes is { } free ? Format.Bytes(free) + " free" : "online",
        };

        var candidates = devices
            .Where(d => !string.Equals(d.Name, thisDevice, StringComparison.OrdinalIgnoreCase) && (d.Online || now - d.LastSeen <= DeviceMap.ForgetAfter))
            .Select(d => (Device: d, Report: Live(s, d.Name, now)))
            .OrderByDescending(x => x.Report is not null)
            .ThenByDescending(x => x.Device.Online || x.Report is not null)
            .ThenByDescending(x => x.Device.LastSeen)
            .ToList();

        var others = candidates.Take(MaxOthers).Select(x => Other(s, state, x.Device, x.Report, supportsIcons, now)).ToList();
        return new NestMapModel(state, new NestMapThis(kind, label, thisDot, ownIcon), nestDot, sub, others, Math.Max(0, candidates.Count - MaxOthers));
    }

    /// <summary>"↑ 4.90 MB/s" from this computer's own speed, or "syncing" / "checking" while nothing moves yet.</summary>
    private static string OwnSpeed(StatusSnapshot s)
    {
        if (s.BytesPerSecond >= 1)
        {
            var up = s.GoingUp && !s.GoingDown ? true
                : s.GoingDown && !s.GoingUp ? false
                : s.UpBytesPerSecond >= s.DownBytesPerSecond;
            return (up ? "↑ " : "↓ ") + Format.Speed(s.BytesPerSecond);
        }
        return s.IsChecking ? "checking" : "syncing";
    }

    /// <summary>The other computer's report, only while it is fresh and really moving files.</summary>
    private static TransferReport? Live(StatusSnapshot s, string name, DateTimeOffset now)
    {
        foreach (var (device, peer) in s.Peers)
        {
            if (string.Equals(device, name, StringComparison.OrdinalIgnoreCase))
                return peer.IsMoving(now) ? peer.Report : null;
        }
        return null;
    }

    private static NestMapOther Other(StatusSnapshot s, NestMapState state, DeviceInfo d, TransferReport? live, bool supportsIcons, DateTimeOffset now)
    {
        var icon = DeviceIcon.For(d, supportsIcons);
        var online = d.Online || live is not null;
        var seen = "last seen " + Presences.Since(d.LastSeen, now);

        // This computer cannot reach the nest: it knows nothing newer about the others than when they were last seen.
        if (state is NestMapState.Offline or NestMapState.SignedOutByYou)
            return new NestMapOther(d.Name, icon, NestLineKind.Next, null, seen, NestDot.Off, false);
        // Red only for a real problem (it vanished while moving files); switched off is grey and dashed.
        if (!online)
        {
            return Presences.Of(s, d.Name, online, now) == Presence.Lost
                ? new NestMapOther(d.Name, icon, NestLineKind.Unreachable, null, "can’t reach", NestDot.Bad, false)
                : new NestMapOther(d.Name, icon, NestLineKind.Next, null, Presences.OffSince(d.LastSeen, now), NestDot.Off, false);
        }

        if (live is not null)
        {
            var down = live.DownBytesPerSecond >= 1 && live.DownBytesPerSecond >= live.UpBytesPerSecond;
            var up = !down && live.UpBytesPerSecond >= 1;
            if (up)
            {
                var files = live.FilesLeft > 0 ? live.FilesLeft : live.FilesTotal;
                return new NestMapOther(d.Name, icon, NestLineKind.Active, "↑ " + Format.Speed(live.UpBytesPerSecond),
                    files > 0 ? "uploading " + Format.Count(files) + (files == 1 ? " file" : " files") : "uploading", NestDot.Ok, true);
            }
            if (down)
                return new NestMapOther(d.Name, icon, NestLineKind.Active, "↓ " + Format.Speed(live.DownBytesPerSecond), "downloading", NestDot.Ok, true);
            // Moving, but no byte yet (or stalled): the line is blue without a speed.
            return new NestMapOther(d.Name, icon, NestLineKind.Active, null, live.Uploading ? "uploading" : "downloading", NestDot.Ok, true);
        }

        return state switch
        {
            NestMapState.UpToDate => new NestMapOther(d.Name, icon, NestLineKind.Ok, null, "in sync", NestDot.Ok, true),
            NestMapState.Waiting => new NestMapOther(d.Name, icon, NestLineKind.Next, null, "next", NestDot.Ok, true),
            _ => new NestMapOther(d.Name, icon, NestLineKind.Idle, null, "online", NestDot.Ok, true),
        };
    }
}
