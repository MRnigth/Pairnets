using System.Globalization;
using System.Text;
using Pairnets.Core.Sync;

namespace Pairnets.Core.Client;

/// <summary>What the nest said when it refused a WebDAV link, and the sentence the windows show for it.</summary>
public static class WebDavErrors
{
    /// <summary>An encrypted nest keeps only scrambled files, so it can't show them as a folder.</summary>
    public const string Sealed = "sealed";

    /// <summary>The nest holds <see cref="WebDavChoices.MaxLinks"/> links already.</summary>
    public const string TooMany = "too_many_links";

    public const string BadRequest = "bad_request";

    /// <summary>The nest refused for another reason.</summary>
    public const string Refused = "refused";

    /// <summary>The app's own code: the nest is from before WebDAV links.</summary>
    public const string UpdateNest = "update_nest";

    public static string Sentence(string? code) => code switch
    {
        Sealed => WebDavSection.SealedLine,
        TooMany => $"The nest already has {WebDavChoices.MaxLinks} links. Remove one you no longer use, then try again.",
        BadRequest => "The nest didn't accept that name or those folders. Check them and try again.",
        UpdateNest => WebDavSection.OldNestNote,
        _ => "The nest refused to make the link. Try again in a moment.",
    };
}

/// <summary>What the "Make a link" window accepts, and the words it fills in.</summary>
public static class WebDavChoices
{
    /// <summary>The most links a nest keeps at once.</summary>
    public const int MaxLinks = 10;

    /// <summary>The longest name for a link.</summary>
    public const int MaxLabelLength = 40;

    /// <summary>"Make a link" can't go on: a plain sentence, or null when the choices are fine.</summary>
    public static string? Problem(string? label, bool wholeNest, IReadOnlyList<string> folders)
    {
        if (string.IsNullOrWhiteSpace(label))
            return "Give the link a name, like \"Mac mini\", so you can tell it apart later.";
        if (label.Trim().Length > MaxLabelLength)
            return $"Use a shorter name (up to {MaxLabelLength.ToString(CultureInfo.InvariantCulture)} letters).";
        if (!wholeNest && folders.Count == 0)
            return "Tick at least one folder, or choose the whole nest.";
        if (!wholeNest && folders.Count > InviteChoices.MaxFolders)
            return $"Choose at most {InviteChoices.MaxFolders.ToString(CultureInfo.InvariantCulture)} folders, or the whole nest.";
        return null;
    }

    /// <summary>"view" or "edit" for the access the window picked.</summary>
    public static string Access(bool canChange) => canChange ? NestAccess.Edit : NestAccess.View;

    /// <summary>"Mac mini" → "mac-mini": a-z, 0-9 and "-" only, up to 40 (the nest's address part and the username).</summary>
    public static string Slug(string? name)
    {
        var sb = new StringBuilder();
        foreach (var c in (name ?? string.Empty).Trim().ToLowerInvariant())
        {
            if (c is >= 'a' and <= 'z' or >= '0' and <= '9')
                sb.Append(c);
            else if (sb.Length > 0 && sb[^1] != '-')
                sb.Append('-');
            if (sb.Length >= 40)
                break;
        }
        return sb.ToString().Trim('-');
    }
}

/// <summary>One row of "Files in Finder or Explorer": a link that was made.</summary>
/// <param name="Title">"Mac mini".</param>
/// <param name="Pill">"View only" or "Can change".</param>
/// <param name="Line">"Made 3 days ago · last used just now", or "... · never used".</param>
public sealed record WebDavRow(string Id, string Title, string Pill, string Line, IReadOnlyList<string> Chips, bool ViewOnly)
{
    public string RemoveLabel => "Remove the link " + Title;

    public bool Equals(WebDavRow? other) =>
        other is not null && Id == other.Id && Title == other.Title && Pill == other.Pill && Line == other.Line && ViewOnly == other.ViewOnly
        && Chips.SequenceEqual(other.Chips, StringComparer.Ordinal);

    public override int GetHashCode() => HashCode.Combine(Id, Title, Pill, Line, Chips.Count);
}

