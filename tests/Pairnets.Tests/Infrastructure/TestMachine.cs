using System.Runtime.CompilerServices;

namespace Pairnets.Tests.Infrastructure;

/// <summary>Settings for the whole test run.</summary>
internal static class TestMachine
{
    /// <summary>
    /// Many tests run side by side, and some of them wait on a thread (SpinWait, GetResult). On a runner with 3 cores
    /// the thread pool then adds threads slowly (about one a second), so a server, a timer or a listener in another
    /// test can wait seconds for a thread and miss its deadline. The app itself is never this crowded.
    /// </summary>
    [ModuleInitializer]
    internal static void Start()
    {
        ThreadPool.GetMinThreads(out var workers, out var io);
        ThreadPool.SetMinThreads(Math.Max(workers, 64), Math.Max(io, 64));
    }
}
