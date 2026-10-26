using System.Globalization;

namespace Pairnets.Core.Client;

/// <summary>What a row of the nest's downloads shows, which also decides its buttons.</summary>
public enum NestDownloadRowKind
{
    Running,

    /// <summary>Waiting for its turn, or to try again after a dropped connection: Cancel.</summary>
    Waiting,

    /// <summary>Show in Files and Clear.</summary>
    Done,

    /// <summary>Try again and Remove.</summary>
    Failed,

    /// <summary>Downloaded on an encrypted nest, waiting without end-to-end encryption for a computer to lock it: Remove.</summary>
    Held,
}

/// <summary>One row of "Downloads on the nest".</summary>
/// <param name="Name">"ubuntu-24.04-desktop-amd64.iso".</param>
/// <param name="Line">"43% · 2.0 GB of 4.7 GB · 12.4 MB/s · about 4 min left", "Done · 4.7 GB in Downloads", "Stopped: …".</param>
/// <param name="Source">"from releases.ubuntu.com".</param>
/// <param name="Percent">The bar, while running with a known size.</param>
/// <param name="Folder">Where the file goes ("Downloads", "" = the top), for "Show in Files".</param>
public sealed record NestDownloadRow(string Id, string Name, string Line, string Source, NestDownloadRowKind Kind, int? Percent, string Folder)
{
    public bool IsRunning => Kind == NestDownloadRowKind.Running;

    public bool IsWaiting => Kind == NestDownloadRowKind.Waiting;

    public bool IsDone => Kind == NestDownloadRowKind.Done;

    public bool IsFailed => Kind == NestDownloadRowKind.Failed;

    public bool IsHeld => Kind == NestDownloadRowKind.Held;

    /// <summary>Running or waiting: Cancel.</summary>
    public bool CanCancel => Kind is NestDownloadRowKind.Running or NestDownloadRowKind.Waiting;

    public double PercentValue => Percent ?? 0;

    public bool HasPercent => Percent is not null;

    public string CancelLabel => "Cancel downloading " + Name;

    public string ClearLabel => "Clear " + Name + " from the list";

    public string RetryLabel => "Try downloading " + Name + " again";

    public string RemoveLabel => "Remove " + Name + " from the list";

    public string ShowLabel => "Show " + Name + " on the Files page";
}

/// <summary>
/// "Downloads on the nest" on the owner's Computers page (1.0.61.8): paste a link, and the nest downloads the file into one
/// of its folders by itself, so this computer may be switched off meanwhile. A heading with a summary and "Add a link…",
/// then one row per download from <c>GET /api/downloads</c>. Hidden for friends and linked nests (only the owner may make
/// the nest download); an older nest says to update it, an encrypted one that it can't. Both apps draw this model.
/// </summary>
/// <param name="CanAdd">"Add a link…" shows.</param>
/// <param name="Note">Instead of rows: why there are none, or why nothing can be added.</param>
public sealed record NestDownloadsSection(bool Visible, bool CanAdd, string Summary, string Note, IReadOnlyList<NestDownloadRow> Rows)
{
    public static readonly NestDownloadsSection Hidden = new(false, false, string.Empty, string.Empty, []);

    public const string Heading = "Downloads on the nest";

    public const string EmptyNote = "Paste a link and the nest downloads the file by itself, into its Downloads folder. You can turn this computer off meanwhile: the file is waiting when you come back.";

    public const string OldNestNote = "Update your nest to let it download links for you.";

    public const string SealedNote = "Your nest is encrypted. Files the nest downloads for you aren't end-to-end encrypted on it; everything else stays encrypted.";

    /// <summary>The warning in the "Download a link" window on an encrypted nest, before anything starts.</summary>
    public const string SealedWarning = "This file won't be end-to-end encrypted on the nest. Everything else stays encrypted.";

    public static NestDownloadsSection From(StatusSnapshot s, DateTimeOffset now)
    {
        if (!s.Access.IsOwner)
            return Hidden;
        if (s.NestDownloadsUnsupported)
            return new NestDownloadsSection(true, false, string.Empty, OldNestNote, []);
        if (s.NestDownloads is not { } list)
            return new NestDownloadsSection(true, false, string.Empty, "Loading…", []);
        var rows = list.Downloads.Select(d => Row(d, list.Downloads, now)).ToList();
        var note = list.Sealed || s.Server?.Sealed == true ? SealedNote : rows.Count == 0 ? EmptyNote : string.Empty;
        return new NestDownloadsSection(true, true, SummaryOf(list.Downloads), note, rows);
    }

