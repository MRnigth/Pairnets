using Pairnets.Core.Paths;
using Pairnets.Core.Sync;

namespace Pairnets.Core.Client;

/// <summary>One step of the Files page's path: "Your nest" (""), "Photos" ("Photos"), "2024" ("Photos/2024").</summary>
public sealed record Crumb(string Name, string Folder);

/// <summary>A row of the Files page: the entry, and the small line under its name (its folder while searching).</summary>
public sealed record NestRow(NestEntry Entry, string Detail = "")
{
    public string Name => Entry.Name;

    public bool IsFolder => Entry.IsFolder;

    public string SizeText => Entry.SizeText;

    public string WhenText => Entry.WhenText;

    public string WhereText => Entry.WhereText;

    public bool HasDetail => Detail.Length > 0;

    /// <summary>What the Where column draws: a folder its choice, a file where it is.</summary>
    public FilePlacement Shows => Entry.IsFolder ? Entry.Placement : Entry.Where switch
    {
        FileWhere.Here => FilePlacement.OnThisPc,
        FileWhere.IconOnly => FilePlacement.OnDemand,
        _ => FilePlacement.NestOnly,
    };
}

/// <summary>A row of "Clean up your nest": a big file, and its bar (its size against the biggest one shown).</summary>
public sealed record CleanupRow(NestEntry Entry, double Percent)
{
    public string Name => Entry.Name;

    public string Detail => FilesPageText.CleanupDetail(Entry);

    public string KindText => FilesPageText.KindOne(NestFiles.KindOf(Entry.Path));

    public string SizeText => Format.Bytes(Entry.Size);

    /// <summary>The rows for <paramref name="biggest"/>, each bar measured against the first (the biggest).</summary>
    public static IReadOnlyList<CleanupRow> For(IReadOnlyList<NestEntry> biggest)
    {
        var top = Math.Max(1, biggest.Count > 0 ? biggest.Max(e => e.Size) : 1);
        return biggest.Select(e => new CleanupRow(e, Math.Max(2, 100.0 * e.Size / top))).ToList();
    }
}

/// <summary>What a question before moving files off this computer says (<see cref="FilesPageText.AwayQuestion"/>).</summary>
public sealed record AwayQuestion(string Title, string Text, IReadOnlyList<string> Points, string Button);

/// <summary>
/// The words of the Files page, "Help me clean up", the nest-full pop-up and the Settings page's "Where your files
/// live", shared by both apps so they say the same.
/// </summary>
public static class FilesPageText
{
    public const string Title = "Files";
    public const string Subtitle = "Everything on your nest, and what was deleted in the last 30 days.";
    public const string Top = "Your nest";
    public const string OfflineTitle = "Your nest can't be reached.";
    public const string OfflineText = "This is the last list Pairnets saw. Files only on the nest open again when it's back; files on this PC open as usual.";
    public const string DropHint = "Drop files here to put them on your nest. Double-click a file to open it: changes you save go back to your nest.";
    public const string OpenedNote = "Open · changes are saved to your nest";
    public const string Empty = "Nothing here yet. Add files, or drop them here.";
    public const string NoMatch = "No file on your nest has that in its name or folder.";
    public const string NotConnected = "Not connected to your nest. Your files show here once Pairnets is connected.";
    public const string SearchHint = "Search files by name or folder";
    public const string MoveTop = "Top of your nest";

    public const string FullTitle = "Your nest is full";
    public const string FullText = "You can still connect and open files, but nothing can be added, changed, moved or deleted until your nest has room.";
    public const string FullSaving = "New files you save stay on this PC until there's room.";
    public const string HelpMeCleanUp = "Help me clean up";

