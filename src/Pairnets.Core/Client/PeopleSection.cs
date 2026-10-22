using System.Globalization;
using Pairnets.Core.Sync;

namespace Pairnets.Core.Client;

/// <summary>One row of the People section: a friend, or someone waiting for the owner to let them in.</summary>
/// <param name="Key">What tells the rows apart ("p:acc_…", "r:inv_…").</param>
/// <param name="Letter">The round picture's letter ("A").</param>
/// <param name="Title">"Anna Berg", or "Oskar Nilsson wants to join".</param>
/// <param name="Line">"a•••@gmail.com · 2 computers", or "oskar.n@gmail.com used your invite link · Music · view only · waiting for your OK".</param>
/// <param name="Chips">The shared folders ("Photos", "School"), or "Whole nest".</param>
/// <param name="Pill">"Can change" or "View only".</param>
/// <param name="Waiting">Someone who used an invite link and waits: Decline and "Let Oskar in" instead of Change… and Remove.</param>
/// <param name="LetInLabel">"Let Oskar in".</param>
public sealed record PersonRow(string Key, string Letter, string Title, string Line, IReadOnlyList<string> Chips, string Pill, bool ViewOnly,
    bool Waiting, string LetInLabel)
{
    public string ChangeLabel => "Change what " + FirstName + " gets";

    public string RemoveLabel => "Remove " + FirstName;

    public string DeclineLabel => "Decline " + FirstName;

    /// <summary>"Anna" for "Anna Berg", the first word of the name.</summary>
    public string FirstName => Title.Split(' ', StringSplitOptions.RemoveEmptyEntries).FirstOrDefault() ?? Title;

    // The chips by what they say, not by which list holds them: the pages redraw the People list only when a row really
    // changed, and every status update reads the list anew (a redraw would take the keyboard focus off a button).
    public bool Equals(PersonRow? other) =>
        other is not null && Key == other.Key && Letter == other.Letter && Title == other.Title && Line == other.Line && Pill == other.Pill
        && ViewOnly == other.ViewOnly && Waiting == other.Waiting && LetInLabel == other.LetInLabel && Chips.SequenceEqual(other.Chips, StringComparer.Ordinal);

    public override int GetHashCode() => HashCode.Combine(Key, Title, Line, Pill, ViewOnly, Waiting, Chips.Count);
}

/// <summary>
/// The People section under the owner's Computers page (friend invites, 1.0.61.2): a heading, a summary line, "Invite a
/// friend…", then one row per friend and per person waiting to be let in, from <c>GET /api/people</c>. Inviting, letting
/// in, changing and removing happen on the account page (only a signed-in browser may), so every button there opens it at
/// this nest's People (<see cref="Relay.PeopleUrl"/>). It shows on the owner's computers of a nest reached through
/// Pairnets; a nest at its own address has no account to share it from. Both apps draw this model.
/// </summary>
/// <param name="NestId">The nest's id at Pairnets ("nst_…"), for the account page's links.</param>
/// <param name="Sealed">An encrypted nest: it can only be shared whole.</param>
public sealed record PeopleSection(bool Visible, string Summary, IReadOnlyList<PersonRow> Rows, string? NestId, bool Sealed)
{
    public static readonly PeopleSection Hidden = new(false, string.Empty, [], null, false);

    /// <summary>The account page at this nest's People.</summary>
    public string? PeopleUrl => NestId is null ? null : Relay.PeopleUrl(NestId);

    public static PeopleSection From(StatusSnapshot s)
    {
        if (!s.IsRelay || !s.Access.IsOwner || Relay.NestId(s.ServerUrl) is not { } nestId)
            return Hidden;
        var rows = new List<PersonRow>();
        foreach (var person in s.People?.People ?? [])
            rows.Add(Friend(person));
        foreach (var request in s.People?.Requests ?? [])
            rows.Add(Asking(request));
        var friends = rows.Count(r => !r.Waiting);
        var waiting = rows.Count - friends;
        var parts = new List<string>();
        if (friends > 0)
            parts.Add(friends == 1 ? "1 friend" : $"{friends.ToString(CultureInfo.InvariantCulture)} friends");
        if (waiting > 0)
            parts.Add($"{waiting.ToString(CultureInfo.InvariantCulture)} waiting for your OK");
        var summary = parts.Count > 0 ? string.Join(" · ", parts) : "Only you so far";
        return new PeopleSection(true, summary, rows, nestId, s.Server?.Sealed == true);
    }

    private static PersonRow Friend(FriendPerson p)
    {
        var (name, email) = Split(p.Person);
        var computers = p.Devices?.Count ?? 0;
        var line = string.Join(" · ", new[] { email, computers == 1 ? "1 computer" : $"{computers.ToString(CultureInfo.InvariantCulture)} computers" }.OfType<string>());
        var access = NestAccess.Read(p.Access, p.Folders);
        return new PersonRow("p:" + (p.PersonId ?? name), LetterOf(name), name, line, Chips(access.Folders), ShareText.Pill(access.Level), access.IsViewOnly, false,
            string.Empty);
    }

