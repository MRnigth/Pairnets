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
/// Opening Pairnets again from the Start menu, a shortcut or the installer pokes the same way
/// (<see cref="SecondStart"/>). The running app answers with one byte once its window is really up, so a
/// second start can tell a stuck app from one that came forward. A poke may carry a message first (a right-click
/// item in the file manager, <see cref="FileManagerCommand"/>), ended by a zero byte; a plain poke sends just the
/// zero byte, and an older Pairnets' poke sends nothing at all.
/// </summary>
public static class AppActivation
{
    /// <summary>True for a "pairnets://…" program argument.</summary>
    public static bool IsActivationUrl(string? arg) =>
        arg is not null && arg.StartsWith("pairnets:", StringComparison.OrdinalIgnoreCase);

    /// <summary>The argument the start-at-login entry passes.</summary>
    public const string AutoStartArgument = "--autostart";

    /// <summary>What a second start says when the running Pairnets did not come forward; each app adds how to end it.</summary>
    public const string NotAnsweringMessage = "Pairnets is already running but isn't responding. Give it a moment and try again.";

    /// <summary>
    /// One pipe per user, so two people on one machine do not poke each other's app. The name stays short on
    /// purpose: on macOS and Linux a pipe is a socket file whose whole path may not pass 104 bytes, and a long
    /// user name or temp folder would make every poke fail.
    /// </summary>
    public static string DefaultPipeName { get; } =
        "pairnets-" + Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(Environment.UserName)))[..8].ToLowerInvariant();

    /// <summary>How long the running app's window may take to come forward before a poke goes unanswered.</summary>
    public static TimeSpan AnswerWithin { get; } = TimeSpan.FromSeconds(10); // a first window on a slow computer can take seconds

    private static readonly byte[] ShownAnswer = [1];

    /// <summary>
    /// Starts answering pokes; <paramref name="activated"/> fires on a background thread for each one and the
    /// poke is answered right away. Disposing the returned handle stops listening.
    /// </summary>
    public static IDisposable Listen(Action activated, string? pipeName = null) =>
        Listen(() =>
        {
            activated();
            return Task.CompletedTask;
        }, pipeName);

    /// <summary>
    /// Starts answering pokes; <paramref name="activated"/> fires on a background thread for each one, and the
    /// poke is answered once its task finishes within <see cref="AnswerWithin"/> (later or failed: no answer).
    /// Disposing the returned handle stops listening.
    /// </summary>
    public static IDisposable Listen(Func<Task> activated, string? pipeName = null) => Listen(_ => activated(), pipeName);

    /// <summary>
    /// As <see cref="Listen(Func{Task}, string?)"/>, and hands on what the poke said: null for a plain poke, or the
    /// message a second Pairnets sent with <see cref="Poke"/> (the file manager's "Open in Pairnets", for example).
    /// </summary>
    public static IDisposable Listen(Func<string?, Task> activated, string? pipeName = null)
    {
        var stop = new CancellationTokenSource();
        _ = Task.Run(() => ListenLoopAsync(activated, pipeName ?? DefaultPipeName, stop.Token));
        return new Stopper(stop);
    }

    /// <summary>The longest message a poke may carry (a command and a full path).</summary>
    public const int MaxMessageBytes = 8192;

    private static async Task ListenLoopAsync(Func<string?, Task> activated, string name, CancellationToken ct)
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
                var message = connected ? await ReadMessageAsync(current, ct).ConfigureAwait(false) : null;
                if (await RunAsync(() => activated(message), ct).ConfigureAwait(false) && connected)
                    await AnswerAsync(current, ct).ConfigureAwait(false);
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

    /// <summary>
    /// Reads the poke's message up to its zero byte, waiting at most a second. A plain poke (or an older Pairnets'
    /// poke, which sends nothing and hangs up) gives null, as does a message too long, unreadable or not finished in time.
    /// </summary>
    private static async Task<string?> ReadMessageAsync(NamedPipeServerStream pipe, CancellationToken ct)
    {
        using var wait = CancellationTokenSource.CreateLinkedTokenSource(ct);
        wait.CancelAfter(TimeSpan.FromSeconds(1));
        var buffer = new byte[MaxMessageBytes + 1];
        var length = 0;
        try
        {
            while (length < buffer.Length)
            {
                var read = await pipe.ReadAsync(buffer.AsMemory(length, 1), wait.Token).ConfigureAwait(false);
                if (read == 0)
                    return null; // hung up without finishing: an older Pairnets' plain poke
                if (buffer[length] == 0)
                    break;
                length++;
            }
        }
        catch (Exception ex) when (ex is OperationCanceledException or IOException or ObjectDisposedException)
        {
            return null;
        }
        if (length == 0 || length > MaxMessageBytes)
            return null;
        try
        {
            return new UTF8Encoding(false, throwOnInvalidBytes: true).GetString(buffer, 0, length);
        }
        catch (DecoderFallbackException)
        {
            return null;
        }
    }

    /// <summary>Does the poke's work; false when it failed or did not finish in time (the app's window is stuck).</summary>
    private static async Task<bool> RunAsync(Func<Task> activated, CancellationToken ct)
    {
        try
        {
            await activated().WaitAsync(AnswerWithin, ct).ConfigureAwait(false);
            return true;
        }
        catch (Exception)
        {
            return false;
        }
    }

    /// <summary>
    /// Says "shown" to the poker, then waits for it to hang up: closing our end first could throw the byte
    /// away before it was read.
    /// </summary>
    private static async Task AnswerAsync(NamedPipeServerStream pipe, CancellationToken ct)
    {
        try
        {
            await pipe.WriteAsync(ShownAnswer, ct).ConfigureAwait(false);
            await pipe.FlushAsync(ct).ConfigureAwait(false);
            using var hangUp = CancellationTokenSource.CreateLinkedTokenSource(ct);
            hangUp.CancelAfter(TimeSpan.FromSeconds(2));
            _ = await pipe.ReadAsync(new byte[1], hangUp.Token).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is IOException or ObjectDisposedException or OperationCanceledException)
        {
            // The poker is gone or never read the answer: nothing more to do.
        }
    }

    /// <summary>
    /// Opens a listening pipe instance, or null when the name is taken (two instances of this one are allowed at once).
    /// Only this user can connect, so someone else on the same computer cannot poke this app.
    /// </summary>
    private static NamedPipeServerStream? TryOpen(string name)
    {
        try
        {
            return new NamedPipeServerStream(name, PipeDirection.InOut, maxNumberOfServerInstances: 2,
                PipeTransmissionMode.Byte, PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly);
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
    public static bool TrySignalRunning(string? pipeName = null, int timeoutMs = 2000) =>
        Poke(pipeName, timeoutMs) != PokeResult.NotRunning;

    /// <summary>
    /// Hands <paramref name="message"/> to the running Pairnets (see <see cref="Listen(Func{string?, Task}, string?)"/>)
    /// and waits for it to be done. False when none is listening, it did not answer, or the message is too long.
    /// </summary>
    public static bool TrySendToRunning(string message, string? pipeName = null, int timeoutMs = 2000) =>
        Poke(pipeName, timeoutMs, message: message) == PokeResult.Shown;

    /// <summary>
    /// Pokes the running Pairnets, with <paramref name="message"/> when there is one, and waits for its answer.
    /// Connecting keeps retrying for <paramref name="connectTimeoutMs"/>, which covers a Pairnets still starting up
    /// that has not opened its pipe yet.
    /// </summary>
    public static PokeResult Poke(string? pipeName = null, int connectTimeoutMs = 10_000, int answerTimeoutMs = 15_000, string? message = null)
    {
        var bytes = Encoding.UTF8.GetBytes(message ?? "");
        if (bytes.Length > MaxMessageBytes || bytes.Contains((byte)0))
            return PokeResult.NotRunning; // cannot be sent
        AllowTakingTheForeground();
        try
        {
            using var client = new NamedPipeClientStream(".", pipeName ?? DefaultPipeName, PipeDirection.InOut,
                PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly);
            client.Connect(connectTimeoutMs);
            try
            {
                using var wait = new CancellationTokenSource(answerTimeoutMs);
                client.WriteAsync(bytes.Append((byte)0).ToArray(), wait.Token).AsTask().GetAwaiter().GetResult();
                client.FlushAsync(wait.Token).GetAwaiter().GetResult();
                var answer = new byte[1];
                var read = client.ReadAsync(answer, wait.Token).AsTask().GetAwaiter().GetResult();
                return read == 1 && answer[0] == ShownAnswer[0] ? PokeResult.Shown : PokeResult.NotAnswering;
            }
            catch (Exception ex) when (ex is OperationCanceledException or IOException)
            {
                return PokeResult.NotAnswering;
            }
        }
        catch (Exception ex) when (ex is TimeoutException or IOException or UnauthorizedAccessException or ArgumentOutOfRangeException)
        {
            return PokeResult.NotRunning; // the last one: a pipe name too long for this platform's socket path
        }
    }

    /// <summary>
    /// Pairnets was started while it is already running: bring the running one forward, except at login (that start
    /// must never pop a window up). Started by a right-click item in the file manager, the running one does that
    /// instead. <see cref="SecondStartResult.NotAnswering"/>: the running one did not come forward, so tell the user.
    /// </summary>
    public static SecondStartResult SecondStart(IEnumerable<string> args, string? pipeName = null,
        int connectTimeoutMs = 10_000, int answerTimeoutMs = 15_000)
    {
        var list = args.ToList();
        if (list.Contains(AutoStartArgument))
            return SecondStartResult.Quiet;
        var message = FileManagerCommand.FromArguments(list)?.ToMessage();
        return Poke(pipeName, connectTimeoutMs, answerTimeoutMs, message) == PokeResult.Shown
            ? SecondStartResult.Quiet
            : SecondStartResult.NotAnswering;
    }

    /// <summary>
    /// Windows only lets the program the user just started bring a window to the front. That program is
    /// us (the browser opened pairnets://, or the user opened Pairnets again), so pass the right on before
    /// poking the running app.
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

    public enum PokeResult
    {
        /// <summary>No Pairnets is listening.</summary>
        NotRunning,
        /// <summary>A Pairnets is listening but its window did not come forward in time.</summary>
        NotAnswering,
        /// <summary>The running Pairnets came to the front.</summary>
        Shown,
    }

    public enum SecondStartResult
    {
        /// <summary>Nothing to tell: the running Pairnets came forward, or this was the start at login.</summary>
        Quiet,
        /// <summary>The running Pairnets did not come forward.</summary>
        NotAnswering,
    }

    /// <summary>Cancels the listen loop; the source is never disposed so a poke mid-stop cannot trip on it.</summary>
    private sealed class Stopper(CancellationTokenSource stop) : IDisposable
    {
        public void Dispose() => stop.Cancel();
    }
}
