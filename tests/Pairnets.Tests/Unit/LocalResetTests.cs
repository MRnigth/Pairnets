using Pairnets.Core.Client;
using Pairnets.Core.Paths;
using Pairnets.Core.Settings;
using Pairnets.Tests.Infrastructure;

namespace Pairnets.Tests.Unit;

/// <summary>"Reset this app": everything Pairnets keeps for itself goes, nothing of the person's goes.</summary>
public sealed class LocalResetTests
{
    private sealed class RecordingProtector : ISecretProtector
    {
        public List<string> Forgotten { get; } = [];

        public Exception? FailWith { get; init; }

        public string Protect(string plainText) => "saved:" + plainText;

        public string Unprotect(string protectedText) => protectedText["saved:".Length..];

        public void Forget(string protectedText)
        {
            if (FailWith is not null)
                throw FailWith;
            Forgotten.Add(protectedText);
        }
    }

    private const string StateA = "0123456789abcdef";
    private const string StateB = "fedcba9876543210";

    /// <summary>A computer that has been used: settings, two sync folders' notes, logs, and a synced folder with a marker.</summary>
    private sealed class Computer : IDisposable
    {
        private readonly TempDir _root = new("reset");

        public string SettingsPath => _root.Combine("roaming", "settings.json");

        public string LocalDir => _root.Combine("local");

        public string Synced => _root.Combine("work");

        public Computer()
        {
            Directory.CreateDirectory(_root.Combine("roaming"));
            File.WriteAllText(SettingsPath, "{}");
            File.WriteAllText(SettingsPath + ".tmp", "{}");
            File.WriteAllText(SettingsPath + ".corrupt", "{ nope");
            foreach (var state in new[] { StateA, StateB })
            {
                Directory.CreateDirectory(Path.Combine(LocalDir, state));
                File.WriteAllText(Path.Combine(LocalDir, state, "state.db"), "db");
            }
            File.WriteAllText(Path.Combine(LocalDir, StateA, "state.db-wal"), "wal");
            File.WriteAllText(Path.Combine(LocalDir, StateA, "state.db-shm"), "shm");
            Directory.CreateDirectory(Path.Combine(LocalDir, "logs"));
            File.WriteAllText(Path.Combine(LocalDir, "logs", "pairnets-20261008.log"), "log line");
            File.WriteAllText(Path.Combine(LocalDir, "app.lock"), "");
            Directory.CreateDirectory(Path.Combine(LocalDir, "cache")); // not a state folder (not a hash name): never touched
            File.WriteAllText(Path.Combine(LocalDir, "cache", "state.db"), "not ours to remove");

            Directory.CreateDirectory(Path.Combine(Synced, "sub"));
            File.WriteAllText(Path.Combine(Synced, "report.docx"), "the person's work");
            File.WriteAllText(Path.Combine(Synced, "sub", "a.txt"), "more work");
            File.WriteAllText(Path.Combine(Synced, PathRules.MarkerFileName), "marker");
            Directory.CreateDirectory(Path.Combine(Synced, PathRules.TempFolderName));
            File.WriteAllText(Path.Combine(Synced, PathRules.TempFolderName, "part.bin"), "half a download");
        }

        public ClientSettings Settings => new() { ProtectedToken = "saved:secret", Folder = Synced, DeviceName = "PC", FirstRunCompleted = true };

        public void Dispose() => _root.Dispose();
    }

