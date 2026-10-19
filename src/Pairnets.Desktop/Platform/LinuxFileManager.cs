using System.Xml.Linq;
using Pairnets.Core.Client;

namespace Pairnets.Desktop.Platform;

/// <summary>
/// Pairnets in the Linux file managers, as far as they allow it (there is no shared system like Windows' cloud files):
/// <list type="bullet">
/// <item>a "Pairnets" bookmark in the sidebar: GTK's bookmarks file (GNOME Files, Nemo, Thunar, Caja) and KDE's places
/// (Dolphin), the latter only when KDE already keeps one;</item>
/// <item>"Open in Pairnets" and "Show sync status" on right-click: a Dolphin service menu (a "Pairnets" submenu) and
/// GNOME Files / Nemo scripts (the "Scripts" submenu). They show everywhere; outside the Pairnets folder they just open
/// Pairnets;</item>
/// <item>the blue Pairnets folder icon, through gio (GNOME Files and Nemo show it).</item>
/// </list>
/// Everything lives in the person's own home folder and is taken away again by <see cref="Remove"/>.
/// </summary>
/// <param name="home">The home folder (a temporary one in tests).</param>
/// <param name="runGio">Runs gio with these arguments (a recorder in tests); returns its exit code.</param>
public sealed class LinuxFileManager(string home, Func<string[], int> runGio)
{
    public const string BookmarkName = "Pairnets";

    /// <summary>The id Pairnets puts on its KDE place, so it only ever removes its own.</summary>
    public const string KdePlaceId = "pairnets-sync-folder";

    private string ConfigDir => Environment.GetEnvironmentVariable("XDG_CONFIG_HOME") is { Length: > 0 } x && home == RealHome ? x : Path.Combine(home, ".config");

    private string DataDir => Environment.GetEnvironmentVariable("XDG_DATA_HOME") is { Length: > 0 } x && home == RealHome ? x : Path.Combine(home, ".local", "share");

