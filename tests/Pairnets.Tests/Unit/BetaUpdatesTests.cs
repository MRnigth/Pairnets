using System.Net;
using System.Security.Cryptography;
using System.Text;
using Pairnets.Core;
using Pairnets.Core.Client;
using Pairnets.Core.Settings;
using Pairnets.Tests.Infrastructure;

namespace Pairnets.Tests.Unit;

/// <summary>
/// Beta updates (docs/DECISIONS.md, "Beta updates"): how builds are ordered, which feed's build a computer may take, the
/// data check that keeps "Off" from ever lowering what an older build could read, and how a choice made in the app and one
/// made on the account page meet on the owner's nest.
/// </summary>
public class BetaUpdatesTests
{
    private static ReleaseVersion R(string number, string channel = ReleaseChannel.Stable, int build = 0) =>
        new(ReleaseVersion.TryParseNumber(number, out var n) ? n : throw new ArgumentException(number), channel, build);

    [Fact]
    public void Builds_AreOrderedByNumber_ThenStableBeforeABetaOfTheSameNumber_ThenBuild()
    {
        Assert.True(R("1.0.61.8") > R("1.0.61.8", ReleaseChannel.DeveloperBeta, 900));
        Assert.True(R("1.0.61.8", ReleaseChannel.PublicBeta, 4) > R("1.0.61.8", ReleaseChannel.PublicBeta, 3));
        Assert.True(R("1.0.61.9", ReleaseChannel.PublicBeta, 1) > R("1.0.61.8"));
        Assert.True(R("1.0.61.8", ReleaseChannel.DeveloperBeta, 12) > R("1.0.61.7"));
        // A re-run of a tag's workflow gets a higher run number: still the same release, never offered again.
        Assert.Equal(0, R("1.0.61.8", build: 120).CompareTo(R("1.0.61.8", build: 7)));
    }

    [Fact]
    public void OneSpellingOfTheVersion_Everywhere()
    {
        Assert.Equal("1.0.61.8", R("1.0.61.8", build: 5).Display);
        Assert.Equal("1.0.61.8 Public beta 3", R("1.0.61.8", ReleaseChannel.PublicBeta, 3).Display);
        Assert.Equal("1.0.61.8 Developer beta 12", R("1.0.61.8", ReleaseChannel.DeveloperBeta, 12).Display);
    }

    [Theory]
    [InlineData("1.0.61.8", "1.0.61.7")]
    [InlineData("1.0.61.1", "1.0.61")]
    [InlineData("1.0.62", "1.0.61")]
    public void ABetaFollowsTheStableBeforeIt(string beta, string stable) =>
        Assert.Equal(stable, PairnetsInfo.FormatVersion(PairnetsInfo.PreviousNumber(R(beta).Number)));

    [Fact]
    public void AFeed_IsOnlyTrustedForItsOwnChannel_AndMainsLatestBuildIsNeverTaken()
    {
        var running = R("1.0.61.7");
        var here = DataVersions.Current;
        Assert.Equal(FeedVerdict.Newer, UpdateChecker.Judge("1.0.61.8", "stable", 40, here, ReleaseChannel.Stable, running, here));
        Assert.Equal(FeedVerdict.WrongChannel, UpdateChecker.Judge("1.0.61.8", "public-beta", 40, here, ReleaseChannel.Stable, running, here));
        Assert.Equal(FeedVerdict.WrongChannel, UpdateChecker.Judge("1.0.61.8", "stable", 40, here, ReleaseChannel.PublicBeta, running, here));
        Assert.Equal(FeedVerdict.WrongChannel, UpdateChecker.Judge("1.0.95", "main", 95, here, ReleaseChannel.Stable, running, here));
        // Main's "Latest build" from before channels said nothing: numbered past 1.0.61 it is not a release.
        Assert.Equal(FeedVerdict.WrongChannel, UpdateChecker.Judge("1.0.85", null, null, null, ReleaseChannel.Stable, running, here));
        // The releases from before channels said nothing either and still count.
        Assert.Equal(FeedVerdict.Newer, UpdateChecker.Judge("1.0.61.8", null, null, null, ReleaseChannel.Stable, running, here));
        Assert.Equal(FeedVerdict.NotNewer, UpdateChecker.Judge("1.0.61.7", "stable", 50, here, ReleaseChannel.Stable, running, here));
    }

