using Pairnets.Client.Platform;
using Pairnets.Core;
using Pairnets.Core.Settings;
using Pairnets.Tests.Infrastructure;

namespace Pairnets.Client.Tests;

/// <summary>
/// The saved sign-in key (real Windows DPAPI) is tied to the Windows user only: not to where Pairnets.exe is, its
/// version or its name, so an update or a moved copy can always read it.
/// </summary>
public sealed class SavedKeyTests
{
    [Fact]
    public void TheEntropyNeverChanges_OrEveryComputerWouldBeSignedOutByTheNextUpdate()
    {
        Assert.Equal("Pairnets.Client.Token.v1", PairnetsInfo.DpapiEntropy);
    }

    [Fact]
    public void AKeySavedByOneCopy_IsReadByAnother_ThroughTheSettingsFile()
    {
        using var dir = new TempDir("dpapi");
        var path = dir.Combine("settings.json");
        SettingsStore.Save(path, new ClientSettings { ProtectedToken = new DpapiProtector().Protect("the-device-key"), FirstRunCompleted = true });

        var loaded = SettingsStore.Load(path);

        Assert.Equal("the-device-key", new DpapiProtector().Unprotect(loaded.ProtectedToken!));
    }
}
