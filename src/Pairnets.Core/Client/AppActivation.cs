using System.IO.Pipes;
using System.Runtime.InteropServices;

namespace Pairnets.Core.Client;

/// <summary>
/// The way back from the browser: approving this computer on the nest's website opens a pairnets://
/// link, the operating system starts a second Pairnets for it, and that second one pokes the one
/// already running through a named pipe and quits. The link itself carries nothing — the app gets its
/// key through its own secret poll (<see cref="PairingFlow"/>) — so a poke only means "come to the front".
/// </summary>
public static class AppActivation
{
    /// <summary>True for a "pairnets://…" program argument.</summary>
    public static bool IsActivationUrl(string? arg) =>
        arg is not null && arg.StartsWith("pairnets:", StringComparison.OrdinalIgnoreCase);

    /// <summary>One pipe per user, so two people on one machine do not poke each other's app.</summary>
    public static string DefaultPipeName { get; } =
        "pairnets-activate-" + string.Concat(Environment.UserName.Where(char.IsLetterOrDigit));

    /// <summary>
    /// Starts answering pokes; <paramref name="activated"/> fires on a background thread for each one.
    /// Disposing the returned handle stops listening.
    /// </summary>
    public static IDisposable Listen(Action activated, string? pipeName = null)
    {
        var stop = new CancellationTokenSource();
        _ = Task.Run(() => ListenLoopAsync(activated, pipeName ?? DefaultPipeName, stop.Token));
        return new Stopper(stop);
    }

    private static async Task ListenLoopAsync(Action activated, string name, CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            NamedPipeServerStream server;
            try
            {
                server = new NamedPipeServerStream(name, PipeDirection.In, maxNumberOfServerInstances: 1,
                    PipeTransmissionMode.Byte, PipeOptions.Asynchronous);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                // The name is taken: wait a moment and try again.
                try
                {
                    await Task.Delay(TimeSpan.FromSeconds(1), ct).ConfigureAwait(false);
                }
                catch (OperationCanceledException)
                {
                    break;
                }
                continue;
            }
            var connected = false;
            await using (server.ConfigureAwait(false))
            {
                try
                {
                    await server.WaitForConnectionAsync(ct).ConfigureAwait(false);
                    connected = true;
                }
                catch (OperationCanceledException)
                {
                    break;
                }
                catch (Exception ex) when (ex is IOException or ObjectDisposedException)
                {
                    // A poker can connect and hang up before the wait even starts; Windows then
                    // fails the wait instead of completing it. Someone did knock, so fall through.
                }
                activated();
            }
            if (!connected)
            {
                // A breather, in case the wait keeps failing for some other reason.
                try
                {
                    await Task.Delay(TimeSpan.FromMilliseconds(100), ct).ConfigureAwait(false);
                }
                catch (OperationCanceledException)
                {
                    break;
                }
            }
        }
    }

    /// <summary>Pokes the running Pairnets. False when none is listening.</summary>
    public static bool TrySignalRunning(string? pipeName = null, int timeoutMs = 2000)
    {
        AllowTakingTheForeground();
        try
        {
            using var client = new NamedPipeClientStream(".", pipeName ?? DefaultPipeName, PipeDirection.Out);
            client.Connect(timeoutMs);
            return true;
        }
        catch (Exception ex) when (ex is TimeoutException or IOException or UnauthorizedAccessException)
        {
            return false;
        }
    }

    /// <summary>
    /// Windows only lets the program the user just started bring a window to the front. That program is
    /// us (the browser opened pairnets://), so pass the right on before poking the running app.
    /// </summary>
    private static void AllowTakingTheForeground()
    {
        if (!OperatingSystem.IsWindows())
            return;
        try
        {
            _ = AllowSetForegroundWindow(-1); // -1: any process may come forward next
        }
        catch (Exception ex) when (ex is EntryPointNotFoundException or DllNotFoundException)
        {
            // Without the right the running app still flashes in the taskbar.
        }
    }

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool AllowSetForegroundWindow(int processId);

    /// <summary>Cancels the listen loop; the source is never disposed so a poke mid-stop cannot trip on it.</summary>
    private sealed class Stopper(CancellationTokenSource stop) : IDisposable
    {
        public void Dispose() => stop.Cancel();
    }
}
