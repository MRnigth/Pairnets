using Avalonia.Controls;
using Microsoft.Extensions.Logging;
using Pairnets.Core.Settings;
using Pairnets.Core.Sync;
using Pairnets.Desktop;
using Pairnets.Desktop.Platform;

namespace Pairnets.Tests.Ui;

/// <summary>
/// The desktop the app runs on in the tests: it keeps track of every window the app opens (as Avalonia's own
/// desktop lifetime does) and records <see cref="Shutdown"/> instead of ending the test run.
/// </summary>
public sealed class FakeLifetime : IDesktopLifetime, IDisposable
{
    private readonly List<Window> _windows = [];
    private readonly IDisposable _opened;
    private readonly IDisposable _closed;

    public FakeLifetime()
    {
        _opened = Window.WindowOpenedEvent.AddClassHandler(typeof(Window), (sender, _) =>
        {
            if (sender is Window window && !_windows.Contains(window))
                _windows.Add(window);
        });
        _closed = Window.WindowClosedEvent.AddClassHandler(typeof(Window), (sender, _) =>
        {
            if (sender is Window window)
                _windows.Remove(window);
        });
    }

    /// <summary>How often the app asked to quit.</summary>
    public int ShutdownCount { get; private set; }

    /// <summary>The windows that are open (shown or hidden), oldest first.</summary>
    public IReadOnlyList<Window> Windows => _windows.ToList();

    public void Shutdown() => ShutdownCount++;

    /// <summary>The shown window with this title, or null.</summary>
    public Window? Shown(string title) => _windows.LastOrDefault(w => w.IsVisible && w.Title == title);

    public void Dispose()
    {
        _opened.Dispose();
        _closed.Dispose();
    }
}

/// <summary>Keeps secrets in memory, as the Keychain or keyring would keep them.</summary>
public sealed class MemorySecrets : ISecretProtector
{
    private readonly Dictionary<string, string> _secrets = [];
    private readonly List<string> _forgotten = [];
    private int _next;

    public string Protect(string plainText)
    {
        lock (_secrets)
        {
            var marker = "memory:" + ++_next;
            _secrets[marker] = plainText;
            return marker;
        }
    }

    public string Unprotect(string protectedText)
    {
        lock (_secrets)
            return _secrets.TryGetValue(protectedText, out var plain) ? plain : throw new InvalidOperationException("No such secret: " + protectedText);
    }

    public void Forget(string protectedText)
    {
        lock (_secrets)
        {
            _secrets.Remove(protectedText);
            _forgotten.Add(protectedText);
        }
    }

    /// <summary>The secrets still kept (plain text).</summary>
    public IReadOnlyList<string> Kept
    {
        get
        {
            lock (_secrets)
                return _secrets.Values.ToList();
        }
    }

    public IReadOnlyList<string> Forgotten
    {
        get
        {
            lock (_secrets)
                return _forgotten.ToList();
        }
    }
}

/// <summary>
/// The operating system around the app: records what it opens, reveals and announces and the start-at-login
/// choice, keeps secrets in memory, and deletes files for good instead of using a trash.
/// </summary>
public sealed class FakePlatform : IPlatformServices
{
    private readonly object _gate = new();
    private readonly List<string> _opened = [];
    private readonly List<string> _revealed = [];
    private readonly List<(string Title, string Text)> _notices = [];
    private readonly List<bool> _autoStartChanges = [];
    private bool _autoStart;

    public string Name => "Test desktop";

    public MemorySecrets SecretStore { get; } = new();

    public ISecretProtector Secrets => SecretStore;

    public ILocalTrash CreateTrash(ILogger log) => new PermanentDeleteTrash();

    public void Notify(string title, string text)
    {
        lock (_gate)
            _notices.Add((title, text));
    }

    public bool IsAutoStartEnabled()
    {
        lock (_gate)
            return _autoStart;
    }

    public void SetAutoStart(bool enabled)
    {
        lock (_gate)
        {
            _autoStart = enabled;
            _autoStartChanges.Add(enabled);
        }
    }

    public bool RemoveLegacyAutoStart() => false;

    public void Open(string path)
    {
        lock (_gate)
            _opened.Add(path);
    }

    public void Reveal(string path)
    {
        lock (_gate)
            _revealed.Add(path);
    }

    /// <summary>Files, folders and web pages the app opened, in order.</summary>
    public IReadOnlyList<string> Opened
    {
        get
        {
            lock (_gate)
                return _opened.ToList();
        }
    }

    public IReadOnlyList<string> Revealed
    {
        get
        {
            lock (_gate)
                return _revealed.ToList();
        }
    }

    public IReadOnlyList<(string Title, string Text)> Notices
    {
        get
        {
            lock (_gate)
                return _notices.ToList();
        }
    }

    public IReadOnlyList<bool> AutoStartChanges
    {
        get
        {
            lock (_gate)
                return _autoStartChanges.ToList();
        }
    }
}
