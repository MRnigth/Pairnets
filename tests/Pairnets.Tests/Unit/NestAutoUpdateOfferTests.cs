using Pairnets.Core.Client;
using Pairnets.Core.Settings;
using Pairnets.Core.Sync;
using Xunit;

namespace Pairnets.Tests.Unit;

public class NestAutoUpdateOfferTests
{
    private static ClientSettings Nest(string id, string name, bool auto = false, string? access = null) => new()
    {
        NestId = id, NestLabel = name, ServerUrl = "https://" + id + ".example", ProtectedToken = "t", Folder = "/f/" + id,
        DeviceName = "PC-1", AutoUpdateServer = auto, Access = access,
    };

    [Fact]
    public void AFreshInstallGetsNoCardAndAnUpdateDoes()
    {
        // A fresh install: remembered, but not counted as an update.
        var fresh = new ClientSettings();
        Assert.True(NestAutoUpdateOffer.NoteStart(fresh, "1.0.62"));
        Assert.Equal("1.0.62", fresh.LastRunVersion);
        Assert.Null(fresh.NestAutoUpdateOfferFor);
        Assert.False(NestAutoUpdateOffer.NoteStart(fresh, "1.0.62"));

        // The next version: the card is due.
        Assert.True(NestAutoUpdateOffer.NoteStart(fresh, "1.0.63"));
        Assert.Equal("1.0.63", fresh.NestAutoUpdateOfferFor);

        // Set up before the version was remembered (the update that brings this card): counts as updated.
        var older = new ClientSettings { FirstRunCompleted = true };
        Assert.True(NestAutoUpdateOffer.NoteStart(older, "1.0.62"));
        Assert.Equal("1.0.62", older.NestAutoUpdateOfferFor);
    }

    [Fact]
    public void TheCardNamesOnlyOwnNestsThatWaitToBeAsked()
    {
        var all = new ClientSettings { NestAutoUpdateOfferFor = "1.0.62" };
        var nests = new[]
        {
            Nest("a", "soro"),
            Nest("b", "Media nest", auto: true),        // already updates by itself
            Nest("c", "Anna's nest", access: NestAccess.Edit), // a friend's: only its owner can update it
            Nest("d", "Backup"),
        };
        var offer = NestAutoUpdateOffer.For(all, nests, "1.0.62");
        Assert.NotNull(offer);
        Assert.Equal(["a", "d"], offer.Nests.Select(n => n.Id));
        Assert.Equal("Let your nests update by themselves?", offer.Title);
        Assert.Contains("2 of your nests", offer.Detail, StringComparison.Ordinal);
        Assert.Equal("2 nests now update by themselves", offer.TurnedOn);

        // A nest the "Update your nest" window asked about this time is left out.
        var one = NestAutoUpdateOffer.For(all, nests, "1.0.62", skip: id => id == "d");
        Assert.NotNull(one);
        Assert.Equal("Let your nest update by itself?", one.Title);
        Assert.Contains("soro only updates when you say so", one.Detail, StringComparison.Ordinal);
        Assert.Equal("soro now updates by itself", one.TurnedOn);
    }

    [Fact]
    public void NoCardWithoutAnUpdateAfterAnAnswerOrWhenNothingToSwitch()
    {
        var nests = new[] { Nest("a", "soro") };
        Assert.Null(NestAutoUpdateOffer.For(new ClientSettings(), nests, "1.0.62"));                                  // no update
        Assert.Null(NestAutoUpdateOffer.For(new ClientSettings { NestAutoUpdateOfferFor = "1.0.61" }, nests, "1.0.62")); // an older one, answered meanwhile
        var due = new ClientSettings { NestAutoUpdateOfferFor = "1.0.62" };
        Assert.Null(NestAutoUpdateOffer.For(due, [Nest("a", "soro", auto: true)], "1.0.62"));
        Assert.Null(NestAutoUpdateOffer.For(due, [Nest("a", "soro", access: NestAccess.View)], "1.0.62"));
        Assert.Null(NestAutoUpdateOffer.For(due, [new ClientSettings { NestId = "x" }], "1.0.62")); // not signed in yet
    }

    [Fact]
    public void TheAnswersAreSavedAndKeptBySavingSettings()
    {
        var settings = new ClientSettings { LastRunVersion = "1.0.62", NestAutoUpdateOfferFor = "1.0.62" };
        var path = Path.Combine(Path.GetTempPath(), "pairnets-offer-" + Guid.NewGuid().ToString("N") + ".json");
        try
        {
            SettingsStore.Save(path, settings);
            var back = SettingsStore.Load(path);
            Assert.Equal("1.0.62", back.LastRunVersion);
            Assert.Equal("1.0.62", back.NestAutoUpdateOfferFor);
        }
        finally
        {
            File.Delete(path);
        }
        var switched = SettingsSwitches.WithSwitchesFrom(settings, new ClientSettings());
        Assert.Equal("1.0.62", switched.NestAutoUpdateOfferFor);
    }
}
