using Pairnets.Core.Client;
using Pairnets.Tests.Infrastructure;

namespace Pairnets.Tests.Unit;

/// <summary>The Mac app swaps its whole Pairnets.app: finding it, checking the new one, and the notes between the two apps.</summary>
public class MacAppUpdateTests
{
    private const string Plist = """
        <?xml version="1.0" encoding="UTF-8"?>
        <plist version="1.0"><dict>
          <key>CFBundleIdentifier</key><string>{0}</string>
          <key>CFBundleShortVersionString</key><string>{1}</string>
        </dict></plist>
        """;

    /// <summary>ditto/codesign stand-ins: "unpacking" writes the app the test asked for; nothing here needs a Mac.</summary>
    private sealed class FakeTools(string id = MacAppUpdate.BundleId, string version = "1.0.62", bool withProgram = true, int codesign = 0, int ditto = 0) : IMacTools
    {
        public List<string> Calls { get; } = [];
        public (string File, string[] Args)? Started { get; private set; }

        public (int ExitCode, string Output) Run(string file, IReadOnlyList<string> args)
        {
            Calls.Add(Path.GetFileName(file));
            if (Path.GetFileName(file) == "ditto")
            {
                if (ditto == 0)
                {
                    var app = Path.Combine(args[^1], MacAppUpdate.BundleName, "Contents");
                    Directory.CreateDirectory(Path.Combine(app, "MacOS"));
                    if (withProgram)
                        File.WriteAllText(Path.Combine(app, "MacOS", "Pairnets"), "new");
                    File.WriteAllText(Path.Combine(app, "Info.plist"), string.Format(Plist, id, version));
                }
                return (ditto, string.Empty);
            }
            return Path.GetFileName(file) == "codesign" ? (codesign, string.Empty) : (0, string.Empty);
        }

        public void StartDetached(string file, IReadOnlyList<string> args) => Started = (file, [.. args]);
    }

    private static string Bundle(TempDir dir) => dir.Combine("Applications", MacAppUpdate.BundleName);

    [Theory]
    [InlineData("/Applications/Pairnets.app/Contents/MacOS/Pairnets", "/Applications/Pairnets.app")]
    [InlineData("/Users/a/Apps/Pairnets.app/Contents/MacOS/Pairnets", "/Users/a/Apps/Pairnets.app")]
    [InlineData("/opt/pairnets/Pairnets", null)]
    [InlineData("", null)]
    [InlineData(null, null)]
    public void TheAppIsFoundFromTheProgramInsideIt(string? program, string? bundle)
    {
        var found = MacAppUpdate.BundleOf(program?.Replace('/', Path.DirectorySeparatorChar));
        Assert.Equal(bundle?.Replace('/', Path.DirectorySeparatorChar), found);
    }

    [Theory]
    [InlineData("/Applications/Pairnets.app/Contents/MacOS/Pairnets", false)]
    [InlineData("/Volumes/Pairnets/Pairnets.app/Contents/MacOS/Pairnets", true)]
    [InlineData("/private/var/folders/x/AppTranslocation/ABC/d/Pairnets.app/Contents/MacOS/Pairnets", true)]
    [InlineData("/usr/local/bin/pairnets", true)]
    public void AnAppRunFromADiskImageOrDownloadsCantReplaceItself(string program, bool refused) =>
        Assert.Equal(refused, MacAppUpdate.WhyNot(program) is not null);

    [Fact]
    public void TheMacPicksTheZip_OnlyWhereItCanSwapItself()
    {
        if (!OperatingSystem.IsMacOS())
            return;
        var inApplications = UpdatePackage.ForThisComputer(false, "/Applications/Pairnets.app/Contents/MacOS/Pairnets");
        Assert.Equal(UpdateMethod.ReplaceBundle, inApplications.Method);
        Assert.EndsWith(".zip", inApplications.Asset);
        var onImage = UpdatePackage.ForThisComputer(false, "/Volumes/Pairnets/Pairnets.app/Contents/MacOS/Pairnets");
        Assert.Equal(UpdateMethod.ByHand, onImage.Method);
        Assert.EndsWith(".dmg", onImage.Asset);
        Assert.Contains("disk image", onImage.WhyByHand);
    }

    [Fact]
    public void ARightPackageIsUnpackedNextToTheApp_AndChecked()
    {
        using var dir = new TempDir("mac-update");
        var tools = new FakeTools();

        var staged = MacAppUpdate.Stage(dir.Combine("p.zip"), Bundle(dir), "1.0.62", tools);

        Assert.Equal(Path.Combine(MacAppUpdate.StagingDir(Bundle(dir)), MacAppUpdate.BundleName), staged);
        Assert.Equal(["ditto", "codesign", "xattr"], tools.Calls);
        Assert.Equal(Path.GetDirectoryName(Bundle(dir)), Path.GetDirectoryName(MacAppUpdate.StagingDir(Bundle(dir)))); // same disk: the move is a rename
    }

    [Fact]
    public void AFiveNumberVersionMatchesTheFourNumbersVersionJsonCarries()
    {
        using var dir = new TempDir("mac-update");
        MacAppUpdate.Stage(dir.Combine("p.zip"), Bundle(dir), "1.0.62", new FakeTools(version: "1.0.62.3"));
    }

