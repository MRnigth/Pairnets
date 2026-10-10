using System.Windows;
using Microsoft.Extensions.Logging;
using Pairnets.Client.Platform;
using Pairnets.Client.Ui;
using Pairnets.Core;
using Pairnets.Core.Client;
using Pairnets.Core.Settings;
using Pairnets.Core.Sync;

namespace Pairnets.Client.Tests;

/// <summary>A thread-safe list for things the app does on its own threads.</summary>
public sealed class Log<T>
{
    private readonly List<T> _items = [];

    public void Add(T item)
    {
        lock (_items)
            _items.Add(item);
    }

    public IReadOnlyList<T> Items
    {
        get
        {
            lock (_items)
                return _items.ToList();
        }
    }

    public int Count => Items.Count;

    public void Clear()
    {
        lock (_items)
            _items.Clear();
    }
}

/// <summary>Keeps keys in plain sight with a marker, and writes down what was forgotten.</summary>
public sealed class FakeSecrets : ISecretProtector
{
    public const string Prefix = "test-protected:";

    public Log<string> Forgotten { get; } = new();

    public string Protect(string plainText) => Prefix + plainText;

    public string Unprotect(string protectedText) =>
        protectedText.StartsWith(Prefix, StringComparison.Ordinal) ? protectedText[Prefix.Length..] : throw new InvalidOperationException("Not saved by this computer.");

    public void Forget(string protectedText) => Forgotten.Add(protectedText);
}

/// <summary>Stands in for Windows: writes down what the app asked for, starts nothing and changes nothing.</summary>
public sealed class FakeWindowsPlatform : IWindowsPlatform
{
    private readonly FakeSecrets _secrets = new();

    public ISecretProtector Secrets => _secrets;

    public FakeSecrets FakeSecrets => _secrets;

    /// <summary>Files, folders and web pages the app opened.</summary>
    public Log<string> Opened { get; } = new();

    public Log<string> Revealed { get; } = new();

    public Log<(string Path, string Arguments)> Installers { get; } = new();

    /// <summary>Every "start with Windows" change, in order.</summary>
    public Log<bool> AutoStartChanges { get; } = new();

    public Log<string> Trashed { get; } = new();

    public bool AutoStart { get; private set; }

    public ILocalTrash CreateTrash(ILogger log) => new RecordingTrash(this);

    public bool IsAutoStartEnabled() => AutoStart;

    public void SetAutoStart(bool enabled)
    {
        AutoStart = enabled;
        AutoStartChanges.Add(enabled);
    }

    public void Open(string path) => Opened.Add(path);

    public void Reveal(string path) => Revealed.Add(path);

    public void RunInstaller(string path, string arguments) => Installers.Add((path, arguments));

    private sealed class RecordingTrash(FakeWindowsPlatform platform) : ILocalTrash
    {
        public void Delete(string fullPath)
        {
            platform.Trashed.Add(fullPath);
            File.Delete(fullPath);
        }
    }
}

/// <summary>
/// Answers Pairnets' message boxes, folder picker and clipboard in place of a person, and writes down what was asked.
/// A question with no rule gets the careful answer (Cancel, No, or OK when that is the only button).
/// </summary>
internal sealed class DialogScript : Dialogs.IResponder
{
    private readonly List<(string Contains, MessageBoxResult Answer)> _rules = [];

    public Log<Dialogs.Question> Asked { get; } = new();

    public Log<string> Copied { get; } = new();

    /// <summary>Titles of the folder pickers that opened.</summary>
    public Log<string> FolderPickers { get; } = new();

    /// <summary>What the folder picker "chooses"; null presses Cancel.</summary>
    public string? Folder { get; set; }

    /// <summary>True acts like another program holding the clipboard.</summary>
    public bool ClipboardBusy { get; set; }

    /// <summary>Answers questions whose text contains <paramref name="contains"/> with <paramref name="answer"/>.</summary>
    public DialogScript When(string contains, MessageBoxResult answer)
    {
        lock (_rules)
            _rules.Add((contains, answer));
        return this;
    }

    /// <summary>Answers every question with OK or Yes.</summary>
    public DialogScript AgreeToEverything() => When(string.Empty, MessageBoxResult.OK);

