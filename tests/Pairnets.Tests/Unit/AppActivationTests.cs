using Pairnets.Core.Client;

namespace Pairnets.Tests.Unit;

/// <summary>The way back from the browser: a second Pairnets pokes the running one through a named pipe.</summary>
public sealed class AppActivationTests
{
    // Short on purpose: on macOS the whole socket path (long temp folder + "CoreFxPipe_" + this) may not pass 104 bytes.
    private static string FreshPipe() => "pt" + Guid.NewGuid().ToString("N")[..12];

    [Fact]
    public async Task APokeReachesTheListener()
    {
        var name = FreshPipe();
        var poked = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        using var listener = AppActivation.Listen(() => poked.TrySetResult(), name);

        // Connect retries inside its timeout, so it waits for the listener's pipe to open.
        Assert.True(AppActivation.TrySignalRunning(name, timeoutMs: 10_000));

        await poked.Task.WaitAsync(TimeSpan.FromSeconds(10));
    }

    [Fact]
    public async Task EveryPokeCounts()
    {
        var name = FreshPipe();
        var pokes = 0;
        var twice = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        using var listener = AppActivation.Listen(() =>
        {
            if (Interlocked.Increment(ref pokes) == 2)
                twice.TrySetResult();
        }, name);

        Assert.True(AppActivation.TrySignalRunning(name, timeoutMs: 10_000));
        Assert.True(AppActivation.TrySignalRunning(name, timeoutMs: 10_000));

        await twice.Task.WaitAsync(TimeSpan.FromSeconds(10));
    }

    [Fact]
    public async Task PokesInQuickSuccessionAllArrive()
    {
        // On macOS and Linux a poke that lands while the pipe is being reopened must not be lost.
        var name = FreshPipe();
        var pokes = 0;
        var all = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        using var listener = AppActivation.Listen(() =>
        {
            if (Interlocked.Increment(ref pokes) == 20)
                all.TrySetResult();
        }, name);

        for (var i = 0; i < 20; i++)
            Assert.True(AppActivation.TrySignalRunning(name, timeoutMs: 10_000));

        await all.Task.WaitAsync(TimeSpan.FromSeconds(15));
    }

    [Fact]
    public void NobodyListeningMeansFalse() =>
        Assert.False(AppActivation.TrySignalRunning(FreshPipe(), timeoutMs: 250));

    [Fact]
    public void TheDefaultPipeNameStaysShortWhateverTheUserIsCalled()
    {
        // macOS allows 104 bytes for the socket path, and its temp folder alone takes about 50.
        Assert.StartsWith("pairnets-", AppActivation.DefaultPipeName);
        Assert.True(AppActivation.DefaultPipeName.Length <= 20, AppActivation.DefaultPipeName);
        Assert.Matches("^[a-z0-9-]+$", AppActivation.DefaultPipeName);
    }

    [Fact]
    public void OnlyPairnetsLinksCountAsActivation()
    {
        Assert.True(AppActivation.IsActivationUrl("pairnets://signed-in"));
        Assert.True(AppActivation.IsActivationUrl("PAIRNETS:open"));
        Assert.False(AppActivation.IsActivationUrl("--autostart"));
        Assert.False(AppActivation.IsActivationUrl(null));
        Assert.False(AppActivation.IsActivationUrl(""));
        Assert.False(AppActivation.IsActivationUrl("https://nest.example.com"));
    }
}
