namespace Pairnets.Core;

/// <summary>
/// The licence texts of the open-source parts the apps ship (deploy/THIRD-PARTY-NOTICES.txt, made by
/// scripts/make-notices.sh). MIT, BSD, Apache and OFL ask for them to go along with the program, so Core carries the
/// file inside itself, and every download also has it next to the app. Settings → About → "Open-source licences" opens it.
/// </summary>
public static class ThirdPartyNotices
{
    public const string FileName = "THIRD-PARTY-NOTICES.txt";

    private static readonly Lazy<string> Embedded = new(() =>
    {
        using var stream = typeof(ThirdPartyNotices).Assembly.GetManifestResourceStream("Pairnets." + FileName)
            ?? throw new InvalidOperationException("The third-party notices are missing from Pairnets.Core.");
        using var reader = new StreamReader(stream);
        return reader.ReadToEnd();
    });

    /// <summary>The whole notices file.</summary>
    public static string Text => Embedded.Value;

    /// <summary>Where <see cref="OpenTarget"/> puts its copy: Pairnets' folder in the user's temp folder.</summary>
    public static string CopyPath => Path.Combine(Path.GetTempPath(), "Pairnets", FileName);

    /// <summary>
    /// What "Open-source licences" opens: a copy of the notices written to <see cref="CopyPath"/> (a text file the
    /// system opens in its text editor), or the website's licences page if that copy can't be written.
    /// </summary>
    public static string OpenTarget()
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(CopyPath)!);
            if (!File.Exists(CopyPath) || File.ReadAllText(CopyPath) != Text)
                File.WriteAllText(CopyPath, Text);
            return CopyPath;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return PairnetsLinks.Licenses;
        }
    }
}
