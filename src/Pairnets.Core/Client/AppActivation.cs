using System.IO.Pipes;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;

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

    /// <summary>
    /// One pipe per user, so two people on one machine do not poke each other's app. The name stays short on
    /// purpose: on macOS and Linux a pipe is a socket file whose whole path may not pass 104 bytes, and a long
    /// user name or temp folder would make every poke fail.
    /// </summary>
    public static string DefaultPipeName { get; } =
        "pairnets-" + Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(Environment.UserName)))[..8].ToLowerInvariant();

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
        NamedPipeServerStream? current = null;
        try
        {
            while (!ct.IsCancellationRequested)
            {
                current ??= TryOpen(name);
                if (current is null)
                {
                    // The name is taken: wait a moment and try again.
                    if (!await PauseAsync(TimeSpan.FromSeconds(1), ct).ConfigureAwait(false))
                        break;
                    continue;
                }
                var connected = false;
                try
                {
                    await current.WaitForConnectionAsync(ct).ConfigureAwait(false);
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
                // The next instance opens before this one closes. On macOS and Linux a pipe is one listening
                // socket shared by its instances: if the last instance closed first, a poke arriving in that gap
                // would sit in the old socket's queue and be lost with it.
                var next = TryOpen(name);
                activated();
                await current.DisposeAsync().ConfigureAwait(false);
                current = next;
                if (!connected && !await PauseAsync(TimeSpan.FromMilliseconds(100), ct).ConfigureAwait(false))
                    break; // a breather, in case the wait keeps failing for some other reason
            }
        }
        catch (ArgumentOutOfRangeException)
        {
            // The pipe's name does not fit this platform's socket path: there is no way back to the app from the
            // browser here, but the app itself works. Stop quietly instead of taking the process down.
        }
        finally
        {
            if (current is not null)
                await current.DisposeAsync().ConfigureAwait(false);
        }
    }

    /// <summary>Opens a listening pipe instance, or null when the name is taken (two instances of this one are allowed at once).</summary>
    private static NamedPipeServerStream? TryOpen(string name)
    {
        try
        {
            return new NamedPipeServerStream(name, PipeDirection.In, maxNumberOfServerInstances: 2,
                PipeTransmissionMode.Byte, PipeOptions.Asynchronous);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return null;
        }
    }

    /// <summary>Waits; false when listening was stopped meanwhile.</summary>
    private static async Task<bool> PauseAsync(TimeSpan time, CancellationToken ct)
    {
        try
        {
            await Task.Delay(time, ct).ConfigureAwait(false);
            return true;
        }
        catch (OperationCanceledException)
        {
            return false;
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
        catch (Exception ex) when (ex is TimeoutException or IOException or UnauthorizedAccessException or ArgumentOutOfRangeException)
        {
            return false; // the last one: a pipe name too long for this platform's socket path
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
