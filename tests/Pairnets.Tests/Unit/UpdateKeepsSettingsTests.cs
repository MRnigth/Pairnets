using Pairnets.Core.Client;
using Pairnets.Core.Settings;
using Pairnets.Tests.Infrastructure;

namespace Pairnets.Tests.Unit;

/// <summary>
/// An update keeps the sign-in and the settings: the installer is for the first install only, updates run hidden, the
/// settings file has a safety copy, and the one-time changes of a new version run once.
/// </summary>
public sealed class UpdateKeepsSettingsTests
{
    private static ClientSettings SignedIn() => new()
    {
        ServerUrl = "https://sync.example.com/n/nst_example/",
        ProtectedToken = "protected-key",
        DeviceId = "dev_1",
        DeviceName = "Office PC",
        Folder = Path.Combine(Path.GetTempPath(), "Pairnets"),
        AccountEmail = "you@example.com",
        FirstRunCompleted = true,
        StartWithWindows = true,
        ParallelTransfers = 4,
    };

    [Fact]
    public void EverySaveLeavesASafetyCopyOfWhatItWrote()
    {
        using var dir = new TempDir("settings-bak");
        var path = dir.Combine("settings.json");

        SettingsStore.Save(path, SignedIn());

        Assert.Equal(File.ReadAllText(path), File.ReadAllText(path + SettingsStore.BackupSuffix));
        Assert.False(File.Exists(path + ".tmp"));
    }

    [Fact]
    public void AMissingSettingsFile_IsBroughtBackFromTheSafetyCopy_AndSaysSo()
    {
        using var dir = new TempDir("settings-bak");
        var path = dir.Combine("settings.json");
        SettingsStore.Save(path, SignedIn());
        File.Delete(path);
        var said = new List<string>();

        var loaded = SettingsStore.Load(path, said.Add);

        Assert.True(loaded.IsComplete);
        Assert.Equal(("protected-key", "Office PC", 4), (loaded.ProtectedToken, loaded.DeviceName, loaded.ParallelTransfers));
        Assert.True(File.Exists(path), "The safety copy is put back as settings.json.");
        Assert.Contains("missing", Assert.Single(said));
    }

    [Fact]
    public void AnUnreadableSettingsFile_IsKeptAsCorrupt_AndTheSafetyCopyIsUsed()
    {
        using var dir = new TempDir("settings-bak");
        var path = dir.Combine("settings.json");
        SettingsStore.Save(path, SignedIn());
        File.WriteAllText(path, "{ not json");
        var said = new List<string>();

        var loaded = SettingsStore.Load(path, said.Add);

        Assert.True(loaded.IsComplete && loaded.FirstRunCompleted);
        Assert.Equal("{ not json", File.ReadAllText(path + ".corrupt"));
        Assert.Contains("safety copy", Assert.Single(said));
    }

    [Fact]
    public void WithoutASafetyCopy_AnUnreadableFileStartsEmpty_AndSaysSo()
    {
        using var dir = new TempDir("settings-bak");
        var path = dir.Combine("settings.json");
        File.WriteAllText(path, "{ not json");
        var said = new List<string>();

        var loaded = SettingsStore.Load(path, said.Add);

        Assert.False(loaded.IsComplete);
        Assert.Contains("no safety copy", Assert.Single(said));
    }

    [Fact]
    public void AComputerNeverSetUp_StartsEmpty_WithoutAWord()
    {
        using var dir = new TempDir("settings-bak");
        var said = new List<string>();

        var loaded = SettingsStore.Load(dir.Combine("settings.json"), said.Add);

        Assert.False(loaded.IsComplete);
        Assert.Empty(said);
    }

    [Fact]
    public void ResetAlsoForgetsTheSafetyCopy_SoItCannotBringTheSettingsBack()
    {
        using var dir = new TempDir("settings-bak");
        var path = dir.Combine("roaming", "settings.json");
        SettingsStore.Save(path, SignedIn());

        LocalReset.Run(SettingsStore.Load(path), protector: null, path, dir.Combine("local"));

        Assert.False(File.Exists(path + SettingsStore.BackupSuffix));
        Assert.False(SettingsStore.Load(path).IsComplete);
    }

