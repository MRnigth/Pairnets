using Pairnets.Core.Client;

namespace Pairnets.Tests.Unit;

/// <summary>Nothing personal survives <see cref="PrivacyCleaner.Clean"/>; the error kind and the Pairnets code do.</summary>
public class PrivacyCleanerTests
{
    [Theory]
    [InlineData(@"Could not find file 'D:\Home\Alex Doe\Documents\Taxes 2026.pdf'.", "Taxes")]
    [InlineData(@"Access to the path D:\Home\alex\Work\secret.txt is denied.", "alex")]
    [InlineData(@"The process cannot access the file ""D:\Photos\Kids\beach.jpg"" because it is in use.", "beach")]
    [InlineData(@"Could not open \\nas\share\family\notes.txt", "family")]
    [InlineData("Could not find a part of the path '/home/alex/Pairnets/Work/plan.md'.", "plan.md")]
    [InlineData("Cannot upload /Users/alex/Pairnets/report.docx", "report")]
    [InlineData("Cannot upload Photos/2024/kid at beach.jpg: busy", "Photos")]
    [InlineData(@"Cannot read Work\notes.txt", "notes")]
    [InlineData("Cannot read ~/Pairnets/notes.txt", "notes")]
    [InlineData("Signed in as alex.doe@example.com", "alex.doe")]
    [InlineData("Connection refused (192.0.2.44:5075)", "192.0.2.44")]
    [InlineData("No route to host 2001:db8:85a3::8a2e:370:7334", "2001:db8")]
    [InlineData("GET https://home.example.net:5075/api/files?device=abc failed", "home.example.net")]
    [InlineData("X-Sync-Token: abc.def.ghi rejected", "abc.def.ghi")]
    [InlineData("token=supersecretvalue123 is wrong", "supersecretvalue123")]
    [InlineData("Authorization: Bearer pca_9f8e7d6c5b4a rejected", "pca_9f8e7d6c5b4a")]
    [InlineData("Unknown session eyJhbGciOi.eyJzdWIi.c2lnbmF0", "eyJhbGci")]
    [InlineData("Device 3f2504e0-4f89-11d3-9a0c-0305e82c3301 is not known", "3f2504e0")]
    [InlineData("Hash 9b74c9897bac770ffc029102a200c5de6a2f5e2a does not match", "9b74c989")]
    public void TakesOutPathsNamesAddressesAndSecrets(string text, string mustGo)
    {
        var cleaned = PrivacyCleaner.Clean(text);

        Assert.DoesNotContain(mustGo, cleaned, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void TakesOutTheNamesThisComputerIsKnownBy()
    {
        var cleaned = PrivacyCleaner.Clean("Device ALEX-LAPTOP (owner alexd) cannot sync Studio Files right now",
            ["ALEX-LAPTOP", "alexd", "Studio Files", null, "x"]);

        Assert.Equal("Device <private> (owner <private>) cannot sync <private> right now", cleaned);
    }

    [Fact]
    public void ThisComputerIncludesTheSettingsAndTheNestsHost()
    {
        var values = PrivacyCleaner.ThisComputer(new Core.Settings.ClientSettings
        {
            Folder = "/home/me/Work",
            DeviceName = "kitchen-pc",
            ServerUrl = "https://home.example.net:5075/",
            AccountEmail = "me@example.com",
        }).ToList();

        Assert.Contains("kitchen-pc", values);
        Assert.Contains("home.example.net", values);
        Assert.Contains("me@example.com", values);
        Assert.Contains(Environment.UserName, values);
        Assert.Contains(Environment.MachineName, values);
    }

    [Fact]
    public void KeepsTheErrorKindTheCodeAndTheSourceFileNames()
    {
        const string trace = """
            System.IO.IOException: The process cannot access the file 'D:\Home\alex\Work\a.txt' because it is being used by another process.
               at System.IO.FileSystem.CopyFile(String sourceFullPath, String destFullPath, Boolean overwrite)
               at Pairnets.Core.Sync.SyncEngine.ApplyAsync(SyncPlan plan, CancellationToken ct) in D:\a\Pairnets\Pairnets\src\Pairnets.Core\Sync\SyncEngine.cs:line 444
               at Pairnets.Core.Sync.SyncRunner.<RunAsync>b__12_0() in /home/runner/work/Pairnets/src/Pairnets.Core/Sync/SyncRunner.cs:line 648
            --- End of stack trace from previous location ---
            """;

        var cleaned = PrivacyCleaner.Clean(trace, ["alex"]);

        Assert.StartsWith("System.IO.IOException: The process cannot access the file '<text>' because it is being used by another process.", cleaned);
        Assert.Contains("at Pairnets.Core.Sync.SyncEngine.ApplyAsync(SyncPlan plan, CancellationToken ct) in SyncEngine.cs:line 444", cleaned);
        Assert.Contains("at Pairnets.Core.Sync.SyncRunner.<RunAsync>b__12_0() in SyncRunner.cs:line 648", cleaned);
        Assert.Contains("--- End of stack trace from previous location ---", cleaned);
        Assert.DoesNotContain("alex", cleaned);
        Assert.DoesNotContain("runner", cleaned);
    }

    [Fact]
    public void KeepsParameterNamesTimesAndVersions()
    {
        Assert.Equal("Value cannot be null. (Parameter 'path')", PrivacyCleaner.Clean("Value cannot be null. (Parameter 'path')"));
        Assert.Equal("Timed out at 10:38:08.123", PrivacyCleaner.Clean("Timed out at 10:38:08.123"));
        Assert.Equal("Needs System.Text.Json, Version=8.0.0.0", PrivacyCleaner.Clean("Needs System.Text.Json, Version=8.0.0.0"));
    }

    [Fact]
    public void HideSecretsLeavesEverythingElse()
    {
        var text = "Server: http://192.0.2.10:5075/\nX-Sync-Token: fake.token.value\nFolder: /home/me/Work\nAuthorization: Bearer abc123";

        var hidden = PrivacyCleaner.HideSecrets(text);

        Assert.Equal("Server: http://192.0.2.10:5075/\nX-Sync-Token: (hidden)\nFolder: /home/me/Work\nAuthorization: (hidden)", hidden);
    }
}