    private static PersonRow Asking(FriendRequestEntry r)
    {
        var name = PlainText.Clean(r.Name) is { Length: > 0 } n ? n : PlainText.Clean(r.Email) is { Length: > 0 } e ? e : "Someone";
        var access = NestAccess.Read(r.Access ?? NestAccess.View, r.Folders);
        var first = name.Split(' ', StringSplitOptions.RemoveEmptyEntries).FirstOrDefault() ?? name;
        var line = string.Join(" · ", new[]
        {
            PlainText.Clean(r.Email) is { Length: > 0 } email ? email + " used your invite link" : "Used your invite link",
            ShareText.Folders(access.Folders),
            ShareText.Words(access.Level),
            "waiting for your OK",
        });
        return new PersonRow("r:" + (r.Id ?? name), LetterOf(name), name + " wants to join", line, [], ShareText.Pill(access.Level), access.IsViewOnly, true,
            "Let " + first + " in");
    }

    /// <summary>"Anna Berg · a•••@gmail.com" → ("Anna Berg", "a•••@gmail.com"); a line without the dot is all name.</summary>
    public static (string Name, string? Email) Split(string? person)
    {
        var line = PlainText.Clean(person, 200);
        if (line.Length == 0)
            return ("A friend", null);
        var at = line.LastIndexOf(" · ", StringComparison.Ordinal);
        return at <= 0 ? (line, null) : (line[..at].Trim(), line[(at + 3)..].Trim() is { Length: > 0 } email ? email : null);
    }

    private static string LetterOf(string name) =>
        name.FirstOrDefault(char.IsLetterOrDigit) is var c && c != default ? char.ToUpperInvariant(c).ToString() : "?";

    private static IReadOnlyList<string> Chips(IReadOnlyList<string>? folders) => folders is null ? [ShareText.WholeNest] : folders;
}

/// <summary>
/// The "Invite a friend…" window's choices (both apps): the whole nest or chosen top-level folders, "View only" (the
/// default) or "Can change". "Continue in your browser" opens the account page's invite form with them filled in
/// (<see cref="Relay.InviteUrl"/>): only a signed-in browser may make an invite, and it sends the email or gives the link.
/// </summary>
public static class InviteChoices
{
    /// <summary>At most this many folders in one invite (the nest takes 20).</summary>
    public const int MaxFolders = 20;

    /// <summary>The top-level folders of the nest's folder on this computer, with their sizes, by name (Pairnets's own left out).</summary>
    public static IReadOnlyList<(string Name, long Bytes)> LocalFolders(string? folder, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(folder) || !Directory.Exists(folder))
            return [];
        var list = new List<(string, long)>();
        try
        {
            foreach (var dir in new DirectoryInfo(folder).EnumerateDirectories().OrderBy(d => d.Name, StringComparer.OrdinalIgnoreCase))
            {
                ct.ThrowIfCancellationRequested();
                if (dir.Name.StartsWith(".pairnets", StringComparison.OrdinalIgnoreCase) || (dir.Attributes & FileAttributes.Hidden) != 0 || IsLink(dir)
                    || !NestAccess.IsFolderName(dir.Name))
                    continue;
                list.Add((dir.Name, SizeOf(dir, ct)));
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // What could be read is offered; the rest can be typed on the account page.
        }
        return list;
    }

    /// <summary>
    /// A link (symbolic link or junction) to somewhere else: never offered or counted. Other reparse points stay: in a
    /// folder kept On-Demand, or under OneDrive, every folder and file is a cloud placeholder, and those are the nest's
    /// own folders (reading their names and sizes downloads nothing).
    /// </summary>
    internal static bool IsLink(FileSystemInfo entry) =>
        (entry.Attributes & FileAttributes.ReparsePoint) != 0 && entry.LinkTarget is not null;

    private static long SizeOf(DirectoryInfo dir, CancellationToken ct)
    {
        long bytes = 0;
        try
        {
            var options = new EnumerationOptions { IgnoreInaccessible = true, AttributesToSkip = 0 };
            var folders = new Stack<DirectoryInfo>([dir]);
            while (folders.TryPop(out var here))
            {
                foreach (var entry in here.EnumerateFileSystemInfos("*", options))
                {
                    ct.ThrowIfCancellationRequested();
                    if (IsLink(entry))
                        continue;
                    if (entry is DirectoryInfo inner)
                        folders.Push(inner);
                    else if (entry is FileInfo file)
                        bytes += file.Length;
                }
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
        }
        return bytes;
    }

    /// <summary>Why the choices can't go on yet ("Choose at least one folder."), or null.</summary>
    public static string? Problem(bool wholeNest, IReadOnlyCollection<string> folders) =>
        wholeNest ? null
        : folders.Count == 0 ? "Choose at least one folder, or share the whole nest."
        : folders.Count > MaxFolders ? $"Choose at most {MaxFolders} folders, or share the whole nest."
        : null;
}