    public const string CleanupTitle = "Clean up your nest";
    public const string CleanupAdvice = "We recommend you don't delete anything. Better ways to make room: save big files you rarely need to an external drive with Save a copy, or give your nest a bigger disk. Delete only if you are completely sure.";
    public const string AllKinds = "All types";
    public const string HistoryRow = "Older versions and deleted files";
    public const string HistoryRowNote = "Kept 30 days in History, then removed by your nest by itself";

    public const string SureCheck = "I understand this file will be gone for good, everywhere.";
    public const string SureType = "To delete it, type " + NestFiles.DeleteWord + ":";
    public const string KeepIt = "Keep it";
    public const string DeleteForGood = "Delete for good";
    public const string SaveCopyFirst = "Save a copy first…";

    public const string SettingsTitle = "Where your files live";
    public const string SettingsNote = "This PC only · your other computers choose for themselves";
    public const string SettingsText = "Keep big folders on your nest to free space here. Pairnets only removes a file from this PC once it is safely on your nest.";
    public const string EverythingElse = "Everything else";
    public const string EverythingElseNote = "Files and folders not listed below";
    public const string SettingsLink = "Choose for a folder inside these… (opens Files)";
    public const string SettingsOff = "Sign in and connect to your nest to choose where your files live.";

    /// <summary>The path to <paramref name="folder"/>, from "Your nest" down.</summary>
    public static IReadOnlyList<Crumb> Crumbs(string folder)
    {
        folder = PlacementMap.NormalizeFolder(folder);
        var crumbs = new List<Crumb> { new(Top, string.Empty) };
        if (folder.Length == 0)
            return crumbs;
        var parts = folder.Split('/');
        for (var i = 0; i < parts.Length; i++)
            crumbs.Add(new Crumb(parts[i], string.Join('/', parts[..(i + 1)])));
        return crumbs;
    }

    /// <summary>"Photos", or "all your files" for the whole Pairnets folder.</summary>
    public static string FolderName(string folder) =>
        PlacementMap.NormalizeFolder(folder) is { Length: > 0 } f ? PathRules.FileName(f) : "all your files";

    /// <summary>"1.8 GB in this folder · 0 B of it on this PC".</summary>
    public static string Footer(long total, long here) =>
        $"{Format.Bytes(total)} in this folder · {(here >= total && total > 0 ? "all" : Format.Bytes(here))} of it on this PC";

    /// <summary>The line under a folder in Settings: "18.4 GB · 0 B on this PC", "3.4 GB · all on this PC".</summary>
    public static string SizeLine(long total, long here) =>
        $"{Format.Bytes(total)} · {(here >= total ? "all" : Format.Bytes(here))} on this PC";

    /// <summary>"WHERE PHOTOS LIVES ON THIS PC".</summary>
    public static string PickerTitle(string folder) =>
        PlacementMap.NormalizeFolder(folder).Length == 0 ? "WHERE YOUR FILES LIVE ON THIS PC" : $"WHERE {FolderName(folder).ToUpperInvariant()} LIVES ON THIS PC";

    /// <summary>The one line under a choice of the placement menu.</summary>
    public static string ChoiceText(FilePlacement placement, long bytesHere) => placement switch
    {
        FilePlacement.OnDemand => "Files show in File Explorer; each downloads when you open it. Right-click › Always keep on this device to keep one here.",
        FilePlacement.NestOnly => "Nothing on this PC. Open the files here, in Files." + (bytesHere > 0 ? $" Frees {Format.Bytes(bytesHere)}." : string.Empty),
        _ => "Every file is here and on your nest. Works without internet.",
    };

    /// <summary>A change that takes files off this computer is asked about first.</summary>
    public static bool AsksFirst(FilePlacement from, FilePlacement to) => to != FilePlacement.OnThisPc && to != from;