/// <summary>
/// "Files in Finder or Explorer" on the owner's Computers page (under People): the nest hands out WebDAV links, each with
/// its own generated password, so a Mac (Finder → Go → Connect to Server) or a Windows PC (Map network drive) can open
/// the nest's files without the Pairnets app. A heading with a summary and "Make a link…", one row per link, the address
/// to type, how to connect, and the 25 MB note. Hidden for friends and for a nest from before WebDAV links; an
/// encrypted nest says it can't. Both apps draw this model.
/// </summary>
/// <param name="CanMake">"Make a link…" shows.</param>
/// <param name="Address">What to type in Finder or Explorer; null when the nest's address isn't known.</param>
public sealed record WebDavSection(bool Visible, bool CanMake, string Summary, string Note, string? Address, IReadOnlyList<WebDavRow> Rows)
{
    public static readonly WebDavSection Hidden = new(false, false, string.Empty, string.Empty, null, []);

    public const string Heading = "Files in Finder or Explorer";

    public const string SealedLine = "Not available on an encrypted nest.";

    public const string OldNestNote = "Update your nest to open its files in Finder or Explorer.";

    public const string EmptyNote = "Make a link to open your nest's files in Finder or Explorer, without the Pairnets app. Each link has its own password, and you can remove it any time.";

    public const string SizeNote = "Files up to 25 MB can be added this way; bigger files only from the app. Bigger files can still be opened and copied out.";

    public const string MacHow = "On a Mac: in Finder choose Go → Connect to Server, type the address, then the username and password.";

    public const string WindowsHow = "On Windows: in File Explorer choose This PC → Map network drive, type the address, then the username and password.";

    /// <summary>The biggest file the nest takes over WebDAV, in bytes.</summary>
    public const long MaxUploadBytes = 25L * 1024 * 1024;

    public string HowMac => MacHow;

    public string HowWindows => WindowsHow;

    public string AddressLine => Address is null ? string.Empty : "Address: " + Address;

    public bool HasRows => Rows.Count > 0;

    public static WebDavSection From(StatusSnapshot s, DateTimeOffset now)
    {
        if (!s.Access.IsOwner || s.WebDav is not { } links)
            return Hidden;
        if (links.Sealed)
            return new WebDavSection(true, false, string.Empty, SealedLine, null, []);
        var address = AddressOf(s);
        if (address is null)
            return Hidden;
        var rows = (links.Links ?? []).Select(l => Row(l, now)).ToList();
        var summary = rows.Count switch
        {
            0 => "No links yet",
            1 => "1 link",
            _ => $"{rows.Count.ToString(CultureInfo.InvariantCulture)} links",
        };
        return new WebDavSection(true, rows.Count < WebDavChoices.MaxLinks, summary, rows.Count == 0 ? EmptyNote : SizeNote, address, rows);
    }

    /// <summary>
    /// What to type in Finder or Explorer: <c>https://pairnets.app/&lt;nest name&gt;.&lt;nest id&gt;/</c> for a nest reached through
    /// Pairnets, <c>&lt;its address&gt;/dav/</c> for a nest at its own address. Null when this computer doesn't know the address.
    /// </summary>
    public static string? AddressOf(StatusSnapshot s)
    {
        if (Relay.NestId(s.ServerUrl) is { } nestId)
        {
            var slug = WebDavChoices.Slug(s.NestTitle);
            return $"https://pairnets.app/{(slug.Length == 0 ? "nest" : slug)}.{nestId}/";
        }
        return Uri.TryCreate(s.ServerUrl?.Trim(), UriKind.Absolute, out var url) && url.Scheme == Uri.UriSchemeHttps && url.Query.Length == 0
            ? url.GetLeftPart(UriPartial.Authority) + url.AbsolutePath.TrimEnd('/') + "/dav/"
            : null;
    }

    private static WebDavRow Row(WebDavLink l, DateTimeOffset now)
    {
        var made = l.Created is { } c ? "Made " + DeviceRow.Ago(now - c) : "Made earlier";
        var used = l.LastUsed is { } u ? "last used " + DeviceRow.Ago(now - u) : "never used";
        var viewOnly = l.Access != NestAccess.Edit;
        var chips = l.Folders is null ? [ShareText.WholeNest] : l.Folders;
        var title = PlainText.Clean(l.Label) is { Length: > 0 } label ? label : "Link";
        return new WebDavRow(l.Id, title, ShareText.Pill(viewOnly ? NestAccess.View : NestAccess.Edit), made + " · " + used, chips, viewOnly);
    }
}
