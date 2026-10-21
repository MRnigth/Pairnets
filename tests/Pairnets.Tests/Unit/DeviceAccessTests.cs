using System.Text.Json;
using Pairnets.Server.Storage;

namespace Pairnets.Tests.Unit;

/// <summary>What a friend's computer may do (friend invites): which paths a share covers, and what the service may send.</summary>
public sealed class DeviceAccessTests
{
    private static readonly DeviceAccess PhotosAndSchool = new(DeviceAccess.View, ["Photos", "School"]);

    [Theory]
    [InlineData("Photos", true)]
    [InlineData("Photos/beach.jpg", true)]
    [InlineData("Photos/2026/summer/beach.jpg", true)]
    [InlineData("School/notes.txt", true)]
    [InlineData("Photos2/beach.jpg", false)] // a folder whose name only starts the same
    [InlineData("Photo", false)]
    [InlineData("photos/beach.jpg", false)] // exact case
    [InlineData("PHOTOS", false)]
    [InlineData("Taxes/2026.pdf", false)]
    [InlineData("beach.jpg", false)]
    [InlineData("", false)]
    [InlineData(null, false)]
    public void AShareCoversItsFoldersAndWhatIsInThem(string? path, bool shared) => Assert.Equal(shared, PhotosAndSchool.Allows(path));

    [Theory]
    [InlineData("Taxes/2026.pdf")]
    [InlineData("anything")]
    public void TheWholeNestCoversEverything(string path)
    {
        Assert.True(DeviceAccess.Full.Allows(path));
        Assert.True(new DeviceAccess(DeviceAccess.Edit).Allows(path));
    }

    [Fact]
    public void OnlyViewOnlyCannotWrite()
    {
        Assert.True(DeviceAccess.Full.CanWrite);
        Assert.True(new DeviceAccess(DeviceAccess.Edit).CanWrite);
        Assert.False(new DeviceAccess(DeviceAccess.View).CanWrite);
        Assert.True(DeviceAccess.Full.IsOwner);
        Assert.False(new DeviceAccess(DeviceAccess.Edit).IsOwner);
    }

    [Theory]
    [InlineData("Photos", true)]
    [InlineData("My Documents", true)]
    [InlineData("Ørsted 2026", true)]
    [InlineData("", false)]
    [InlineData(".", false)]
    [InlineData("..", false)]
    [InlineData("Photos/2026", false)]
    [InlineData("Photos\\2026", false)]
    [InlineData("Pho\ntos", false)]
    public void FolderNamesAreTopLevelNames(string name, bool valid) => Assert.Equal(valid, DeviceAccess.IsFolderName(name));

    [Fact]
    public void FolderNamesAreAtMost255Characters()
    {
        Assert.True(DeviceAccess.IsFolderName(new string('a', 255)));
        Assert.False(DeviceAccess.IsFolderName(new string('a', 256)));
    }

    [Theory]
    [InlineData("acc_annaberg01", true)]
    [InlineData("acc-1", true)]
    [InlineData("", false)]
    [InlineData("acc 1", false)]
    [InlineData("acc/1", false)]
    [InlineData("acc_ø", false)]
    public void PersonIdsAreTheServicesAccountIds(string id, bool valid) => Assert.Equal(valid, DeviceAccess.IsPersonId(id));

    [Fact]
    public void PersonIdsAreAtMost64Characters()
    {
        Assert.True(DeviceAccess.IsPersonId(new string('a', 64)));
        Assert.False(DeviceAccess.IsPersonId(new string('a', 65)));
    }

    private static (bool Ok, DeviceAccess Access) Read(string json, bool ownerAllowed = true)
    {
        using var doc = JsonDocument.Parse(json);
        var ok = DeviceAccess.TryRead(doc.RootElement, ownerAllowed, out var access);
        return (ok, access);
    }

    [Theory]
    [InlineData("""{}""", "owner", null)]
    [InlineData("""{"access":null,"folders":null}""", "owner", null)]
    [InlineData("""{"access":"owner"}""", "owner", null)]
    [InlineData("""{"access":"edit"}""", "edit", null)]
    [InlineData("""{"access":"view","folders":null}""", "view", null)]
    [InlineData("""{"access":"view","folders":["Photos"]}""", "view", "Photos")]
    [InlineData("""{"access":"edit","folders":["Photos","School","Photos"]}""", "edit", "Photos,School")]
    public void WhatTheServiceSendsIsRead(string json, string level, string? folders)
    {
        var (ok, access) = Read(json);
        Assert.True(ok);
        Assert.Equal(level, access.Level);
        Assert.Equal(folders, access.Folders is null ? null : string.Join(',', access.Folders));
    }

    [Theory]
    [InlineData("""{"access":"admin"}""")]
    [InlineData("""{"access":"Owner"}""")]
    [InlineData("""{"access":1}""")]
    [InlineData("""{"access":"view","folders":"Photos"}""")]
    [InlineData("""{"access":"view","folders":[]}""")]
    [InlineData("""{"access":"view","folders":[1]}""")]
    [InlineData("""{"access":"view","folders":[null]}""")]
    [InlineData("""{"access":"view","folders":["Photos/2026"]}""")]
    [InlineData("""{"access":"view","folders":[".."]}""")]
    [InlineData("""{"folders":["Photos"]}""")] // the owner never has folders
    [InlineData("""{"access":"owner","folders":["Photos"]}""")]
    [InlineData("""[]""")]
    public void AnythingNotExactlyRightIsRefused(string json) => Assert.False(Read(json).Ok);

    [Fact]
    public void AtMostTwentyFolders()
    {
        var twenty = JsonSerializer.Serialize(new { access = "view", folders = Enumerable.Range(1, 20).Select(i => $"F{i}") });
        var twentyOne = JsonSerializer.Serialize(new { access = "view", folders = Enumerable.Range(1, 21).Select(i => $"F{i}") });
        Assert.True(Read(twenty).Ok);
        Assert.False(Read(twentyOne).Ok);
    }

    [Fact]
    public void AFriendsShareCannotBeTheOwners()
    {
        Assert.False(Read("""{"access":"owner"}""", ownerAllowed: false).Ok);
        Assert.False(Read("""{}""", ownerAllowed: false).Ok);
        Assert.True(Read("""{"access":"view"}""", ownerAllowed: false).Ok);
    }

    [Fact]
    public void FoldersSurviveTheDatabaseAndADamagedValueSharesNothing()
    {
        Assert.Null(DeviceAccess.FoldersToJson(null));
        Assert.Null(DeviceAccess.FoldersFromJson(null));
        Assert.Equal(new[] { "Photos", "Ørsted" }, DeviceAccess.FoldersFromJson(DeviceAccess.FoldersToJson(["Photos", "Ørsted"]))!);
        Assert.Empty(DeviceAccess.FoldersFromJson("not json")!);
        Assert.Empty(DeviceAccess.FoldersFromJson("""["../x"]""")!);
        Assert.False(new DeviceAccess(DeviceAccess.View, DeviceAccess.FoldersFromJson("not json")).Allows("Photos/a.jpg"));
    }
}