    private static string RealHome => Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);

    public string GtkBookmarks => Path.Combine(ConfigDir, "gtk-3.0", "bookmarks");

    public string KdePlaces => Path.Combine(DataDir, "user-places.xbel");

    /// <summary>Dolphin's service menus (KDE 6 first, then the KDE 5 folder).</summary>
    public IReadOnlyList<string> ServiceMenus =>
    [
        Path.Combine(DataDir, "kio", "servicemenus", "pairnets.desktop"),
        Path.Combine(DataDir, "kservices5", "ServiceMenus", "pairnets.desktop"),
    ];

    /// <summary>The scripts folders of GNOME Files and Nemo.</summary>
    public IReadOnlyList<string> ScriptDirs => [Path.Combine(DataDir, "nautilus", "scripts"), Path.Combine(DataDir, "nemo", "scripts")];

    private static readonly (string Name, string Argument)[] Scripts =
    [
        ("Open in Pairnets", FileManagerCommand.OpenArgument),
        ("Show Pairnets sync status", FileManagerCommand.StatusArgument),
    ];

    /// <summary>Adds (or brings up to date) the bookmark, the right-click items and the folder icon.</summary>
    public bool Apply(string folder, string exePath, string folderIconPng)
    {
        folder = Path.TrimEndingDirectorySeparator(Path.GetFullPath(folder));
        var ok = true;
        ok &= Try(() => SetGtkBookmark(folder));
        ok &= Try(() => SetKdePlace(folder));
        ok &= Try(() => WriteServiceMenus(exePath));
        ok &= Try(() => WriteScripts(exePath));
        ok &= runGio(["set", folder, "metadata::custom-icon", FileUri(folderIconPng)]) == 0;
        return ok;
    }

    /// <summary>Takes it all away again; <paramref name="folder"/> (when known) loses its icon.</summary>
    public bool Remove(string? folder)
    {
        var ok = true;
        ok &= Try(() => SetGtkBookmark(null));
        ok &= Try(() => SetKdePlace(null));
        ok &= Try(() =>
        {
            foreach (var file in ServiceMenus)
                File.Delete(file);
        });
        ok &= Try(() =>
        {
            foreach (var dir in ScriptDirs)
                foreach (var (name, _) in Scripts)
                    File.Delete(Path.Combine(dir, name));
        });
        if (!string.IsNullOrWhiteSpace(folder) && Directory.Exists(folder))
            runGio(["set", "-t", "unset", folder, "metadata::custom-icon"]);
        return ok;
    }

    private static bool Try(Action action)
    {
        try
        {
            action();
            return true;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.Xml.XmlException)
        {
            return false;
        }
    }

    /// <summary>
    /// GTK's bookmarks: one "uri name" per line. Pairnets' line is the one named "Pairnets"; the person's own
    /// bookmarks are kept as they are, in their order.
    /// </summary>
    private void SetGtkBookmark(string? folder)
    {
        var lines = File.Exists(GtkBookmarks) ? File.ReadAllLines(GtkBookmarks).ToList() : [];
        var kept = lines.Where(l => !l.EndsWith(" " + BookmarkName, StringComparison.Ordinal)).ToList();
        if (folder is not null)
            kept.Add(FileUri(folder) + " " + BookmarkName);
        if (kept.SequenceEqual(lines))
            return;
        if (folder is null && !File.Exists(GtkBookmarks))
            return;
        Directory.CreateDirectory(Path.GetDirectoryName(GtkBookmarks)!);
        File.WriteAllLines(GtkBookmarks, kept);
    }

    /// <summary>KDE's places (Dolphin's sidebar): a bookmark marked with <see cref="KdePlaceId"/>. Only where KDE keeps the file.</summary>
    private void SetKdePlace(string? folder)
    {
        if (!File.Exists(KdePlaces))
            return;
        var doc = XDocument.Load(KdePlaces, LoadOptions.PreserveWhitespace);
        if (doc.Root is not { } xbel)
            return;
        var ours = xbel.Elements("bookmark").Where(b => b.Descendants("ID").Any(id => id.Value == KdePlaceId)).ToList();
        var href = folder is null ? null : FileUri(folder);
        if (folder is null && ours.Count == 0)
            return;
        if (ours.Count == 1 && (string?)ours[0].Attribute("href") == href)
            return;
        ours.ForEach(b => b.Remove());
        if (href is not null)
        {
            XNamespace bookmark = "http://www.freedesktop.org/standards/desktop-bookmarks";
            xbel.Add(new XElement("bookmark", new XAttribute("href", href),
                new XElement("title", BookmarkName),
                new XElement("info",
                    new XElement("metadata", new XAttribute("owner", "http://freedesktop.org"),
                        new XElement(bookmark + "icon", new XAttribute("name", "folder-cloud"))),
                    new XElement("metadata", new XAttribute("owner", "http://www.kde.org"),
                        new XElement("ID", KdePlaceId)))));
        }
        doc.Save(KdePlaces);
    }

    /// <summary>Dolphin's right-click items, in a "Pairnets" submenu. KDE 6 only runs service menus that may be executed.</summary>
    private void WriteServiceMenus(string exePath)
    {
        var text = $"""
            [Desktop Entry]
            Type=Service
            MimeType=all/all;
            X-KDE-Protocols=file
            X-KDE-RequiredNumberOfUrls=1
            X-KDE-Submenu=Pairnets
            Actions=PairnetsOpen;PairnetsStatus;

            [Desktop Action PairnetsOpen]
            Name=Open in Pairnets
            Icon=pairnets
            Exec={DesktopQuote(exePath)} {FileManagerCommand.OpenArgument} %f

            [Desktop Action PairnetsStatus]
            Name=Show sync status
            Icon=pairnets
            Exec={DesktopQuote(exePath)} {FileManagerCommand.StatusArgument} %f

            """;
        foreach (var file in ServiceMenus)
            WriteExecutable(file, text);
    }

    /// <summary>GNOME Files and Nemo run any program in their scripts folder with the chosen files as arguments.</summary>
    private void WriteScripts(string exePath)
    {
        foreach (var dir in ScriptDirs)
            foreach (var (name, argument) in Scripts)
                WriteExecutable(Path.Combine(dir, name), $"#!/bin/sh\n# Added by Pairnets; removed again when Pairnets leaves the file manager.\nexec {ShellQuote(exePath)} {argument} \"$1\"\n");
    }

    private static void WriteExecutable(string file, string text)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(file)!);
        if (!File.Exists(file) || File.ReadAllText(file) != text)
            File.WriteAllText(file, text);
        if (!OperatingSystem.IsWindows())
            File.SetUnixFileMode(file, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute
                | UnixFileMode.GroupRead | UnixFileMode.GroupExecute | UnixFileMode.OtherRead | UnixFileMode.OtherExecute);
    }

    /// <summary>"file:///home/me/My%20Pairnets" for a full path (also a Unix path while testing on Windows).</summary>
    public static string FileUri(string path) =>
        path.StartsWith('/') ? new UriBuilder { Scheme = Uri.UriSchemeFile, Host = string.Empty, Path = path }.Uri.AbsoluteUri : new Uri(path).AbsoluteUri;

    /// <summary>A program path for a .desktop file's Exec line (quoted, with its own escapes).</summary>
    public static string DesktopQuote(string path) =>
        "\"" + path.Replace("\\", "\\\\").Replace("\"", "\\\"").Replace("`", "\\`").Replace("$", "\\$").Replace("%", "%%") + "\"";

    /// <summary>A program path for /bin/sh, in single quotes.</summary>
    public static string ShellQuote(string path) => "'" + path.Replace("'", "'\\''") + "'";
}
