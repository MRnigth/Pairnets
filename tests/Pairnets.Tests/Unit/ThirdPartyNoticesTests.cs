using System.Text.RegularExpressions;
using Pairnets.Core;
using Pairnets.Tests.Infrastructure;
using Xunit;

namespace Pairnets.Tests.Unit;

/// <summary>
/// The licence texts of the open-source parts the apps ship (deploy/THIRD-PARTY-NOTICES.txt): MIT, BSD, Apache and OFL
/// ask for them to go along with the program, so every download carries the file and Core carries it inside.
/// </summary>
public class ThirdPartyNoticesTests
{
    private static string Notices => RepoPaths.Read("deploy", "THIRD-PARTY-NOTICES.txt");

    [Fact]
    public void CoreCarriesTheNoticesFile()
    {
        Assert.Equal(Notices, ThirdPartyNotices.Text.ReplaceLineEndings("\n"));
        Assert.Contains("Copyright (c) 2026 MRnigth", ThirdPartyNotices.Text); // Pairnets' own licence comes first
    }

    [Fact]
    public void EveryShippedPackageIsListed()
    {
        // After adding or upgrading a package: run scripts/make-notices.sh (and add the package to deploy/notices/head.txt).
        var props = RepoPaths.Read("Directory.Packages.props");
        var testOnly = new[] { "xunit", "xunit.runner.visualstudio", "Microsoft.NET.Test.Sdk", "Microsoft.Playwright", "Avalonia.Headless.XUnit" };
        var head = Notices[..Notices.IndexOf("A. MIT licence", StringComparison.Ordinal)];
        foreach (Match m in Regex.Matches(props, "PackageVersion Include=\"([^\"]+)\""))
        {
            var name = m.Groups[1].Value;
            if (testOnly.Contains(name)) continue;
            var listed = name switch
            {
                "Microsoft.AspNetCore.SignalR.Client" => "SignalR",
                "Microsoft.Extensions.Logging" or "Microsoft.Extensions.Logging.Abstractions" => "Microsoft.Extensions.*",
                "Avalonia.Desktop" or "Avalonia.Themes.Fluent" => "Avalonia",
                _ => name,
            };
            Assert.True(head.Contains(listed, StringComparison.Ordinal), $"{name} is not in deploy/notices/head.txt");
        }
        foreach (var text in new[] { "Apache License", "SIL OPEN FONT LICENSE", "Inno Setup License", "Skia", "HarfBuzz", "public domain" })
            Assert.Contains(text, Notices, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void EveryDownloadCarriesTheNotices()
    {
        var workflow = RepoPaths.Read(".github", "workflows", "release.yml");
        Assert.Contains("publish/client/THIRD-PARTY-NOTICES.txt", workflow); // the Windows zip
        Assert.Contains("cp deploy/THIRD-PARTY-NOTICES.txt LICENSE dist/pairnets-desktop-linux-x64/", workflow); // the Linux app
        Assert.Contains(@"Source: ""{#SourceDir}\THIRD-PARTY-NOTICES.txt""", RepoPaths.Read("deploy", "installer", "pairnets.iss")); // PairnetsSetup.exe
        Assert.Contains("deploy/THIRD-PARTY-NOTICES.txt", RepoPaths.Read("deploy", "macos", "make-app.sh")); // the Mac app
        Assert.Contains("deploy/THIRD-PARTY-NOTICES.txt", RepoPaths.Read("scripts", "package-server.sh")); // the nest
    }

    [Fact]
    public void OpenSourceLicencesOpensACopyOfTheNotices()
    {
        var target = ThirdPartyNotices.OpenTarget();
        Assert.Equal(ThirdPartyNotices.CopyPath, target);
        Assert.Equal(ThirdPartyNotices.Text, File.ReadAllText(target));
    }
}
