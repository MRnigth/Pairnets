using Microsoft.Extensions.Logging;

namespace Pairnets.Core.Client;

/// <summary>
/// Catches the errors nothing else caught, in both apps: logs each one and keeps a cleaned
/// <see cref="CrashCard"/> for it on this computer. <see cref="Watch"/> covers errors behind the scenes
/// (the app closes) and in background tasks (it keeps running); each app reports the errors on its screen
/// side itself with <see cref="Record"/>. Nothing is sent anywhere.
/// </summary>
public sealed class CrashCatcher : IDisposable
{
    private readonly string _directory;
    private readonly string _app;
    private readonly ILogger _log;
    private readonly Func<IEnumerable<string?>> _privateValues;
    private bool _watching;

    /// <param name="directory">Where the cards are kept (<see cref="ClientEnvironment.CrashesDir"/>).</param>
    /// <param name="app">Which app ("Windows app", "Mac app", …).</param>
    /// <param name="privateValues">The names to take out of the card at the time of the crash
    /// (see <see cref="PrivacyCleaner.ThisComputer"/>).</param>
    public CrashCatcher(string directory, string app, ILogger log, Func<IEnumerable<string?>> privateValues)
    {
        _directory = directory;
        _app = app;
        _log = log;
        _privateValues = privateValues;
    }

    public string Directory => _directory;

    /// <summary>Starts catching errors behind the scenes and in background tasks, for the whole app.</summary>
    public void Watch()
    {
        if (_watching)
            return;
        _watching = true;
        AppDomain.CurrentDomain.UnhandledException += OnUnhandled;
        TaskScheduler.UnobservedTaskException += OnUnobserved;
    }

    /// <summary>Logs <paramref name="error"/> and keeps a card for it.</summary>
    public void Record(Exception error, CrashPlace place)
    {
        switch (place)
        {
            case CrashPlace.Screen:
                _log.LogError(error, "Unhandled UI exception");
                break;
            case CrashPlace.Task:
                _log.LogError(error, "Unobserved task exception");
                break;
            default:
                _log.LogCritical(error, "Unhandled exception");
                break;
        }
        try
        {
            CrashCards.Add(_directory, CrashCard.From(error, place, _app, _privateValues()));
        }
        catch (Exception ex)
        {
            // Keeping the card must never cause a second error.
            _log.LogWarning("Could not keep a crash card: {Error}", ex.Message);
        }
    }

    private void OnUnhandled(object? sender, UnhandledExceptionEventArgs args)
    {
        if (args.ExceptionObject is Exception error)
            Record(error, CrashPlace.Background);
        else
            _log.LogCritical("Unhandled exception: {Error}", args.ExceptionObject);
    }

    private void OnUnobserved(object? sender, UnobservedTaskExceptionEventArgs args)
    {
        Record(args.Exception, CrashPlace.Task);
        args.SetObserved();
    }

    public void Dispose()
    {
        if (!_watching)
            return;
        _watching = false;
        AppDomain.CurrentDomain.UnhandledException -= OnUnhandled;
        TaskScheduler.UnobservedTaskException -= OnUnobserved;
    }
}
