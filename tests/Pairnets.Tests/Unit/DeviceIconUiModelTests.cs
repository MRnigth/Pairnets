using Pairnets.Core;
using Pairnets.Core.Client;

namespace Pairnets.Tests.Unit;

/// <summary>Where the chosen icon shows up: the Devices rows, the left side and the map.</summary>
public class DeviceIconUiModelTests
{
    private static readonly DateTimeOffset Now = new(2026, 10, 12, 12, 0, 0, TimeSpan.Zero);

    private static DeviceInfo Device(string name, string? icon, string id) => new(name, Now.AddDays(-30), Now, true, "1.0.60", "Windows", Now, id, icon);

    private static StatusSnapshot Snapshot(bool supportsIcons, params DeviceInfo[] devices) =>
        StatusSnapshot.Initial with { Devices = devices, DeviceIconsSupported = supportsIcons };

    [Fact]
    public void TheFiveChoicesAreInTheDesignsOrderAndEachHasADrawingAndAWireValue()
    {
        Assert.Equal(["Desktop", "Laptop", "Office", "Mini PC", "TV / media"], DeviceIcon.Choices.Select(DeviceIcon.Label));
        Assert.Equal(DeviceIcons.All, DeviceIcon.Choices.Select(DeviceIcon.ToWire));
        Assert.Equal(["I.Desktop", "I.Laptop", "I.Office", "I.MiniPc", "I.Tv"], DeviceIcon.Choices.Select(DeviceIcon.ResourceKey));
    }

    [Fact]
    public void DevicesRowsCarryTheIconTheIdAndThePencilOnlyForANestThatKnowsIcons()
    {
        var devices = new[] { Device("DESKTOP", "office", "d1"), Device("LAPTOP", null, "d2") };

        var rows = DeviceRow.From(devices, "DESKTOP", Now, Snapshot(true, devices));
        Assert.Equal(DeviceIconKind.Office, rows[0].Icon);
        Assert.Equal("d1", rows[0].Id);
        Assert.All(rows, r => Assert.True(r.CanChooseIcon));
        Assert.Equal(DeviceIconKind.Laptop, rows[1].Icon); // nothing chosen: guessed from the name

        var old = DeviceRow.From(devices, "DESKTOP", Now, Snapshot(false, devices));
        Assert.All(old, r => Assert.False(r.CanChooseIcon));
        Assert.Equal(DeviceIconKind.Desktop, old[0].Icon); // an old nest: desktop, whatever the row says
        Assert.Equal(DeviceIconKind.Laptop, old[1].Icon);
    }

    [Fact]
    public void ARowUpdatesInPlaceWhenTheIconChanges()
    {
        var devices = new[] { Device("DESKTOP", null, "d1") };
        var view = new DeviceRowView(DeviceRow.From(devices, "DESKTOP", Now, Snapshot(true, devices))[0]);
        var changed = new List<string?>();
        view.PropertyChanged += (_, e) => changed.Add(e.PropertyName);

        var after = new[] { Device("DESKTOP", "tv", "d1") };
        view.Update(DeviceRow.From(after, "DESKTOP", Now, Snapshot(true, after))[0]);

        Assert.Equal(DeviceIconKind.Tv, view.Icon);
        Assert.Contains(nameof(DeviceRowView.Icon), changed);
    }

    [Fact]
    public void TheLeftSideAndTheMapUseTheChosenIcon()
    {
        var s = Snapshot(true, Device("DESKTOP", "mini", "d1"), Device("MEDIA-PC", "tv", "d2")) with { Status = Pairnets.Core.Sync.RunnerStatus.Idle };
        var side = SidePanelModel.Build(s, "DESKTOP", 0, 0, null, Now);
        Assert.Equal(DeviceIconKind.Mini, side.This.Icon);
        Assert.Equal(DeviceIconKind.Tv, side.AllOthers.Single(o => o.Name == "MEDIA-PC").Icon);

        var map = DeviceMap.Build(s, "DESKTOP", Now);
        Assert.Equal(DeviceIconKind.Mini, map.Here.Icon);
        Assert.Equal(DeviceIconKind.Tv, map.Other.Icon);

        var old = s with { DeviceIconsSupported = false };
        Assert.Equal(DeviceIconKind.Desktop, SidePanelModel.Build(old, "DESKTOP", 0, 0, null, Now).This.Icon);
        Assert.Equal(DeviceIconKind.Desktop, DeviceMap.Build(old, "DESKTOP", Now).Other.Icon);
    }
}
