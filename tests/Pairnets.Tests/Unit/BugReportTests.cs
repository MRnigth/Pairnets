using Pairnets.Core;
using Pairnets.Core.Client;
using Pairnets.Core.Settings;
using Pairnets.Tests.Infrastructure;

namespace Pairnets.Tests.Unit;

public class BugReportTests
{
    [Fact]
    public async Task ReportWithoutASessionHasTheErrorTheSettingsAndTheEndOfTheLog()
    {
        using var dir = new TempDir("bug-report");
        var log = dir.Combine("pairnets-20261006.log");
        File.WriteAllLines(log, Enumerable.Range(0, 400).Select(i => $"line {i:000}")
            .Append("X-Sync-Token: fake.token.value"));
        var settings = new ClientSettings
        {
            ServerUrl = "http://192.0.2.10:5075/",
            ProtectedToken = "PROTECTED-SECRET-BLOB",
            Folder = "/home/me/Work",
            DeviceName = "laptop",
            DebugMode = true,
        };
        InvalidOperationException error;
        try
        {
            throw new InvalidOperationException("boom");
        }
        catch (InvalidOperationException ex)
        {
            error = ex;
        }

        var report = await BugReport.BuildAsync(settings, null, log, error, "Windows app");

        Assert.Contains("App:       Windows app " + PairnetsInfo.ProductVersion, report);
        Assert.Contains("System.InvalidOperationException: boom", report);
        Assert.Contains("Server:              http://192.0.2.10:5075/", report);
        Assert.Contains("Debug mode:          yes", report);
        Assert.Contains("No sync session is running", report);
        Assert.Contains("line 399", report);
        Assert.DoesNotContain("line 099", report); // only the last 300 lines
        Assert.Contains("X-Sync-Token: (hidden)", report);
        Assert.DoesNotContain("fake.token", report);
        Assert.DoesNotContain("PROTECTED-SECRET-BLOB", report);
    }

    [Fact]
    public void SaveWritesATimestampedFile()
    {
        using var dir = new TempDir("bug-report");
        var path = BugReport.Save("hello", dir.Combine("logs"));
        Assert.StartsWith("bug-report-", Path.GetFileName(path));
        Assert.EndsWith(".txt", path);
        Assert.Equal("hello", File.ReadAllText(path));
    }
}