    [Fact]
    public void ABuild_ThatCouldNotReadThisComputersData_IsNeverInstalled()
    {
        var running = R("1.0.61.8", ReleaseChannel.PublicBeta, 3);
        var wrote = DataVersions.Current with { State = DataVersions.Current.State + 1 }; // the beta moved the state database on
        Assert.Equal(FeedVerdict.LowersData,
            UpdateChecker.Judge("1.0.61.8", "stable", 60, DataVersions.Current, ReleaseChannel.Stable, running, wrote));
        Assert.Equal(FeedVerdict.Newer,
            UpdateChecker.Judge("1.0.61.9", "stable", 61, wrote, ReleaseChannel.Stable, running, wrote));
        // A release from before "data" was written reads what 1.0.61.7 and older read.
        Assert.Equal(FeedVerdict.LowersData, UpdateChecker.Judge("1.0.61.9", "stable", null, null, ReleaseChannel.Stable, running, wrote));
    }

    [Fact]
    public async Task ABetaComputer_ReadsBothFeeds_AndTakesTheNewest()
    {
        var feeds = new FakeFeeds();
        feeds.Put("latest", "1.0.61.8", "stable", 70);
        feeds.Put(ReleaseChannel.PublicBeta, "1.0.61.8", ReleaseChannel.PublicBeta, 66);
        var checker = feeds.Checker();
        var running = R("1.0.61.8", ReleaseChannel.PublicBeta, 3);

        var found = (await checker.CheckAsync(running, ReleaseChannel.PublicBeta, "PairnetsSetup.exe", DataVersions.Current, CancellationToken.None)).Found;
        Assert.Equal(("1.0.61.8", ReleaseChannel.Stable), (found!.Version, found.Channel)); // the stable release beats every beta of its number

        feeds.Put(ReleaseChannel.PublicBeta, "1.0.61.9", ReleaseChannel.PublicBeta, 71);
        found = (await checker.CheckAsync(running, ReleaseChannel.PublicBeta, "PairnetsSetup.exe", DataVersions.Current, CancellationToken.None)).Found;
        Assert.Equal("1.0.61.9 Public beta 71", found!.Display);
        Assert.True(found.Signed);
        Assert.StartsWith("https://example.invalid/releases/download/public-beta/", found.AssetUrl.ToString(), StringComparison.Ordinal);

        // A computer on Off never asks a beta feed.
        feeds.Asked.Clear();
        Assert.Null((await checker.CheckAsync(R("1.0.61.8"), ReleaseChannel.Stable, "PairnetsSetup.exe", DataVersions.Current, CancellationToken.None)).Found);
        Assert.DoesNotContain(feeds.Asked, u => u.Contains("public-beta", StringComparison.Ordinal));
    }

    [Fact]
    public async Task ABetaFeedServingAnotherChannelsBuild_OffersNothing()
    {
        var feeds = new FakeFeeds();
        feeds.Put("latest", "1.0.61.7", "stable", 1);
        feeds.Put(ReleaseChannel.DeveloperBeta, "1.0.61.9", ReleaseChannel.PublicBeta, 80); // signed, but for Public beta
        var result = await feeds.Checker().CheckAsync(R("1.0.61.8", ReleaseChannel.DeveloperBeta, 2), ReleaseChannel.DeveloperBeta, "PairnetsSetup.exe",
            DataVersions.Current, CancellationToken.None);
        Assert.Null(result.Found);
    }

