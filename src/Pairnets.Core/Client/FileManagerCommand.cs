namespace Pairnets.Core.Client;

/// <summary>What a right-click item in the file manager asks Pairnets to do with a file or folder.</summary>
public enum FileManagerAction
{
    /// <summary>"Open in Pairnets": the main window, on that file's history.</summary>
    Open,

    /// <summary>"Show sync status": a short answer about that file or folder.</summary>
    ShowStatus,

    /// <summary>"Where this folder lives…": the Files page on that folder, with its choice open.</summary>
    WhereItLives,
}

/// <summary>
/// A right-click item's request. The file manager starts Pairnets with <c>--open-in-pairnets "path"</c> or
/// <c>--sync-status "path"</c>; when Pairnets is already running, that second copy passes the request on through
/// <see cref="AppActivation.TrySendToRunning"/> as <see cref="ToMessage"/> and quits.
/// </summary>
public sealed record FileManagerCommand(FileManagerAction Action, string Path)
{
    public const string OpenArgument = "--open-in-pairnets";
    public const string StatusArgument = "--sync-status";
    public const string WhereArgument = "--where-it-lives";

    private const string MessagePrefix = "pairnets-file:";

    /// <summary>The request in a program's arguments, or null when there is none.</summary>
    public static FileManagerCommand? FromArguments(IReadOnlyList<string> args)
    {
        for (var i = 0; i + 1 < args.Count; i++)
        {
            var action = args[i] switch
            {
                OpenArgument => FileManagerAction.Open,
                StatusArgument => FileManagerAction.ShowStatus,
                WhereArgument => FileManagerAction.WhereItLives,
                _ => (FileManagerAction?)null,
            };
            if (action is { } a && args[i + 1].Length > 0)
                return new FileManagerCommand(a, args[i + 1]);
        }
        return null;
    }

    /// <summary>How the request travels to the running Pairnets.</summary>
    public string ToMessage() => MessagePrefix + Action switch
    {
        FileManagerAction.Open => "open",
        FileManagerAction.WhereItLives => "where",
        _ => "status",
    } + "\n" + Path;

    /// <summary>The request in a poke's message, or null for a plain poke or anything else.</summary>
    public static FileManagerCommand? FromMessage(string? message)
    {
        if (message is null || !message.StartsWith(MessagePrefix, StringComparison.Ordinal))
            return null;
        var rest = message[MessagePrefix.Length..];
        var newline = rest.IndexOf('\n');
        if (newline <= 0 || newline == rest.Length - 1)
            return null;
        var action = rest[..newline] switch
        {
            "open" => FileManagerAction.Open,
            "status" => FileManagerAction.ShowStatus,
            "where" => FileManagerAction.WhereItLives,
            _ => (FileManagerAction?)null,
        };
        var path = rest[(newline + 1)..];
        return action is { } a && path.IndexOf('\0') < 0 ? new FileManagerCommand(a, path) : null;
    }
}
