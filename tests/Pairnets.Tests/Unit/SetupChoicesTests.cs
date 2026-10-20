using System.Text.RegularExpressions;
using Pairnets.Core.Settings;
using Pairnets.Core.Sync;
using Pairnets.Tests.Infrastructure;

namespace Pairnets.Tests.Unit;

/// <summary>The installer's "Select Additional Tasks" page, as the app takes it into its settings.</summary>
public class SetupChoicesTests
{
    [Fact]
    public void TheTickedBoxesBecomeSettings()
    {
        var settings = new ClientSettings { CloudFolder = CloudFolderMode.Off };
        SetupChoices.Apply(settings, "explorerside=0;explorermenu=1;syncticks=1;where=nest;checkupdates=0;updatenest=1;autostart=1");

        Assert.False(settings.ShowInFileManager);
        Assert.True(settings.ExplorerRightClick);
        Assert.Equal(CloudFolderMode.KeepAllHere, settings.CloudFolder);
        Assert.Equal(FilePlacement.NestOnly, settings.Placement[""]);
        Assert.False(settings.CheckForUpdates);
        Assert.True(settings.AutoUpdateServer);
        Assert.True(settings.StartWithWindows);

        SetupChoices.Apply(settings, "where=here;syncticks=0");
        Assert.Empty(settings.Placement);
        Assert.Equal(CloudFolderMode.Off, settings.CloudFolder);
    }

    [Fact]
    public void UnknownOrBrokenPartsChangeNothing()
    {
        var settings = new ClientSettings();
        SetupChoices.Apply(settings, "explorerside=maybe;=1;nothing;where=moon;something=1");
        Assert.True(settings.ShowInFileManager);
        Assert.Empty(settings.Placement);
    }

    [Fact]
    public void TheInstallerWritesEveryChoiceTheAppReads()
    {
        var iss = File.ReadAllText(RepoPaths.Of("deploy", "installer", "pairnets.iss"));
        foreach (var key in new[] { "explorerside", "explorermenu", "syncticks", "where", "checkupdates", "updatenest", "autostart" })
            Assert.Matches(new Regex(@"'[;]?" + key + "="), iss);
        foreach (var task in new[] { "startmenu", "desktopicon", "explorerside", "explorermenu", "syncticks", "wherehere", "whereondemand", "wherenest", "checkupdates", "updatenest", "autostart" })
            Assert.Contains($"Name: \"{task}\"", iss);
        Assert.Contains("not WizardSilent()", iss); // a silent update leaves the settings alone
    }
}
