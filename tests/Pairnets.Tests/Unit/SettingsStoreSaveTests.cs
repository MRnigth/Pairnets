using Pairnets.Core.Settings;
using Pairnets.Tests.Infrastructure;

namespace Pairnets.Tests.Unit;

/// <summary>Two saves of the settings at once (a button, a background notice) must not collide or lose the newer settings.</summary>
public class SettingsStoreSaveTests
{
    [Fact]
    public async Task SavesFromManyThreadsNeverFailAndTheFileIsAlwaysWhole()
    {
        using var dir = new TempDir("settings-save");
        var path = dir.Combine("roaming", "settings.json");
        SettingsStore.Save(path, new ClientSettings { DeviceName = "start" });
        var failures = new List<Exception>();
        var stop = false;

        var reader = Task.Run(() =>
        {
            while (!Volatile.Read(ref stop))
            {
                Thread.Sleep(10); // a window reading its settings now and then, not a spin
                try
                {
                    Assert.NotNull(SettingsStore.Load(path).DeviceName); // never half a file
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
                {
                    // A read that lands in the middle of a replace may be refused for a moment; a half-written file would not parse.
                }
                catch (Exception ex)
                {
                    lock (failures)
                        failures.Add(ex);
                }
            }
        });
        var writers = Enumerable.Range(0, 8).Select(t => Task.Run(() =>
        {
            for (var i = 0; i < 60; i++)
            {
                try
                {
                    SettingsStore.Save(path, new ClientSettings { DeviceName = $"thread {t} save {i}" });
                }
                catch (Exception ex)
                {
                    lock (failures)
                        failures.Add(ex);
                }
            }
        })).ToArray();
        await Task.WhenAll(writers);
        Volatile.Write(ref stop, true);
        await reader;

        Assert.Empty(failures);
        SettingsStore.Save(path, new ClientSettings { DeviceName = "the newest" });
        Assert.Equal("the newest", SettingsStore.Load(path).DeviceName); // the last save is what stays
        Assert.False(File.Exists(path + ".tmp"));
    }

    [Fact]
    public async Task ASaveWaitsOutAFileHeldForAMoment()
    {
        using var dir = new TempDir("settings-save");
        var path = dir.Combine("settings.json");
        SettingsStore.Save(path, new ClientSettings { DeviceName = "before" });

        // Something else (a scanner, a backup) holds the settings file open for a moment; on Windows that blocks the move.
        using var held = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.None);
        var release = Task.Run(async () =>
        {
            await Task.Delay(250);
            held.Dispose();
        });
        SettingsStore.Save(path, new ClientSettings { DeviceName = "after" });
        await release;

        Assert.Equal("after", SettingsStore.Load(path).DeviceName);
    }

    [Fact]
    public void AFileHeldForgoodStillFailsInsteadOfWaitingForEver()
    {
        if (!OperatingSystem.IsWindows())
            return; // only Windows refuses to replace an open file
        using var dir = new TempDir("settings-save");
        var path = dir.Combine("settings.json");
        SettingsStore.Save(path, new ClientSettings { DeviceName = "before" });
        using var held = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.None);

        var started = DateTime.UtcNow;
        Assert.ThrowsAny<Exception>(() => SettingsStore.Save(path, new ClientSettings { DeviceName = "after" }));
        Assert.True(DateTime.UtcNow - started < TimeSpan.FromSeconds(5));
    }
}
