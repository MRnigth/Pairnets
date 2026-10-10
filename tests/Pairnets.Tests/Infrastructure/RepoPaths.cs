namespace Pairnets.Tests.Infrastructure;

/// <summary>The checkout the tests run from (the folder holding Pairnets.sln), for tests that read the sources.</summary>
public static class RepoPaths
{
    private static readonly Lazy<string> RootDir = new(() =>
    {
        for (var dir = new DirectoryInfo(AppContext.BaseDirectory); dir is not null; dir = dir.Parent)
        {
            if (File.Exists(Path.Combine(dir.FullName, "Pairnets.sln")))
                return dir.FullName;
        }
        throw new FileNotFoundException("Pairnets.sln not found above " + AppContext.BaseDirectory);
    });

    public static string Root => RootDir.Value;

    /// <summary>A path inside the checkout: <c>RepoPaths.Of("src", "Pairnets.Server", "WebUi")</c>.</summary>
    public static string Of(params string[] parts) => Path.Combine([Root, .. parts]);

    /// <summary>A source file's text, with "\n" line endings whatever the checkout uses.</summary>
    public static string Read(params string[] parts) => File.ReadAllText(Of(parts)).ReplaceLineEndings("\n");
}