    /// <summary>"2 downloading · 1 waiting", "1 done", "Nothing yet".</summary>
    public static string SummaryOf(IReadOnlyList<NestDownload> downloads)
    {
        var parts = new List<string>();
        void Add(int n, string what)
        {
            if (n > 0)
                parts.Add(n.ToString(CultureInfo.InvariantCulture) + " " + what);
        }
        Add(downloads.Count(d => d.State == NestDownload.Running), "downloading");
        Add(downloads.Count(d => d.State == NestDownload.Queued), "waiting");
        Add(downloads.Count(d => d.State == NestDownload.Done && !d.Plain), "done");
        Add(downloads.Count(d => d.State == NestDownload.Done && d.Plain), "not end-to-end encrypted");
        Add(downloads.Count(d => d.State == NestDownload.Failed), "stopped");
        return parts.Count > 0 ? string.Join(" · ", parts) : "Nothing yet";
    }

    public static NestDownloadRow Row(NestDownload d, IReadOnlyList<NestDownload> all, DateTimeOffset now)
    {
        var name = d.Name.Length > 0 ? d.Name : d.Source;
        var source = "from " + d.Source;
        switch (d.State)
        {
            case NestDownload.Running:
            {
                int? percent = d.Total is > 0 and var total ? (int)Math.Clamp(d.Bytes * 100 / total, 0, 100) : null;
                var parts = new List<string>();
                if (percent is { } p)
                    parts.Add(p.ToString(CultureInfo.InvariantCulture) + "%");
                parts.Add(d.Total is > 0 ? $"{Format.Bytes(d.Bytes)} of {Format.Bytes(d.Total.Value)}" : Format.Bytes(d.Bytes));
                if (d.BytesPerSecond >= 1)
                {
                    parts.Add(Format.Speed(d.BytesPerSecond));
                    if (d.Total is > 0 && d.Total.Value > d.Bytes)
                        parts.Add(Format.Duration(TimeSpan.FromSeconds((d.Total.Value - d.Bytes) / d.BytesPerSecond)) + " left");
                }
                else
                {
                    parts.Add("starting…");
                }
                return new NestDownloadRow(d.Id, name, string.Join(" · ", parts), source, NestDownloadRowKind.Running, percent, d.Folder);
            }
            case NestDownload.Done when d.Plain:
                return new NestDownloadRow(d.Id, name, $"Done · {Format.Bytes(d.Bytes)} in {FolderName(d.Folder)} · not end-to-end encrypted",
                    source, NestDownloadRowKind.Held, null, d.Folder);
            case NestDownload.Done:
            {
                var where = FolderName(d.Folder);
                var line = $"Done · {Format.Bytes(d.Bytes)} in {where}" + (d.Finished is { } at ? " · " + Format.RelativeTime(at, now) : string.Empty);
                return new NestDownloadRow(d.Id, name, line, source, NestDownloadRowKind.Done, null, d.Folder);
            }
            case NestDownload.Failed:
                return new NestDownloadRow(d.Id, name, "Stopped: " + NestDownloadErrors.Text(d.Error), source, NestDownloadRowKind.Failed, null, d.Folder);
            default:
            {
                string line;
                if (d.Error is not null)
                    line = "Lost the connection · trying again soon";
                else
                {
                    var running = all.Count(x => x.State == NestDownload.Running);
                    line = running switch
                    {
                        0 => "Waiting to start",
                        1 => "Waiting · 1 is downloading",
                        _ => $"Waiting · {running.ToString(CultureInfo.InvariantCulture)} are downloading",
                    };
                }
                return new NestDownloadRow(d.Id, name, line, source, NestDownloadRowKind.Waiting, null, d.Folder);
            }
        }
    }

    /// <summary>"Downloads", "Films/2026", or "the top of the nest" for "".</summary>
    public static string FolderName(string folder) => folder.Trim('/').Length == 0 ? "the top of the nest" : folder.Trim('/');

    /// <summary>The done note, once per finished download: "ubuntu.iso finished downloading on the nest".</summary>
    /// <summary>"Change…" in the download window: the top of the nest, "Downloads" (also before it exists), then the nest's folders.</summary>
    public static IReadOnlyList<string> FolderChoices(IReadOnlyList<string> folders)
    {
        var choices = new List<string> { FilesPageText.MoveTop, NestDownloadRules.DefaultFolder };
        choices.AddRange(folders.Where(f => f != NestDownloadRules.DefaultFolder));
        return choices;
    }

    /// <summary>The nest folder of a choice from <see cref="FolderChoices"/> ("" = the top).</summary>
    public static string FolderOfChoice(string choice) => choice == FilesPageText.MoveTop ? string.Empty : choice;

    public static (string Title, string Text) DoneNote(NestDownload d) =>
        ("Download finished", $"{(d.Name.Length > 0 ? d.Name : d.Source)} finished downloading on the nest, in {FolderName(d.Folder)}.");
}
