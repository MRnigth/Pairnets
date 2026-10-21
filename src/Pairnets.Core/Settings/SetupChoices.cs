using Pairnets.Core.Sync;

namespace Pairnets.Core.Settings;

/// <summary>
/// What the person ticked on the Windows installer's "Select Additional Tasks" page, as the installer leaves it for the
/// app ("explorerside=1;explorermenu=1;syncticks=0;where=nest;checkupdates=1;updatenest=0;autostart=1"). The app takes
/// it into its settings once, at its next start; a silent update from inside Pairnets writes nothing, so it never
/// undoes what was changed in Settings since.
/// </summary>
public static class SetupChoices
{
    /// <summary>Applies the choices to <paramref name="settings"/>; unknown or missing ones leave the setting as it is.</summary>
    public static void Apply(ClientSettings settings, string choices)
    {
        foreach (var part in choices.Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            var eq = part.IndexOf('=');
            if (eq <= 0)
                continue;
            var key = part[..eq].Trim().ToLowerInvariant();
            var value = part[(eq + 1)..].Trim().ToLowerInvariant();
            bool? on = value switch
            {
                "1" or "true" or "yes" => true,
                "0" or "false" or "no" => false,
                _ => null,
            };
            switch (key)
            {
                case "explorerside" when on is { } side:
                    settings.ShowInFileManager = side;
                    break;
                case "explorermenu" when on is { } menu:
                    settings.ExplorerRightClick = menu;
                    break;
                case "syncticks" when on is { } ticks:
                    settings.CloudFolder = ticks ? CloudFolderMode.KeepAllHere : CloudFolderMode.Off;
                    break;
                case "checkupdates" when on is { } check:
                    settings.CheckForUpdates = check;
                    break;
                case "updatenest" when on is { } nest:
                    settings.AutoUpdateServer = nest;
                    break;
                case "autostart" when on is { } start:
                    settings.StartWithWindows = start;
                    break;
                case "where":
                    var placement = value switch
                    {
                        "here" => FilePlacement.OnThisPc,
                        "ondemand" => FilePlacement.OnDemand,
                        "nest" => FilePlacement.NestOnly,
                        _ => (FilePlacement?)null,
                    };
                    if (placement is FilePlacement.OnThisPc)
                        settings.Placement.Remove("");
                    else if (placement is { } p)
                        settings.Placement[""] = p;
                    break;
            }
        }
    }
}
