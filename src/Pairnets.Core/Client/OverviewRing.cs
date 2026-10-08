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