    [Fact]
    public void ResetRemovesWhatPairnetsKeepsAndNeverThePersonsFiles()
    {
        using var pc = new Computer();
        var protector = new RecordingProtector();

        var result = LocalReset.Run(pc.Settings, protector, pc.SettingsPath, pc.LocalDir);

        Assert.Empty(result.Problems);
        Assert.True(result.SettingsRemoved);
        Assert.True(result.KeyForgotten);
        Assert.True(result.MarkerRemoved);
        Assert.Equal(2, result.StateFoldersRemoved);
        Assert.Equal(["saved:secret"], protector.Forgotten);

        // Pairnets' own files are gone, with the leftovers of saving and of a damaged file.
        Assert.False(File.Exists(pc.SettingsPath));
        Assert.False(File.Exists(pc.SettingsPath + ".tmp"));
        Assert.False(File.Exists(pc.SettingsPath + ".corrupt"));
        Assert.False(Directory.Exists(Path.Combine(pc.LocalDir, StateA)));
        Assert.False(Directory.Exists(Path.Combine(pc.LocalDir, StateB)));
        Assert.False(File.Exists(Path.Combine(pc.Synced, PathRules.MarkerFileName)));
        Assert.False(Directory.Exists(Path.Combine(pc.Synced, PathRules.TempFolderName)));

        // Everything else stays: the person's files, the logs, the app's own lock, and a folder that is not a state folder.
        Assert.Equal("the person's work", File.ReadAllText(Path.Combine(pc.Synced, "report.docx")));
        Assert.Equal("more work", File.ReadAllText(Path.Combine(pc.Synced, "sub", "a.txt")));
        Assert.True(File.Exists(Path.Combine(pc.LocalDir, "logs", "pairnets-20261008.log")));
        Assert.True(File.Exists(Path.Combine(pc.LocalDir, "app.lock")));
        Assert.True(File.Exists(Path.Combine(pc.LocalDir, "cache", "state.db")));
    }

    [Fact]
    public void ResetOnAComputerThatWasNeverSetUpDoesNothingAndSaysNothing()
    {
        using var dir = new TempDir("empty-reset");

        var result = LocalReset.Run(null, null, dir.Combine("no", "settings.json"), dir.Combine("no", "local"));

        Assert.Empty(result.Problems);
        Assert.True(result.SettingsRemoved); // nothing was there, so nothing is left
        Assert.False(result.KeyForgotten);
        Assert.False(result.MarkerRemoved);
        Assert.Equal(0, result.StateFoldersRemoved);
    }

    [Fact]
    public void AKeyThatCannotBeForgottenIsReportedAndTheRestStillGoes()
    {
        using var pc = new Computer();
        var protector = new RecordingProtector { FailWith = new InvalidOperationException("keyring is locked") };

        var result = LocalReset.Run(pc.Settings, protector, pc.SettingsPath, pc.LocalDir);

        Assert.False(result.KeyForgotten);
        Assert.Contains(result.Problems, p => p.Contains("secret store", StringComparison.Ordinal) && p.Contains("keyring is locked", StringComparison.Ordinal));
        Assert.False(File.Exists(pc.SettingsPath));
        Assert.Equal(2, result.StateFoldersRemoved);
    }

    [Fact]
    public void AFileThatIsInUseIsReportedInsteadOfCrashing()
    {
        if (!OperatingSystem.IsWindows())
            return; // elsewhere a file in use can be deleted anyway
        using var pc = new Computer();
        using var held = new FileStream(Path.Combine(pc.LocalDir, StateB, "state.db"), FileMode.Open, FileAccess.Read, FileShare.None);

        var result = LocalReset.Run(pc.Settings, new RecordingProtector(), pc.SettingsPath, pc.LocalDir);

        Assert.Contains(result.Problems, p => p.StartsWith("state.db", StringComparison.Ordinal));
        Assert.Equal(1, result.StateFoldersRemoved); // the other one is gone
        Assert.False(File.Exists(pc.SettingsPath));
    }

    [Fact]
    public void ASyncFolderThatIsNotAFullPathIsLeftAlone()
    {
        using var pc = new Computer();
        var settings = pc.Settings;
        settings.Folder = "work"; // relative: Reset must never guess where that is

        var result = LocalReset.Run(settings, new RecordingProtector(), pc.SettingsPath, pc.LocalDir);

        Assert.False(result.MarkerRemoved);
        Assert.True(File.Exists(Path.Combine(pc.Synced, PathRules.MarkerFileName)));
    }

    [Fact]
    public void ASecretStoreThatKnowsNothingOfForgettingStillWorks()
    {
        ISecretProtector plain = new PlainProtector();
        plain.Forget("anything"); // the default does nothing (DPAPI keeps its key inside the settings file)
        using var pc = new Computer();

        var result = LocalReset.Run(pc.Settings, plain, pc.SettingsPath, pc.LocalDir);

        Assert.True(result.KeyForgotten);
        Assert.False(File.Exists(pc.SettingsPath));
    }

    private sealed class PlainProtector : ISecretProtector
    {
        public string Protect(string plainText) => plainText;

        public string Unprotect(string protectedText) => protectedText;
    }
}
