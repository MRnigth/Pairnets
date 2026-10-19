using Pairnets.Core;
using Pairnets.Core.Client;
using Pairnets.Core.Settings;

namespace Pairnets.Tests.Unit;

/// <summary>The name a new computer gets (its own one, cleaned), and the one-time offer to use it instead of "PC-1".</summary>
public class ComputerNameTests
{
    [Theory]
    [InlineData("Family MacBook Pro", "family-macbook-pro.local", "FAMILY-MACBOOK", "macOS", "Family MacBook Pro")]
    [InlineData(null, "StudyPC", "STUDYPC", "Windows", "StudyPC")]
    [InlineData("  ", "kitchen-laptop.home.lan", "kitchen-laptop", "Linux", "kitchen-laptop")]
    [InlineData(null, null, "DESKTOP-7Q2", "Windows", "DESKTOP-7Q2")]
    [InlineData(null, "", "", "Windows", "Windows PC")]
    [InlineData("​", null, null, "macOS", "Mac")]
    [InlineData(null, null, null, "Linux", "Linux computer")]
    [InlineData(null, "10.0.0.7", null, "Linux", "10.0.0.7")]
    public void TheNameIsTheOneItsOwnerKnows(string? pretty, string? host, string? machine, string system, string expected) =>
        Assert.Equal(expected, ComputerName.Choose(pretty, host, machine, system));

    [Fact]
    public void ANameIsCleanedAndKeptTo64Characters()
    {
        Assert.Equal("Desk", ComputerName.Clean("  De‮sk\u0007 "));
        Assert.Equal(string.Empty, ComputerName.Clean("​‏"));
        Assert.Equal(new string('a', 64), ComputerName.Clean(new string('a', 70)));
        // Never in the middle of an emoji, and never longer than the service takes (64 code points).
        var emoji = string.Concat(Enumerable.Repeat("\U0001F600", 40));
        var clean = ComputerName.Clean(emoji);
        Assert.Equal(32, clean.EnumerateRunes().Count());
        Assert.True(clean.EnumerateRunes().Count() <= ComputerName.MaxLength);
        // Accented letters made of two code points count as two, as the service counts them.
        var accents = string.Concat(Enumerable.Repeat("é", 40));
        Assert.True(ComputerName.Clean(accents).EnumerateRunes().Count() <= ComputerName.MaxLength);
    }

    [Fact]
    public void ThePrettyHostNameIsReadFromMachineInfo()
    {
        Assert.Equal("Home server", ComputerName.PrettyHostName(["CHASSIS=server", "PRETTY_HOSTNAME=\"Home server\""]));
        Assert.Equal("Say \"hi\"", ComputerName.PrettyHostName(["PRETTY_HOSTNAME='Say \\\"hi\\\"'"]));
        Assert.Equal("plain", ComputerName.PrettyHostName(["  PRETTY_HOSTNAME=plain  "]));
        Assert.Null(ComputerName.PrettyHostName(["# PRETTY_HOSTNAME=no", "DEPLOYMENT=production"]));
    }

    [Theory]
    [InlineData("PC-1", "StudyPC", true)]
    [InlineData("pc 2", "StudyPC", true)]
    [InlineData("Computer3", "StudyPC", true)]
    [InlineData("STUDYPC", "StudyPC", true)]
    [InlineData("StudyPC", "StudyPC", false)]
    [InlineData("PC-1", "PC-1", false)]
    [InlineData("Work laptop", "StudyPC", false)]
    [InlineData("DESK", "StudyPC", false)]
    [InlineData("StudyPC (2)", "StudyPC", false)]
    [InlineData("", "StudyPC", false)]
    [InlineData(null, "StudyPC", false)]
    [InlineData("1234", "1234", false)]
    public void OnlyAPlaceholderOrTheOldCapitalsNameIsWorthOffering(string? current, string own, bool expected) =>
        Assert.Equal(expected, ComputerName.WorthOffering(current, own));

    [Fact]
    public void TheOfferIsMadeOnceToAComputerWithItsOwnKey()
    {
        var settings = new ClientSettings { DeviceId = "d1", DeviceName = "PC-1" };
        var offer = NameOffer.For(settings, "StudyPC");
        Assert.NotNull(offer);
        Assert.Equal("Use this computer's own name?", offer.Title);
        Assert.Equal("Use StudyPC", offer.UseLabel);
        Assert.Equal("Keep PC-1", offer.KeepLabel);
        Assert.Contains("called PC-1", offer.Detail, StringComparison.Ordinal);
        Assert.Contains("own name is StudyPC", offer.Detail, StringComparison.Ordinal);

        // Answered either way: never again.
        settings.NameOfferAnswered = true;
        Assert.Null(NameOffer.For(settings, "StudyPC"));
        // The shared token has no name on the nest to change.
        Assert.Null(NameOffer.For(new ClientSettings { DeviceName = "PC-1" }, "StudyPC"));
        Assert.Null(NameOffer.For(new ClientSettings { DeviceId = "d1", DeviceName = "Work laptop" }, "StudyPC"));
    }

    [Fact]
    public void ANewNameThatDidNotReachTheNestIsMentioned()
    {
        var original = new ClientSettings { DeviceId = "d1", DeviceName = "PC-1" };
        Assert.Contains("still called PC-1", OwnKey.NameNotSaved(original, "d1", "StudyPC", "PC-1"), StringComparison.Ordinal);
        Assert.Null(OwnKey.NameNotSaved(original, "d1", "StudyPC", "StudyPC"));
        Assert.Null(OwnKey.NameNotSaved(original, "d1", "Desk", "Desk (2)"));
        Assert.Null(OwnKey.NameNotSaved(original, "d1", "PC-1", "PC-1"));
        Assert.Null(OwnKey.NameNotSaved(original, null, "Other", "Other"));
        Assert.Null(OwnKey.NameNotSaved(new ClientSettings { DeviceName = "PC-1" }, null, "StudyPC", "PC-1"));
    }

    [Fact]
    public void TheAppsCallTheNestByTheNameItsOwnerGaveIt()
    {
        var named = StatusSnapshot.Initial with { Server = new ServerInfo("s1", 1, 1, NestName: "soro") };
        Assert.Equal("soro", named.NestTitle);
        Assert.Equal("Your nest", StatusSnapshot.Initial.NestTitle);
        Assert.Equal("Your nest", (StatusSnapshot.Initial with { Server = new ServerInfo("s1", 1, 1, NestName: " ​ ") }).NestTitle);
        Assert.Equal("soro", NestCard.From(named, null).Title);
        Assert.Equal("Your nest", NestCard.From(StatusSnapshot.Initial, null).Title);
        Assert.Equal("soro", NestMapModel.Build(named, "DESK", DateTimeOffset.Now).NestTitle);
    }

    [Fact]
    public void ThisComputersOwnNameIsUsable()
    {
        var own = ComputerName.Default();
        Assert.False(string.IsNullOrWhiteSpace(own));
        Assert.Equal(own, ComputerName.Clean(own));
        Assert.DoesNotContain(".local", own, StringComparison.OrdinalIgnoreCase);
    }
}