    /// <summary>The question before <paramref name="folder"/> goes to On-Demand or the nest only.</summary>
    public static AwayQuestion AwayQuestion(string folder, FilePlacement to, long bytesHere)
    {
        var name = FolderName(folder);
        var size = Format.Bytes(bytesHere);
        if (to == FilePlacement.OnDemand)
        {
            return new AwayQuestion($"Make {name} On-Demand?",
                "Pairnets checks that each file is safely on your nest, then keeps only its icon on this PC. A file downloads again when you open it."
                + (bytesHere > 0 ? $" This frees up to {size}." : string.Empty),
                [
                    "Files still being uploaded stay here until your nest has them.",
                    "Right-click a file › Always keep on this device to keep it here.",
                    "Your other computers keep their copies.",
                ],
                "Make it On-Demand");
        }
        return new AwayQuestion($"Keep {name} only on your nest?",
            "Pairnets checks that each file is safely on your nest, then removes it from this PC." + (bytesHere > 0 ? $" This frees {size}." : string.Empty),
            [
                "Files still being uploaded stay here until your nest has them.",
                "Open them any time from Files while your nest is online.",
                "Your other computers keep their copies.",
            ],
            bytesHere > 0 ? "Free " + size : "Keep it on the nest only");
    }

    /// <summary>"3.1 GB free of 1 TB on your nest." (empty when the nest has not said).</summary>
    public static string FreeLine(ServerInfo? server) => server?.DiskFreeBytes is { } free
        ? server.DiskTotalBytes is { } total ? $"{Format.Bytes(free)} free of {Format.Bytes(total)} on your nest." : $"{Format.Bytes(free)} free on your nest."
        : string.Empty;

    /// <summary>The Clean up page's line under its title.</summary>
    public static string CleanupSubtitle(ServerInfo? server) =>
        "The biggest files on your nest, so you can see what takes the space." + (FreeLine(server) is { Length: > 0 } free ? " " + free : string.Empty);

    /// <summary>"Videos · 612 GB".</summary>
    public static string KindChip(FileKind kind, long bytes) => $"{NestFiles.KindText(kind)} · {Format.Bytes(bytes)}";

    /// <summary>"Video", "Photo", "Archive", "Document", "Other".</summary>
    public static string KindOne(FileKind kind) => kind switch
    {
        FileKind.Video => "Video",
        FileKind.Photo => "Photo",
        FileKind.Archive => "Archive",
        FileKind.Document => "Document",
        _ => "Other",
    };

    /// <summary>"Videos/2019 · last changed 2 Oct 2019 10:00".</summary>
    public static string CleanupDetail(NestEntry entry) =>
        $"{PathRules.Parent(entry.Path) ?? Top} · last changed {entry.WhenText}";

    /// <summary>"Showing the 50 biggest files · 997 GB used in total".</summary>
    public static string CleanupFooter(int shown, long used) =>
        $"Showing the {(shown == 1 ? "biggest file" : Format.Count(shown) + " biggest files")} · {Format.Bytes(used)} used in total";

    /// <summary>The strong question before deleting for good.</summary>
    public static (string Title, string Text) SureQuestion(NestEntry entry) =>
        ($"Delete “{entry.Name}” for good?",
         $"This frees {Format.Bytes(entry.Size)}, but it can never be brought back: not from History, and not from any of your computers. We recommend saving a copy somewhere else first.");

    /// <summary>"Delete for good" works only when the box is ticked and the word typed.</summary>
    public static bool SureReady(bool ticked, string? typed) =>
        ticked && string.Equals(typed?.Trim(), NestFiles.DeleteWord, StringComparison.Ordinal);

    /// <summary>"Delete Photos?": what a plain delete on the Files page asks first.</summary>
    public static string DeleteQuestion(NestEntry entry) => entry.IsFolder
        ? $"Delete the folder {entry.Name} and the {Format.Count(entry.Files)} {(entry.Files == 1 ? "file" : "files")} in it from your nest and all your computers?\n\nYou can get them back from the Deleted tab for 30 days."
        : $"Delete {entry.Name} from your nest and all your computers?\n\nYou can get it back from the Deleted tab for 30 days.";
}