    [Fact]
    public void CheckForUpdates_IsSwitchedOnOnce_ForSettingsAnOlderPairnetsSaved()
    {
        using var dir = new TempDir("settings-upgrade");
        var path = dir.Combine("settings.json");
        // A settings file from 1.0.59, left with update checks off (as the test set-ups were): no settingsVersion in it.
        File.WriteAllText(path, """{ "serverUrl": "https://sync.example.com/", "protectedToken": "k", "folder": "C:\\P", "deviceName": "PC", "firstRunCompleted": true, "checkForUpdates": false }""");

        var old = SettingsStore.Load(path);
        Assert.Equal(0, old.SettingsVersion);
        var changes = SettingsUpgrades.Apply(old);

        Assert.True(old.CheckForUpdates);
        Assert.Equal(SettingsUpgrades.Current, old.SettingsVersion);
        Assert.Contains(changes, c => c.Contains("Check for updates", StringComparison.Ordinal));
        Assert.True(old.IsComplete, "The sign-in is untouched.");

        // Switched off again by the person afterwards: it stays off.
        old.CheckForUpdates = false;
        SettingsStore.Save(path, old);
        var later = SettingsStore.Load(path);
        Assert.Empty(SettingsUpgrades.Apply(later));
        Assert.False(later.CheckForUpdates);
    }

    [Fact]
    public void NewSettings_AreCurrent_SoTheFirstRunsChoiceIsNeverOverruled()
    {
        var fresh = new ClientSettings { CheckForUpdates = false };

        Assert.Empty(SettingsUpgrades.Apply(fresh));
        Assert.False(fresh.CheckForUpdates);
    }

    [Fact]
    public void ASignedOutComputer_IsAskedToSignInAgain_NotGreetedAsNew()
    {
        var signedOut = SignedIn();
        signedOut.ProtectedToken = null;
        signedOut.DeviceId = null;
        signedOut.FirstRunCompleted = false;

        var (title, sub) = SignInTexts.First(signedOut);
        Assert.Equal("Sign in again", title);
        Assert.Contains(signedOut.Folder!, sub);
        Assert.Contains("Office PC", sub);

        Assert.Equal("Sign in to Pairnets", SignInTexts.First(new ClientSettings()).Title);
    }

    [Fact]
    public void TheInstalledApp_UpdatesWithTheInstallerHidden()
    {
        var arguments = UpdatePackage.InstallerArguments.Split(' ');

        Assert.Contains("/VERYSILENT", arguments); // no wizard and no progress window
        Assert.DoesNotContain("/SILENT", arguments);
        Assert.Contains("/SUPPRESSMSGBOXES", arguments);
        Assert.Contains("/CLOSEAPPLICATIONS", arguments);
    }

    [Fact]
    public void TheInstaller_IsForTheFirstInstall_AndNeverTouchesTheAppsData()
    {
        var iss = RepoPaths.Read("deploy", "installer", "pairnets.iss");

        // Nothing the installer or its uninstaller deletes on its own: settings, the saved sign-in and the sync notes stay.
        Assert.DoesNotMatch(@"(?m)^\[(Install|Uninstall)Delete\]", iss);
        Assert.DoesNotContain("{userappdata}", iss);
        // The same AppId and folder every version, so an update lands on the installed copy.
        Assert.Contains("AppId={{5DB04D62-50C8-4967-B220-449C44AC1BA3}", iss);
        Assert.Contains(@"Uninstall\{5DB04D62-50C8-4967-B220-449C44AC1BA3}_is1", iss);
        Assert.Contains(@"DefaultDirName={localappdata}\Programs\Pairnets", iss);
        // Started again by hand over an installed Pairnets: no pages to go through.
        Assert.Contains("function ShouldSkipPage", iss);
        Assert.Contains("(PageID = wpSelectTasks)", iss);
        // After the hidden update, the new Pairnets starts and says it was updated.
        Assert.Contains($@"Parameters: ""{SelfReplace.AfterUpdateArgument}""; Flags: nowait; Check: WizardSilent", iss);
        // Choices ticked in the installer reach the app's settings only on the first install, never on an update.
        foreach (var line in iss.Split('\n').Where(l => l.Contains("LeaveChoicesForTheApp()", StringComparison.Ordinal) && !l.TrimStart().StartsWith("procedure", StringComparison.Ordinal)))
            Assert.True(line.Contains("IsFirstInstall()", StringComparison.Ordinal) || line.Contains("not WasInstalled", StringComparison.Ordinal), "Not guarded by the first install: " + line.Trim());
    }
}
