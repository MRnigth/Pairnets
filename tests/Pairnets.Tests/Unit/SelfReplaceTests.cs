using System.Text;
using Pairnets.Core.Client;
using Pairnets.Tests.Infrastructure;

namespace Pairnets.Tests.Unit;

/// <summary>A copy no installer looks after swaps its own file for the one in the release package (on files in a temp folder).</summary>
public class SelfReplaceTests
{
    private static readonly byte[] NewApp = Encoding.UTF8.GetBytes("the new Pairnets");

    private static (TempDir Dir, string App) OldApp(string name)
    {
        var dir = new TempDir("self-replace");
        var app = dir.Combine(name);
        File.WriteAllText(app, "the old Pairnets");
        return (dir, app);
    }

    [Theory]
    [InlineData(UpdatePackage.WindowsZip, "Pairnets.exe")]
    [InlineData(UpdatePackage.LinuxDesktop, "Pairnets")]
    public void PutsTheNewAppInPlace_AndKeepsTheOldOneAsideUntilCleanUp(string asset, string appFile)
    {
        var (dir, app) = OldApp(appFile);
        using var _ = dir;
        var package = dir.Combine(asset);
        File.WriteAllBytes(package, TestUpdatePackages.Make(asset, NewApp));

        SelfReplace.Install(package, appFile, app);

        Assert.Equal(NewApp, File.ReadAllBytes(app));
        Assert.Equal("the old Pairnets", File.ReadAllText(app + ".old"));
        Assert.False(File.Exists(app + ".new"));
        if (asset == UpdatePackage.LinuxDesktop && !OperatingSystem.IsWindows())
            Assert.True(File.GetUnixFileMode(app).HasFlag(UnixFileMode.UserExecute), "The new Linux app must be runnable.");

        SelfReplace.CleanUp(app);
        Assert.False(File.Exists(app + ".old"));
        Assert.Equal(NewApp, File.ReadAllBytes(app));
    }

    [Fact]
    public void APackageWithoutTheApp_ChangesNothing()
    {
        var (dir, app) = OldApp("Pairnets.exe");
        using var _ = dir;
        var package = dir.Combine(UpdatePackage.WindowsZip);
        File.WriteAllBytes(package, TestUpdatePackages.Zip(("README-client.txt", NewApp)));

        Assert.Throws<InvalidDataException>(() => SelfReplace.Install(package, "Pairnets.exe", app));

        Assert.Equal("the old Pairnets", File.ReadAllText(app));
        Assert.False(File.Exists(app + ".old"));
        Assert.False(File.Exists(app + ".new"));
    }

    [Theory]
    [InlineData(UpdatePackage.WindowsZip)]
    [InlineData(UpdatePackage.LinuxDesktop)]
    public void ADamagedPackage_ChangesNothing(string asset)
    {
        var (dir, app) = OldApp("Pairnets");
        using var _ = dir;
        var package = dir.Combine(asset);
        File.WriteAllBytes(package, Encoding.UTF8.GetBytes("not an archive at all"));

        Assert.ThrowsAny<InvalidDataException>(() => SelfReplace.Install(package, "Pairnets", app));

        Assert.Equal("the old Pairnets", File.ReadAllText(app));
        Assert.False(File.Exists(app + ".new"));
    }

    [Fact]
    public void AnOldFileLeftFromALastUpdate_IsReplaced()
    {
        var (dir, app) = OldApp("Pairnets.exe");
        using var _ = dir;
        File.WriteAllText(app + ".old", "an even older Pairnets");
        var package = dir.Combine(UpdatePackage.WindowsZip);
        File.WriteAllBytes(package, TestUpdatePackages.Make(UpdatePackage.WindowsZip, NewApp));

        SelfReplace.Install(package, "Pairnets.exe", app);

        Assert.Equal(NewApp, File.ReadAllBytes(app));
        Assert.Equal("the old Pairnets", File.ReadAllText(app + ".old"));
    }

    [Fact]
    public void AMissingApp_ChangesNothing_AndSaysItCouldNotReplaceItsFile()
    {
        using var dir = new TempDir("self-replace");
        var app = dir.Combine("Pairnets.exe");
        var package = dir.Combine(UpdatePackage.WindowsZip);
        File.WriteAllBytes(package, TestUpdatePackages.Make(UpdatePackage.WindowsZip, NewApp));

        var ex = Assert.ThrowsAny<IOException>(() => SelfReplace.Install(package, "Pairnets.exe", app));

        Assert.False(File.Exists(app));
        Assert.False(File.Exists(app + ".new"));
        Assert.StartsWith("Pairnets couldn't replace its own file", UpdateTexts.Failed(ex, app));
    }

    [Fact]
    public void CleanUp_WithNothingLeft_DoesNothing()
    {
        using var dir = new TempDir("self-replace");
        SelfReplace.CleanUp(dir.Combine("Pairnets.exe"));
        SelfReplace.CleanUp(null);
    }

    [Fact]
    public void EveryFailure_IsSaidInPlainWords_WithNoOfferOfTheBrowser()
    {
        Assert.Equal(ReleaseSigning.NotSignedMessage, UpdateTexts.Failed(new UpdateVerificationException("x", notSigned: true)));
        Assert.Contains("didn't match its checksum", UpdateTexts.Failed(new UpdateVerificationException("x")));
        Assert.Contains("isn't allowed to change its own folder", UpdateTexts.Failed(new UnauthorizedAccessException(), Path.Combine("apps", "Pairnets.exe")));
        Assert.Contains("wasn't a Pairnets package", UpdateTexts.Failed(new InvalidDataException()));
        Assert.StartsWith("The download failed.", UpdateTexts.Failed(new HttpRequestException("boom")));
    }

    [Fact]
    public void TheCardWritesTheReason_WhenNothingWasInstalled_OrTheUpdateIsByHand()
    {
        Assert.True(SideUpdate.From("Pairnets 1.0.60 could not be installed", "Why.", UpdateTexts.TryAgain).ShowsReason);
        Assert.True(SideUpdate.From("Pairnets 1.0.60 is available", UpdateTexts.ByHand("1.0.59"), UpdateTexts.OpenDownloadPage).ShowsReason);
        Assert.False(SideUpdate.From("Pairnets 1.0.60 is available", UpdateTexts.Ready("1.0.59"), UpdateTexts.UpdateNow).ShowsReason);
        Assert.False(SideUpdate.From("Downloading Pairnets 1.0.60…", "Syncing continues.", UpdateTexts.UpdateNow, 40, busy: true).ShowsReason);
    }

    [Fact]
    public void ThePackage_FollowsTheKindOfCopy()
    {
        var installed = UpdatePackage.ForThisComputer(installedCopy: true);
        var loose = UpdatePackage.ForThisComputer(installedCopy: false);
        if (OperatingSystem.IsWindows())
        {
            Assert.Equal(new UpdatePackage(UpdateMethod.Installer, "PairnetsSetup.exe"), installed);
            Assert.Equal(new UpdatePackage(UpdateMethod.ReplaceApp, "pairnets-client-win-x64.zip", "Pairnets.exe"), loose);
        }
        else if (OperatingSystem.IsMacOS())
        {
            Assert.Equal(UpdateMethod.ByHand, loose.Method);
            Assert.EndsWith(".dmg", loose.Asset);
        }
        else if (System.Runtime.InteropServices.RuntimeInformation.OSArchitecture == System.Runtime.InteropServices.Architecture.X64)
        {
            Assert.Equal(new UpdatePackage(UpdateMethod.ReplaceApp, "pairnets-desktop-linux-x64.tar.gz", "Pairnets"), loose);
        }
    }
}
