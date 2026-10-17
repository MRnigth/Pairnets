using System.Text;
using System.Text.RegularExpressions;
using Pairnets.Core.Client;
using Pairnets.Tests.Infrastructure;

namespace Pairnets.Tests.Unit;

/// <summary>The release signing key: one key everywhere, and the apps read what openssl (the release workflow) writes.</summary>
public class ReleaseSigningTests
{
    /// <summary>
    /// "Pairnets release signing check\n" signed with the real Pairnets release key by
    /// <c>openssl dgst -sha256 -sign</c>, as scripts/sign-release.sh does (a public sample, not a secret).
    /// </summary>
    private const string SampleSignature = "MEUCIGQGfArhvHIF14fBi3E7QTo8ejQ4Qx4tCuSEvT5JkcV1AiEAtcn5NY/Zab/AnJ24xAeQEPzWO/80GPgbOtzlMWs0GKQ=";

    private static readonly byte[] Sample = Encoding.UTF8.GetBytes("Pairnets release signing check\n");

    [Fact]
    public void TheAppsAcceptWhatOpensslSignedWithThePairnetsKey()
    {
        Assert.True(ReleaseSigning.Verify(Sample, Convert.FromBase64String(SampleSignature), ReleaseSigning.PublicKeyPem));
        Assert.False(ReleaseSigning.Verify([.. Sample, (byte)'!'], Convert.FromBase64String(SampleSignature), ReleaseSigning.PublicKeyPem));
    }

    [Theory]
    [InlineData("deploy/update.sh")]
    [InlineData("deploy/get.sh")]
    public void TheScriptsCarryTheSameKey(string script)
    {
        var text = File.ReadAllText(RepoPaths.Of(script.Split('/')));
        var embedded = Regex.Match(text, "RELEASE_KEY='(-----BEGIN PUBLIC KEY-----.*?-----END PUBLIC KEY-----)'", RegexOptions.Singleline);
        Assert.True(embedded.Success, $"{script} has no RELEASE_KEY");
        Assert.Equal(ReleaseSigning.PublicKeyPem.Trim().ReplaceLineEndings("\n"), embedded.Groups[1].Value.ReplaceLineEndings("\n"));
    }

    [Fact]
    public void BothReleaseJobsSignBeforePublishing()
    {
        var workflow = File.ReadAllText(RepoPaths.Of(".github", "workflows", "release.yml")).ReplaceLineEndings("\n");
        Assert.Equal(2, Regex.Matches(workflow, @"run: bash scripts/sign-release\.sh release-files").Count);
        Assert.Equal(2, Regex.Matches(workflow, @"RELEASE_SIGNING_KEY: \$\{\{ secrets\.RELEASE_SIGNING_KEY \}\}").Count);
        foreach (var publish in new[] { "- name: Publish release", "- name: Replace the \"latest\" release" })
            Assert.True(workflow.LastIndexOf("sign-release.sh", workflow.IndexOf(publish, StringComparison.Ordinal), StringComparison.Ordinal) > 0,
                $"no signing before \"{publish}\"");
    }
}