    [Theory]
    [InlineData("other.app", "1.0.62", true, 0, 0, "another app")]
    [InlineData(MacAppUpdate.BundleId, "1.0.61", true, 0, 0, "version 1.0.61, not 1.0.62")]
    [InlineData(MacAppUpdate.BundleId, "1.0.620", true, 0, 0, "version 1.0.620, not 1.0.62")]
    [InlineData(MacAppUpdate.BundleId, "1.0.62", false, 0, 0, "has no Pairnets.app")]
    [InlineData(MacAppUpdate.BundleId, "1.0.62", true, 1, 0, "signature is damaged")]
    [InlineData(MacAppUpdate.BundleId, "1.0.62", true, 0, 1, "damaged")]
    public void APackageThatIsNotTheRightUpdate_IsRefused_AndLeavesNothingBehind(string id, string version, bool withProgram, int codesign, int ditto, string message)
    {
        using var dir = new TempDir("mac-update");
        Directory.CreateDirectory(Path.GetDirectoryName(Bundle(dir))!);

        var ex = Assert.Throws<InvalidDataException>(() =>
            MacAppUpdate.Stage(dir.Combine("p.zip"), Bundle(dir), "1.0.62", new FakeTools(id, version, withProgram, codesign, ditto)));

        Assert.Contains(message, ex.Message);
        Assert.False(Directory.Exists(MacAppUpdate.StagingDir(Bundle(dir))));
        Assert.Contains("Nothing was installed", UpdateTexts.Failed(ex));
    }

    [Fact]
    public void TheSwapIsHandedToTheScript_WithTheWaitForThisApp()
    {
        using var dir = new TempDir("mac-update");
        var tools = new FakeTools();
        var staged = MacAppUpdate.Stage(dir.Combine("p.zip"), Bundle(dir), "1.0.62", tools);

        MacAppUpdate.StartSwap(Bundle(dir), staged, "1.0.62", 4242, dir.Combine("local"), dir.Combine("logs"), tools);

        var (file, args) = tools.Started!.Value;
        Assert.Equal("/bin/bash", file);
        Assert.EndsWith("update-app.sh", args[0]);
        Assert.Equal(["4242", Bundle(dir), staged, MacAppUpdate.BackupDir(Bundle(dir)), MacAppUpdate.ReadyFile(dir.Combine("local")),
            MacAppUpdate.FailedFile(dir.Combine("local")), MacAppUpdate.LogFile(dir.Combine("logs")), "1.0.62"], args[1..]);
        var script = File.ReadAllText(args[0]);
        Assert.StartsWith("#!/bin/bash", script);
        Assert.DoesNotContain('\r', script);
    }

    [Fact]
    public void TheNewAppSaysItRuns_AndAFailedUpdateIsToldOnce()
    {
        using var dir = new TempDir("mac-update");
        MacAppUpdate.SignalStarted(dir.Combine("local"));
        Assert.True(File.Exists(MacAppUpdate.ReadyFile(dir.Combine("local"))));

        Assert.Null(MacAppUpdate.TakeFailedNote(dir.Combine("local")));
        File.WriteAllText(MacAppUpdate.FailedFile(dir.Combine("local")), "Pairnets 1.0.62 didn't start, so your old version was put back.\n");
        Assert.Equal("Pairnets 1.0.62 didn't start, so your old version was put back.", MacAppUpdate.TakeFailedNote(dir.Combine("local")));
        Assert.Null(MacAppUpdate.TakeFailedNote(dir.Combine("local")));
    }

    [Fact]
    public void WhatAnInterruptedUpdateLeftNextToTheApp_IsRemoved()
    {
        using var dir = new TempDir("mac-update");
        var program = Path.Combine(Bundle(dir), "Contents", "MacOS", "Pairnets");
        Directory.CreateDirectory(MacAppUpdate.StagingDir(Bundle(dir)));
        Directory.CreateDirectory(MacAppUpdate.BackupDir(Bundle(dir)));
        File.WriteAllText(Path.Combine(MacAppUpdate.BackupDir(Bundle(dir)), "x"), "x");

        MacAppUpdate.CleanUp(program);

        Assert.False(Directory.Exists(MacAppUpdate.StagingDir(Bundle(dir))));
        Assert.False(Directory.Exists(MacAppUpdate.BackupDir(Bundle(dir))));
        MacAppUpdate.CleanUp(null);
        MacAppUpdate.CleanUp("/not/an/app");
    }

    [Fact]
    public void TheCardTellsAMacOnADiskImageWhatToDo()
    {
        var text = UpdateTexts.ByHand("1.0.61", UpdateTexts.MacNotInApplications);
        Assert.Contains("drag it to Applications", text);
        Assert.Contains("updates by itself", text);
        Assert.Contains("old version was put back", UpdateTexts.MacPutBack("1.0.62"));
    }

    [Fact]
    public void TheRealSwapScript_PassesItsFourCases()
    {
        if (OperatingSystem.IsWindows())
            return; // (the cases need bash and kill; CI and the pre-deploy check run them on Linux, CI also on a Mac)
        var psi = new System.Diagnostics.ProcessStartInfo("bash") { RedirectStandardOutput = true, RedirectStandardError = true };
        psi.ArgumentList.Add(Path.Combine(RepoPaths.Root, "tests", "macos", "update-check.sh"));
        using var p = System.Diagnostics.Process.Start(psi)!;
        var output = p.StandardOutput.ReadToEnd();
        p.WaitForExit();
        Assert.True(p.ExitCode == 0, output);
    }
}
