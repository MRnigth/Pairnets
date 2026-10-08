using System.Globalization;
using Pairnets.Core.Sync;

namespace Pairnets.Core.Client;

/// <summary>What the overview's big ring stands for.</summary>
public enum RingKind
{
    Done,
    Syncing,
    Waiting,
    Decision,
    Problem,
    Paused,
    Offline,
}

/// <summary>
/// The overview's big ring: how far round it goes, its colour (an S.* key), and what sits in the middle (a
/// percentage, or an icon key when there is no number).
/// </summary>
public sealed record OverviewRing(RingKind Kind, double Percent, string? CenterText, string? Caption, string BrushKey, string? IconKey)
{
    /// <summary>Syncing without a number yet: a short arc that turns.</summary>
    public bool Spins => Kind == RingKind.Syncing && CenterText is null;

    /// <summary>Waiting for the other computer: the ring breathes.</summary>
    public bool Breathes => Kind == RingKind.Waiting;

    public static OverviewRing From(StatusSnapshot s)
    {
        if (s.IsWaiting)
        {
            return s.WaitingPercent is { } w
                ? new(RingKind.Waiting, w, Percentage(w), "on the server", "S.Grey", null)
                : new(RingKind.Waiting, 0, null, null, "S.Grey", "I.Wait");
        }
        return s.Status switch
        {
            RunnerStatus.Syncing => s.OverallPercent is { } p
                ? new(RingKind.Syncing, p, Percentage(p), null, "S.Blue", null)
                : new(RingKind.Syncing, 25, null, null, "S.Blue", "I.Sync"),
            RunnerStatus.Idle => new(RingKind.Done, 100, null, null, "S.Green", "I.Check"),
            RunnerStatus.Blocked => new(RingKind.Decision, 100, null, null, "S.Orange", "I.Bang"),
            RunnerStatus.Paused => new(RingKind.Paused, 100, null, null, "S.Grey", "I.Pause"),
            RunnerStatus.Offline => new(RingKind.Offline, 100, null, null, "S.Grey", "I.CloudOff"),
            _ => new(RingKind.Problem, 100, null, null, "S.Red", "I.X"),
        };
    }

    private static string Percentage(int value) => value.ToString(CultureInfo.InvariantCulture) + "%";
}

/// <summary>The words under the overview's big headline: one lead sentence, then quieter lines.</summary>
public sealed record OverviewLines(string Lead, IReadOnlyList<string> Lines)
{
    public static OverviewLines From(StatusSnapshot s, DateTimeOffset now)
    {
        if (s.IsWaiting)
        {
            return new($"{s.WaitingFor!.Device} is uploading a big batch.",
                Present("Pairnets downloads it all in one go when it's done, so the two computers don't fight over the connection.",
                    Join(s.WaitingProgressText, "Your own changes still upload")));
        }
        if (s.Status == RunnerStatus.Syncing && s.IsTransferring)
            return new(s.BatchTitle + Direction(s), Present(s.OverallText, Join(s.SpeedText, s.LimitText)));
        if (s.Status == RunnerStatus.Idle)
        {
            var last = s.LastSyncAt is { } at ? "Last synced " + Format.Moment(at, now) : null;
            return new("Your folder and the server match.", Present(Join(last, s.ConnectionText)));
        }
        var detail = s.DetailText.Trim();
        var split = detail.IndexOf(". ", StringComparison.Ordinal);
        return split < 0
            ? new(detail, [])
            : new(detail[..(split + 1)], Present(detail[(split + 2)..]));
    }

    private static string Direction(StatusSnapshot s)
    {
        var uploads = s.Active.Count == 0 ? s.Operation == "upload" : s.Active.All(a => a.IsUpload);
        var downloads = s.Active.Count == 0 ? s.Operation == "download" : s.Active.All(a => !a.IsUpload);
        return uploads ? " to the server" : downloads ? " from the server" : string.Empty;
    }

    private static string Join(string? first, string? second) =>
        string.Join(" · ", new[] { first, second }.Where(t => !string.IsNullOrWhiteSpace(t)));

    private static IReadOnlyList<string> Present(params string?[] lines) =>
        lines.Where(l => !string.IsNullOrWhiteSpace(l)).Select(l => l!).ToList();
}
