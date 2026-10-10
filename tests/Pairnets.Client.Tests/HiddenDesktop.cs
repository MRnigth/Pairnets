using System.ComponentModel;
using System.Runtime.InteropServices;

namespace Pairnets.Client.Tests;

/// <summary>
/// Runs WPF on a Windows desktop of its own that is never shown, so a run of the Windows app's tests (or its
/// screenshots) never shows anything on the screen of whoever sits at the computer. Moving windows off screen is
/// not enough: Windows keeps a menu on the screen, at the mouse pointer.
/// </summary>
/// <remarks>
/// A thread can only move to another desktop before it has a window, and an STA thread that .NET starts already has
/// one (COM's). So the thread is started by Windows itself, moves, and only then becomes an STA thread.
/// </remarks>
public static class HiddenDesktop
{
    private const uint GenericAll = 0x10000000;

    private delegate uint NativeThreadStart(IntPtr parameter);

    // Windows calls these from its own threads: they must outlive them.
    private static readonly List<NativeThreadStart> Starts = [];

    /// <summary>Starts an STA thread on a new hidden desktop and runs <paramref name="body"/> on it.</summary>
    /// <returns>A task that ends when <paramref name="body"/> returns, or fails with its exception.</returns>
    public static Task Run(string threadName, Action body)
    {
        var done = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        NativeThreadStart start = _ =>
        {
            try
            {
                MoveThisThread();
                if (!Thread.CurrentThread.TrySetApartmentState(ApartmentState.STA))
                    throw new InvalidOperationException("The thread on the hidden desktop could not become a UI (STA) thread.");
                Thread.CurrentThread.Name = threadName;
                Thread.CurrentThread.IsBackground = true;
                body();
                done.SetResult();
            }
            catch (Exception ex)
            {
                done.SetException(ex);
            }
            return 0;
        };
        lock (Starts)
            Starts.Add(start);
        var thread = CreateThread(IntPtr.Zero, UIntPtr.Zero, Marshal.GetFunctionPointerForDelegate(start), IntPtr.Zero, 0, out _);
        if (thread == IntPtr.Zero)
            throw new Win32Exception(Marshal.GetLastWin32Error(), "Could not start the thread for the hidden desktop.");
        CloseHandle(thread);
        return done.Task;
    }

    private static void MoveThisThread()
    {
        var name = $"Pairnets-tests-{Environment.ProcessId}-{Environment.CurrentManagedThreadId}";
        // The handle stays open while the process runs; Windows removes the desktop once the process has ended.
        var desktop = CreateDesktopW(name, IntPtr.Zero, IntPtr.Zero, 0, GenericAll, IntPtr.Zero);
        if (desktop == IntPtr.Zero)
            throw new Win32Exception(Marshal.GetLastWin32Error(), "Could not make a hidden desktop for the windows.");
        if (!SetThreadDesktop(desktop))
            throw new Win32Exception(Marshal.GetLastWin32Error(), "Could not move onto the hidden desktop.");
    }

    [DllImport("user32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    private static extern IntPtr CreateDesktopW(string name, IntPtr device, IntPtr devMode, uint flags, uint access, IntPtr security);

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetThreadDesktop(IntPtr desktop);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern IntPtr CreateThread(IntPtr security, UIntPtr stackSize, IntPtr start, IntPtr parameter, uint flags, out uint threadId);

    [DllImport("kernel32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool CloseHandle(IntPtr handle);
}