    public MessageBoxResult Answer(Dialogs.Question question)
    {
        Asked.Add(question);
        MessageBoxResult? answer;
        lock (_rules)
            answer = _rules.Where(r => question.Text.Contains(r.Contains, StringComparison.Ordinal)).Select(r => (MessageBoxResult?)r.Answer).FirstOrDefault();
        return (answer, question.Buttons) switch
        {
            (MessageBoxResult.OK or MessageBoxResult.Yes, MessageBoxButton.YesNo or MessageBoxButton.YesNoCancel) => MessageBoxResult.Yes,
            (MessageBoxResult.OK or MessageBoxResult.Yes, _) => MessageBoxResult.OK,
            ({ } given, _) => given,
            (null, MessageBoxButton.OK) => MessageBoxResult.OK,
            (null, MessageBoxButton.OKCancel) => MessageBoxResult.Cancel,
            _ => MessageBoxResult.No,
        };
    }

    public string? PickFolder(string title, string? initialDirectory)
    {
        FolderPickers.Add(title);
        return Folder;
    }

    public bool Copy(string text)
    {
        if (ClipboardBusy)
            return false;
        Copied.Add(text);
        return true;
    }

    /// <summary>Makes this script answer the app's dialogs until disposed.</summary>
    public IDisposable Install()
    {
        Dialogs.Responder = this;
        return new Uninstall();
    }

    private sealed class Uninstall : IDisposable
    {
        public void Dispose() => Dialogs.Responder = null;
    }
}

/// <summary>The windows' actions, written down by name ("TogglePause", "OpenWindow(History)").</summary>
public sealed class RecordingActions(IHistorySource? history = null) : ITrayActions
{
    public Log<string> Calls { get; } = new();

    public IHistorySource? History { get; } = history;

    public void FixBlocked() => Calls.Add(nameof(FixBlocked));
    public void SyncNow() => Calls.Add(nameof(SyncNow));
    public void TogglePause() => Calls.Add(nameof(TogglePause));
    public void OpenFolder() => Calls.Add(nameof(OpenFolder));
    public void ShowSettings() => Calls.Add(nameof(ShowSettings));
    public void ViewLog() => Calls.Add(nameof(ViewLog));
    public void UpdateNow() => Calls.Add(nameof(UpdateNow));
    public void DismissUpdate() => Calls.Add(nameof(DismissUpdate));
    public void DownloadNow() => Calls.Add(nameof(DownloadNow));
    public void UpdateServer() => Calls.Add(nameof(UpdateServer));
    public void ReportBug() => Calls.Add(nameof(ReportBug));
    public void RefreshDevices() => Calls.Add(nameof(RefreshDevices));
    public void AddComputer() => Calls.Add(nameof(AddComputer));
    public void ManageDevices() => Calls.Add(nameof(ManageDevices));
    public void RevealFile(string syncPath) => Calls.Add($"{nameof(RevealFile)}({syncPath})");
    public void OpenWindow(MainPage page) => Calls.Add($"{nameof(OpenWindow)}({page})");
    public void Quit() => Calls.Add(nameof(Quit));
}

/// <summary>Sample files and versions for the History page, with every read and restore written down.</summary>
public sealed class SampleHistory(bool broken = false) : IHistorySource
{
    private readonly DateTimeOffset _now = DateTimeOffset.UtcNow;

    public Log<string> Reads { get; } = new();

    public Log<(string Path, string Version)> Restored { get; } = new();

    public Task<IReadOnlyList<ServerFile>> GetServerFilesAsync(CancellationToken ct)
    {
        Reads.Add("files");
        if (broken)
            throw new Pairnets.Core.Api.PairnetsNetworkException("The server did not answer.");
        return Task.FromResult<IReadOnlyList<ServerFile>>(
        [
            new("Projects/2026/budget-draft.xlsx", true, _now.AddHours(-2), 0),
            new("Photos/summer/IMG_2041.jpg", true, _now.AddDays(-1), 0),
            new("Notes/old-report.md", true, _now.AddDays(-5), 0),
            new("Projects/report.docx", false, _now.AddMinutes(-20), 482_304),
            new("Notes/ideas.md", false, _now.AddDays(-2), 1_204),
        ]);
    }

    public Task<IReadOnlyList<HistoryVersion>> GetVersionsAsync(string path, CancellationToken ct)
    {
        Reads.Add("versions:" + path);
        return Task.FromResult<IReadOnlyList<HistoryVersion>>(
        [
            new("20261006T100000000Z-c0ffee00", _now.AddHours(-2), 48_220, "c0ffee00"),
            new("20261004T100000000Z-be5eda7a", _now.AddDays(-2), 47_100, "be5eda7a"),
        ]);
    }

    public Task<string?> RestoreVersionAsync(string path, HistoryVersion version, CancellationToken ct)
    {
        Restored.Add((path, version.Id));
        return Task.FromResult<string?>(null);
    }
}
