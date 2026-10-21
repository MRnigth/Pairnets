using Pairnets.Core.Client;
using Pairnets.Core.Settings;
using Pairnets.Core.Sync;

namespace Pairnets.Tests.Unit;

/// <summary>The Settings page's switches apply at once (A11): what a change needs, and that only the switches carry over.</summary>
public class SettingsSwitchesTests
{
    private static ClientSettings Saved() => new()
    {
        ServerUrl = "https://sync.example.com/",
        ProtectedToken = "x",
        Folder = @"D:\Sync",
        DeviceName = "DESKTOP",
        ExtraIgnore = ["*.bak"],
        ParallelTransfers = 4,
    };

    [Fact]
    public void OnlyTheEngineSettingsNeedANewSession()
    {
        var before = Saved();
        var map = before.Clone();
        map.ShowMapSpeed = false;
        map.DebugMode = true;
        map.CheckForUpdates = false;
        Assert.False(SettingsSwitches.NeedsNewSession(before, map));

        var wait = before.Clone();
        wait.WaitForPeerBatches = !before.WaitForPeerBatches;
        Assert.True(SettingsSwitches.NeedsNewSession(before, wait));

        var parallel = before.Clone();
        parallel.ParallelTransfers = 8;
        Assert.True(SettingsSwitches.NeedsNewSession(before, parallel));
    }

    [Fact]
    public void SpeedLimitsChangeOnTheRunningConnection()
    {
        var before = Saved();
        var limited = before.Clone();
        limited.UploadLimitMBps = 5;
        Assert.True(SettingsSwitches.LimitsChanged(before, limited));
        Assert.False(SettingsSwitches.NeedsNewSession(before, limited));

        var zero = before.Clone();
        zero.DownloadLimitMBps = 0; // 0 is no limit, the same as none
        Assert.False(SettingsSwitches.LimitsChanged(before, zero));
    }

    [Fact]
    public void OnlyTheSwitchesCarryOverFromThePage()
    {
        var current = Saved();
        var page = Saved();
        page.Folder = @"E:\Typed\But\Not\Saved";
        page.DeviceName = "TYPED";
        page.ExtraIgnore = ["*.tmp2"];
        page.ShowMapSpeed = false;
        page.DebugMode = true;
        page.StartWithWindows = true;
        page.ParallelTransfers = 2;
        page.UploadLimitMBps = 0;
        page.DownloadLimitMBps = 10;

        var next = SettingsSwitches.WithSwitchesFrom(current, page);

        Assert.Equal(@"D:\Sync", next.Folder); // text boxes wait for Save
        Assert.Equal("DESKTOP", next.DeviceName);
        Assert.Equal(["*.bak"], next.ExtraIgnore);
        Assert.False(next.ShowMapSpeed);
        Assert.True(next.DebugMode);
        Assert.True(next.StartWithWindows);
        Assert.Equal(2, next.ParallelTransfers);
        Assert.Null(next.UploadLimitMBps);
        Assert.Equal(10, next.DownloadLimitMBps);
        Assert.NotSame(current, next);
        Assert.True(current.ShowMapSpeed); // the saved settings object is not changed
    }

    [Fact]
    public void TheFileManagerSwitchAndTheCloudFolderApplyAtOnce()
    {
        var before = Saved();
        var page = Saved();
        page.ShowInFileManager = !before.ShowInFileManager;
        var next = SettingsSwitches.WithSwitchesFrom(before, page);
        Assert.Equal(page.ShowInFileManager, next.ShowInFileManager);
        Assert.True(SettingsSwitches.FileManagerChanged(before, next));
        Assert.False(SettingsSwitches.NeedsNewSession(before, next)); // only the entry is put there or taken away

        page.CloudFolder = CloudFolderMode.OnDemand;
        next = SettingsSwitches.WithSwitchesFrom(before, page);
        Assert.Equal(CloudFolderMode.OnDemand, next.CloudFolder);
        Assert.True(SettingsSwitches.NeedsNewSession(before, next)); // the engine connects to the cloud folder at its start
    }

    [Fact]
    public void TheRightClickSwitchAndWhereFilesLiveApplyAtOnce()
    {
        var before = Saved();
        var page = Saved();
        page.ExplorerRightClick = false;
        var next = SettingsSwitches.WithSwitchesFrom(before, page);
        Assert.False(next.ExplorerRightClick);
        Assert.True(SettingsSwitches.FileManagerChanged(before, next));
        Assert.False(SettingsSwitches.NeedsNewSession(before, next));

        page.Placement["Photos"] = FilePlacement.NestOnly;
        next = SettingsSwitches.WithSwitchesFrom(before, page);
        Assert.Equal(FilePlacement.NestOnly, next.Placement["Photos"]);
        Assert.NotSame(page.Placement, next.Placement);
        Assert.True(SettingsSwitches.PlacementChanged(before, next));
        Assert.True(SettingsSwitches.NeedsNewSession(before, next)); // the engine reads the placement at its start

        var same = WithPlacement(("Photos", FilePlacement.NestOnly));
        Assert.False(SettingsSwitches.PlacementChanged(next, same));
        Assert.True(SettingsSwitches.PlacementChanged(next, WithPlacement(("Photos", FilePlacement.OnThisPc))));
        Assert.True(SettingsSwitches.PlacementChanged(next, WithPlacement(("Videos", FilePlacement.NestOnly))));
    }

    private static ClientSettings WithPlacement((string Folder, FilePlacement Choice) choice)
    {
        var settings = Saved();
        settings.Placement[choice.Folder] = choice.Choice;
        return settings;
    }
}
