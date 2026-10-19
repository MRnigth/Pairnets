using System.Text.RegularExpressions;

namespace Pairnets.Core.Client;

/// <summary>
/// Takes anything personal out of error text before it is kept in a crash card: paths, file and folder names,
/// web addresses, emails, IP addresses, tokens, long ids, and the names this computer is known by. What stays is
/// the kind of error and the Pairnets code it went through, which is what finding the bug needs.
/// Errs on the side of removing too much.
/// </summary>
public static class PrivacyCleaner
{
    public const string Hidden = "(hidden)";

    private const RegexOptions Options = RegexOptions.IgnoreCase | RegexOptions.CultureInvariant;

    // "token=…", "X-Sync-Token: …", "Authorization: Bearer …", and tokens recognised by their shape.
    private static readonly Regex NamedSecret = new(@"((?:sync[_-]?token|x-sync-token|token|password|secret|authorization)\s*[:=]\s*)(?:bearer\s+)?\S+", Options);
    private static readonly Regex BearerSecret = new(@"(\bbearer\s+)\S+", Options);
    private static readonly Regex ShapedSecret = new(@"\b(?:pca_|pn_)[A-Za-z0-9_\-]+|\beyJ[A-Za-z0-9_\-]+\.[A-Za-z0-9_\-]+(?:\.[A-Za-z0-9_\-]+)?", Options);

    // A stack frame's source file keeps only its name: " in C:\a\src\Sync\SyncEngine.cs:line 42" → " in SyncEngine.cs:line 42".
    private static readonly Regex FrameFile = new(@"(\)\s+in\s+)[^\r\n]*[\\/]([^\\/\r\n]+\.cs:line\s+\d+)", Options);
    private static readonly Regex Url = new(@"\b[a-z][a-z0-9+.\-]*://[^\s'""<>]+", Options);
    private static readonly Regex Email = new(@"[A-Za-z0-9._%+\-]+@[A-Za-z0-9.\-]+\.[A-Za-z]{2,}", Options);
    // Quoted text in error messages is data (a path, a file name, a value); parameter names stay.
    private static readonly Regex SingleQuoted = new(@"(?<!Parameter )'[^'\r\n]*'", Options);
    private static readonly Regex DoubleQuoted = new(@"""[^""\r\n]*""", Options);
    private static readonly Regex DrivePath = new(@"\b[A-Za-z]:[\\/][^\s'""<>|]*", Options);
    private static readonly Regex UncPath = new(@"\\\\[^\s'""<>|]+", Options);
    // Anything with a slash in it: /home/me/x, ~/Work, Photos/2024/kid.jpg, Work\notes.txt.
    private static readonly Regex SlashPath = new(@"(?:~|[^\s'""<>()\[\],;=/\\]*)(?:[/\\][^\s'""<>()\[\],;=/\\]+)+[/\\]?", Options);
    private static readonly Regex Ipv4 = new(@"(?<!Version=)(?<![\w.])(?:\d{1,3}\.){3}\d{1,3}(?![\w.])", Options);
    private static readonly Regex Ipv6 = new(@"(?<![\w:])(?:[0-9a-f]{0,4}:){3,7}[0-9a-f]{0,4}(?![\w:])", Options);
    private static readonly Regex Guid = new(@"\b[0-9a-f]{8}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{12}\b", Options);
    private static readonly Regex LongId = new(@"(?=[A-Za-z0-9_\-+/=]*\d)(?=[A-Za-z0-9_\-+/=]*[A-Za-z])\b[A-Za-z0-9_\-+=]{24,}", Options);

    /// <summary>Only the secrets: tokens, passwords and sign-in headers. What "Report a bug" applies to its text.</summary>
    public static string HideSecrets(string text)
    {
        text = NamedSecret.Replace(text, "$1" + Hidden);
        text = BearerSecret.Replace(text, "$1" + Hidden);
        return ShapedSecret.Replace(text, Hidden);
    }

    /// <summary>Everything personal out of <paramref name="text"/>.</summary>
    /// <param name="privateValues">Names this computer is known by (user name, computer name, home and sync folders,
    /// device name, nest address); each is replaced wherever it appears.</param>
    public static string Clean(string text, IEnumerable<string?>? privateValues = null)
    {
        text = FrameFile.Replace(text, "$1$2");
        text = HideSecrets(text);
        text = Url.Replace(text, "<url>");
        text = Email.Replace(text, "<email>");
        foreach (var value in (privateValues ?? []).Where(v => v is { Length: >= 3 }).Distinct(StringComparer.OrdinalIgnoreCase).OrderByDescending(v => v!.Length))
            text = Regex.Replace(text, @"(?<![A-Za-z0-9])" + Regex.Escape(value!) + @"(?![A-Za-z0-9])", "<private>", Options);
        text = SingleQuoted.Replace(text, "'<text>'");
        text = DoubleQuoted.Replace(text, "\"<text>\"");
        text = DrivePath.Replace(text, "<path>");
        text = UncPath.Replace(text, "<path>");
        text = SlashPath.Replace(text, "<path>");
        text = Guid.Replace(text, "<id>");
        text = Ipv4.Replace(text, "<ip>");
        text = Ipv6.Replace(text, "<ip>");
        return LongId.Replace(text, "<id>");
    }

    /// <summary>The names this computer and its user are known by, with this app's own: sync folder, device name and id,
    /// nest address and account email.</summary>
    public static IEnumerable<string?> ThisComputer(Settings.ClientSettings settings) =>
        ThisComputer(settings.Folder, settings.DeviceName, settings.DeviceId, settings.ServerUrl, settings.AccountEmail);

    /// <summary>The names this computer and its user are known by, plus the given ones (settings values).</summary>
    public static IEnumerable<string?> ThisComputer(params string?[] more)
    {
        string? Safe(Func<string?> get)
        {
            try
            {
                return get();
            }
            catch (Exception ex) when (ex is InvalidOperationException or PlatformNotSupportedException or System.Security.SecurityException)
            {
                return null;
            }
        }

        yield return Safe(() => Environment.GetFolderPath(Environment.SpecialFolder.UserProfile));
        yield return Safe(() => Environment.UserName);
        yield return Safe(() => Environment.MachineName);
        yield return Safe(() => Environment.UserDomainName);
        foreach (var value in more)
        {
            yield return value;
            if (Uri.TryCreate(value, UriKind.Absolute, out var uri) && uri.Host.Length > 0)
                yield return uri.Host;
        }
    }
}