    [Fact]
    public async Task MovingDown_WaitsForTheNextBuildOfTheLowerChannel_AndSaysSo()
    {
        var feeds = new FakeFeeds();
        feeds.Put("latest", "1.0.61.7", "stable", 1);
        feeds.Put(ReleaseChannel.PublicBeta, "1.0.61.8", ReleaseChannel.PublicBeta, 5);
        using var updates = new UpdateService(feeds.Checker(), R("1.0.61.8", ReleaseChannel.DeveloperBeta, 12), "PairnetsSetup.exe");

        updates.SetChannel(ReleaseChannel.PublicBeta);
        await updates.CheckNowAsync(CancellationToken.None);
        Assert.Null(updates.Available); // Public beta 5 is older than Developer beta 12 of the same number
        Assert.Equal("Waiting for the next Public beta. You keep 1.0.61.8 Developer beta 12 until then.", updates.Waiting);

        updates.SetChannel(ReleaseChannel.Stable);
        await updates.CheckNowAsync(CancellationToken.None);
        Assert.StartsWith("Beta updates are off. You keep 1.0.61.8 Developer beta 12 until the next normal version is newer", updates.Waiting, StringComparison.Ordinal);

        feeds.Put("latest", "1.0.61.8", "stable", 90);
        await updates.CheckNowAsync(CancellationToken.None);
        Assert.Equal("1.0.61.8", updates.Available!.Display);
        Assert.Null(updates.Waiting);
    }

    // ------------------------------------------------------------------ the choice: app, account page and the owner's nest

    private static readonly DateTimeOffset T0 = new(2026, 10, 26, 10, 0, 0, TimeSpan.Zero);

    private static BetaInfo Joined(BetaChoice? choice = null, params string[] channels) =>
        new(true, "2026-10-26", channels.Length == 0 ? [ReleaseChannel.PublicBeta] : channels, choice);

    [Fact]
    public void NoWordFromTheNest_KeepsTheLastChoice()
    {
        var s = new ClientSettings { BetaChannel = ReleaseChannel.PublicBeta, BetaChannelAt = T0, BetaProgram = Joined() };
        Assert.Equal((false, false), BetaUpdates.Heard(s, null));
        Assert.Equal(ReleaseChannel.PublicBeta, s.EffectiveChannel);
    }

    [Fact]
    public void TheNewestChoiceWins_WhereverItWasMade()
    {
        var s = new ClientSettings();
        BetaUpdates.Choose(s, ReleaseChannel.PublicBeta, T0);
        Assert.Equal((true, true), BetaUpdates.Heard(s, Joined())); // the nest has none yet: send ours
        Assert.Equal(ReleaseChannel.PublicBeta, s.EffectiveChannel);

        Assert.Equal((true, false), BetaUpdates.Heard(s, Joined(new BetaChoice(ReleaseChannel.Off, T0.AddMinutes(5), "web"))));
        Assert.Null(s.BetaChannel);
        Assert.Equal(ReleaseChannel.Stable, s.EffectiveChannel);

        BetaUpdates.Choose(s, ReleaseChannel.PublicBeta, T0.AddMinutes(9));
        Assert.Equal((false, true), BetaUpdates.Heard(s, Joined(new BetaChoice(ReleaseChannel.Off, T0.AddMinutes(5), "web"))));
        Assert.Equal(new BetaChoiceRequest(ReleaseChannel.PublicBeta, T0.AddMinutes(9)), BetaUpdates.Request(s));
    }

    [Fact]
    public void ABetaOnlyCounts_WhileTheAccountMayUseIt()
    {
        var s = new ClientSettings { BetaChannel = ReleaseChannel.DeveloperBeta, BetaChannelAt = T0 };
        BetaUpdates.Heard(s, Joined());
        Assert.Equal(ReleaseChannel.Stable, s.EffectiveChannel); // not signed up for Developer beta
        Assert.False(BetaUpdates.CanChoose(s, ReleaseChannel.DeveloperBeta));
        Assert.True(BetaUpdates.CanChoose(s, ReleaseChannel.PublicBeta));

        BetaUpdates.Heard(s, Joined(null, ReleaseChannel.PublicBeta, ReleaseChannel.DeveloperBeta));
        Assert.Equal(ReleaseChannel.DeveloperBeta, s.EffectiveChannel);

        BetaUpdates.Heard(s, new BetaInfo(false, null, []));
        Assert.Equal(ReleaseChannel.Stable, s.EffectiveChannel); // left the beta program
        Assert.True(BetaTexts.NeedsJoin(s));
    }

