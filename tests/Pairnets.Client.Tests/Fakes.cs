using System.Windows;
using Microsoft.Extensions.Logging;
using Pairnets.Client.Platform;
using Pairnets.Client.Ui;
using Pairnets.Core;
using Pairnets.Core.Client;
using Pairnets.Core.Settings;
using Pairnets.Core.Sync;
using Pairnets.Tests.Infrastructure;

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

    /// <summary>The new Pairnets.exe a copy from the zip started after replacing itself (nothing is started).</summary>
    public Log<(string Path, string Arguments)> UpdatedApps { get; } = new();

    public void StartUpdatedApp(string path, string arguments) => UpdatedApps.Add((path, arguments));

    /// <summary>Every change to Pairnets' place in File Explorer, in order (null: taken away).</summary>
    public Log<string?> ExplorerEntries { get; } = new();

    /// <summary>What the installer's Tasks page left for the app (null: nothing).</summary>
    public (string Id, string Choices)? SetupChoices { get; set; }

    public (string Id, string Choices)? ReadSetupChoices() => SetupChoices;

    /// <summary>Which parts each of those asked for: the left side, the right-click items.</summary>
    public Log<(bool LeftSide, bool RightClick)> ExplorerParts { get; } = new();

    public bool SetExplorerEntry(string? folder, bool leftSide = true, bool rightClick = true, bool folderIcon = true)
    {
        ExplorerParts.Add((leftSide, rightClick));
        ExplorerEntries.Add(folder);
        return true;
    }

    /// <summary>What "make it a cloud folder" was asked for, in order; the stand-in never touches Windows' cloud files.</summary>
    public Log<CloudFolderMode> CloudFolderStarts { get; } = new();

    public Client.Platform.CloudFiles.ICloudFolder? StartCloudFolder(string folder, CloudFolderMode mode, ILogger log, out string? problem,
        IReadOnlyDictionary<string, Core.Sync.FilePlacement>? placement = null)
    {
        CloudFolderStarts.Add(mode);
        problem = "Tests leave Windows' cloud files alone.";
        return null;
    }

    public bool HasCloudFolder() => false;

    public int ForgetCloudFolders() => 0;

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

    /// <summary>The Files page's pop-ups that opened, by title.</summary>
    public Log<string> Popups { get; } = new();

    /// <summary>The button pressed in a pop-up with buttons of its own, by the start of its label; unset closes it.</summary>
    public Func<Dialogs.Choice, string?> Chooses { get; set; } = _ => null;

    /// <summary>What the rename box answers (null: Cancel).</summary>
    public string? Typed { get; set; }

    /// <summary>What the strong delete question answers (null: Keep it).</summary>
    public string? DeleteWord { get; set; }

    /// <summary>Where "Save a copy…" saves (null: Cancel).</summary>
    public string? SaveTo { get; set; }

    /// <summary>The files "Add files" adds (null: Cancel).</summary>
    public IReadOnlyList<string>? AddFiles { get; set; }

    public string? Choose(Dialogs.Choice choice)
    {
        Popups.Add(choice.Title);
        return Chooses(choice);
    }

    public string? Prompt(string title, string initial)
    {
        Popups.Add(title);
        return Typed;
    }

    public string? PickOne(string title, IReadOnlyList<string> items)
    {
        Popups.Add(title);
        return items.Count > 0 ? items[0] : null;
    }

    public string? DeleteForGood(string title)
    {
        Popups.Add(title);
        return DeleteWord;
    }

    public string? SaveFile(string title, string fileName)
    {
        Popups.Add(title);
        return SaveTo;
    }

    public IReadOnlyList<string>? OpenFiles(string title)
    {
        Popups.Add(title);
        return AddFiles;
    }

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

    /// <summary>The nest's files for the Files page (null: not connected).</summary>
    public FakeNestFiles? NestFiles { get; set; } = new();

    public INestFiles? Files => NestFiles;

    public void OpenFile(string path) => Calls.Add($"{nameof(OpenFile)}({path})");

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
    public Task<bool> SetDeviceIcon(string id, string icon)
    {
        Calls.Add($"icon:{id}:{icon}");
        return Task.FromResult(true);
    }
    public void AddComputer() => Calls.Add(nameof(AddComputer));
    public void ManageDevices() => Calls.Add(nameof(ManageDevices));
    public void RevealFile(string syncPath) => Calls.Add($"{nameof(RevealFile)}({syncPath})");
    public void OpenWindow(MainPage page) => Calls.Add($"{nameof(OpenWindow)}({page})");
    public void Quit() => Calls.Add(nameof(Quit));

    private readonly HashSet<NoticeKind> _silenced = [];

    public bool Notifies(NoticeKind kind) => !_silenced.Contains(kind);

    public void SetNotifies(NoticeKind kind, bool on)
    {
        if (on)
            _silenced.Remove(kind);
        else
            _silenced.Add(kind);
        Calls.Add($"SetNotifies({kind},{on})");
    }

    public void SignOut() => Calls.Add(nameof(SignOut));

    public void SignInAgain() => Calls.Add(nameof(SignInAgain));

    public void OpenAccountSite() => Calls.Add(nameof(OpenAccountSite));
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
