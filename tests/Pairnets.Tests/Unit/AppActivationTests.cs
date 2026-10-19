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
    public void APokeIsAnsweredOnceTheWindowIsUp()
    {
        var name = FreshPipe();
        var shown = false;
        using var listener = AppActivation.Listen(async () =>
        {
            await Task.Delay(300); // the window takes a moment to come forward
            shown = true;
        }, name);

        Assert.Equal(AppActivation.PokeResult.Shown, AppActivation.Poke(name));
        Assert.True(shown);
    }

    [Fact]
    public void AStuckWindowMeansNotAnswering()
    {
        var name = FreshPipe();
        var stuck = new TaskCompletionSource();
        using var listener = AppActivation.Listen(() => stuck.Task, name);

        var timer = System.Diagnostics.Stopwatch.StartNew();
        Assert.Equal(AppActivation.PokeResult.NotAnswering, AppActivation.Poke(name, answerTimeoutMs: 10_000));
        // The running app gives up on its own window after AnswerWithin, so the second start is not left waiting.
        Assert.True(timer.Elapsed < TimeSpan.FromSeconds(8), timer.Elapsed.ToString());
        stuck.SetResult();
    }

    [Fact]
    public void AFailingWindowMeansNotAnswering()
    {
        var name = FreshPipe();
        using var listener = AppActivation.Listen(() => throw new InvalidOperationException("the window broke"), name);

        Assert.Equal(AppActivation.PokeResult.NotAnswering, AppActivation.Poke(name));
    }

    [Fact]
    public void NobodyListeningMeansNotRunning()
    {
        Assert.Equal(AppActivation.PokeResult.NotRunning, AppActivation.Poke(FreshPipe(), connectTimeoutMs: 250));
        // Pairnets holds its single-instance lock but never opens its pipe: the second start says so.
        Assert.Equal(AppActivation.SecondStartResult.NotAnswering, AppActivation.SecondStart([], FreshPipe(), connectTimeoutMs: 250));
    }

    [Fact]
    public async Task APokeWaitsForAPairnetsThatIsStillStarting()
    {
        var name = FreshPipe();
        var poke = Task.Run(() => AppActivation.Poke(name, connectTimeoutMs: 10_000));
        await Task.Delay(1000); // the running Pairnets has not opened its pipe yet
        using var listener = AppActivation.Listen(() => { }, name);

        Assert.Equal(AppActivation.PokeResult.Shown, await poke.WaitAsync(TimeSpan.FromSeconds(15)));
    }

    [Fact]
    public async Task OpeningAgainBringsItForward_ButTheStartAtLoginNeverDoes()
    {
        var name = FreshPipe();
        var pokes = 0;
        using var listener = AppActivation.Listen(() => Interlocked.Increment(ref pokes), name);

        Assert.Equal(AppActivation.SecondStartResult.Quiet, AppActivation.SecondStart([AppActivation.AutoStartArgument], name));
        await Task.Delay(500);
        Assert.Equal(0, Volatile.Read(ref pokes));

        Assert.Equal(AppActivation.SecondStartResult.Quiet, AppActivation.SecondStart([], name));
        Assert.Equal(AppActivation.SecondStartResult.Quiet, AppActivation.SecondStart(["pairnets://signed-in"], name));
        Assert.Equal(2, Volatile.Read(ref pokes));
    }

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