    [Fact]
    public void AFriendsNest_NeverDecides()
    {
        var s = new ClientSettings { BetaChannel = ReleaseChannel.PublicBeta, BetaChannelAt = T0 };
        BetaUpdates.Heard(s, Joined());
        Assert.Equal((false, false), BetaUpdates.Heard(s, new BetaInfo(false, Reason: BetaInfo.ReasonFriend)));
        Assert.Equal(ReleaseChannel.PublicBeta, s.EffectiveChannel);

        // A computer that only uses friends' nests is told why it cannot choose.
        var guest = new ClientSettings();
        Assert.True(BetaUpdates.Heard(guest, new BetaInfo(false, Reason: BetaInfo.ReasonFriend)).Changed);
        Assert.False(BetaUpdates.CanChoose(guest, ReleaseChannel.PublicBeta));
        Assert.StartsWith("Beta updates are chosen through your own nest", BetaTexts.Explain(guest), StringComparison.Ordinal);
    }

    [Fact]
    public void SettingsANewerPairnetsWrote_SurviveAnOlderOne()
    {
        var dir = Directory.CreateTempSubdirectory("pairnets-beta-");
        try
        {
            var path = Path.Combine(dir.FullName, "settings.json");
            File.WriteAllText(path, """{"deviceName":"StudyPC","fromTheFuture":{"a":1},"otherNests":[{"id":"n1","label":"Cabin","alsoNew":true}]}""");
            var loaded = SettingsStore.Load(path);
            SettingsStore.Save(path, loaded.Clone());
            var text = File.ReadAllText(path);
            Assert.Contains("\"fromTheFuture\"", text, StringComparison.Ordinal);
            Assert.Contains("\"alsoNew\"", text, StringComparison.Ordinal);
        }
        finally
        {
            dir.Delete(recursive: true);
        }
    }

    /// <summary>GitHub with a stable feed (releases/latest/download) and the rolling beta pre-releases, each signed by the test key.</summary>
    private sealed class FakeFeeds : HttpMessageHandler
    {
        private readonly Dictionary<string, Dictionary<string, byte[]>> _feeds = new(StringComparer.Ordinal);

        public List<string> Asked { get; } = [];

        public UpdateChecker Checker() => new(new HttpClient(this), new Uri("https://example.invalid/releases/latest/download/"),
            signingKeyPem: TestReleaseKey.PublicKeyPem,
            betaFeed: c => (new Uri($"https://example.invalid/releases/download/{c}/"), new Uri($"https://example.invalid/releases/tag/{c}")));

        /// <param name="feed">"latest", or a beta's tag.</param>
        public void Put(string feed, string version, string channel, int build)
        {
            var files = new Dictionary<string, byte[]>(StringComparer.Ordinal)
            {
                ["PairnetsSetup.exe"] = Encoding.UTF8.GetBytes($"setup {version} {channel} {build}"),
                ["version.json"] = Encoding.UTF8.GetBytes(
                    $"{{\"version\":\"{version}\",\"commit\":\"{new string('b', 40)}\",\"channel\":\"{channel}\",\"build\":{build},\"data\":{{\"settings\":2,\"state\":2,\"sealed\":1}}}}\n"),
            };
            var sums = new StringBuilder();
            foreach (var name in files.Keys.Order(StringComparer.Ordinal))
                sums.Append(Convert.ToHexString(SHA256.HashData(files[name])).ToLowerInvariant()).Append("  ").Append(name).Append('\n');
            files["SHA256SUMS.txt"] = Encoding.UTF8.GetBytes(sums.ToString());
            files["SHA256SUMS.txt.sig"] = TestReleaseKey.Sign(files["SHA256SUMS.txt"]);
            lock (_feeds)
                _feeds[feed] = files;
        }

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            var url = request.RequestUri!.AbsolutePath;
            lock (_feeds)
            {
                Asked.Add(url);
                var feed = url.StartsWith("/releases/latest/download/", StringComparison.Ordinal) ? "latest" : url.Split('/')[3];
                return Task.FromResult(_feeds.TryGetValue(feed, out var files) && files.TryGetValue(url.Split('/')[^1], out var body)
                    ? new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent(body) }
                    : new HttpResponseMessage(HttpStatusCode.NotFound));
            }
        }
    }
}
