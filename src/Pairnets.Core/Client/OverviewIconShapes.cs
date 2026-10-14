namespace Pairnets.Core.Client;

/// <summary>
/// The line shapes of <see cref="OverviewIcon"/> as path text on a 24 by 24 square, the same in both apps (each parses
/// it into its own geometry). <c>Filled</c> icons are drawn solid as well as outlined.
/// </summary>
public static class OverviewIconShapes
{
    public static (string Path, bool Filled) Of(OverviewIcon icon) => icon switch
    {
        OverviewIcon.Check => ("M5 12.5l4.5 4.5L19 7.5", false),
        OverviewIcon.Hourglass => ("M6 3h12M6 21h12M7 3c0 5 5 6 5 9s-5 4-5 9M17 3c0 5-5 6-5 9s5 4 5 9", false),
        OverviewIcon.Shield => ("M12 3 4.5 6v5.5c0 4.6 3.2 8.2 7.5 9.5 4.3-1.3 7.5-4.9 7.5-9.5V6L12 3zM9 12.5l2 2 4-4.5", false),
        OverviewIcon.Key => ("M12 15a4 4 0 1 1-8 0a4 4 0 0 1 8 0zM10.8 12.2l9.2-9.2M17 6l3 3M14.5 8.5l2 2", false),
        OverviewIcon.UserX => ("M14 8a4 4 0 1 1-8 0a4 4 0 0 1 8 0zM3 21a7 7 0 0 1 11.5-5.4M17 15l4 4M21 15l-4 4", false),
        OverviewIcon.CloudOff => ("M3 3l18 18M6.2 8.2A5 5 0 0 0 7 18h10.8M20.6 16.2A4 4 0 0 0 17 10h-.7A6.5 6.5 0 0 0 9.6 5.5", false),
        OverviewIcon.Pause => ("M9 5v14M15 5v14", false),
        OverviewIcon.Play => ("M8 5.5v13l10.5-6.5L8 5.5z", true),
        OverviewIcon.FolderQuestion => ("M3 7a2 2 0 0 1 2-2h4l2 2h8a2 2 0 0 1 2 2v8a2 2 0 0 1-2 2H5a2 2 0 0 1-2-2V7zM10.2 11.2a1.9 1.9 0 1 1 2.6 1.7c-.5.3-.8.7-.8 1.2M12 16.5v.01", false),
        OverviewIcon.Folder => ("M3 7a2 2 0 0 1 2-2h4l2 2h8a2 2 0 0 1 2 2v8a2 2 0 0 1-2 2H5a2 2 0 0 1-2-2V7z", false),
        OverviewIcon.FolderFill => ("M2 6.5A2.5 2.5 0 0 1 4.5 4h4.2l2 2h8.8A2.5 2.5 0 0 1 22 8.5v9a2.5 2.5 0 0 1-2.5 2.5h-15A2.5 2.5 0 0 1 2 17.5z", true),
        OverviewIcon.Sync => ("M20 11a8 8 0 0 0-14.3-4.9L4 8M4 3v5h5M4 13a8 8 0 0 0 14.3 4.9L20 16M20 21v-5h-5", false),
        OverviewIcon.List => ("M9 6h11M9 12h11M9 18h11M4.5 6h.01M4.5 12h.01M4.5 18h.01", false),
        OverviewIcon.External => ("M14 4h6v6M20 4 11 13M19 14v5a1 1 0 0 1-1 1H5a1 1 0 0 1-1-1V6a1 1 0 0 1 1-1h5", false),
        OverviewIcon.SignIn => ("M15 3h4a2 2 0 0 1 2 2v14a2 2 0 0 1-2 2h-4M10 17l5-5-5-5M15 12H3", false),
        OverviewIcon.Search => ("M17.5 11a6.5 6.5 0 1 1-13 0a6.5 6.5 0 0 1 13 0zM20 20l-4.4-4.4", false),
        OverviewIcon.Clock => ("M21 12a9 9 0 1 1-18 0a9 9 0 0 1 18 0zM12 7v5l3 2", false),
        OverviewIcon.Up => ("M12 19V5M5 12l7-7 7 7", false),
        OverviewIcon.Down => ("M12 5v14M5 12l7 7 7-7", false),
        OverviewIcon.Computer => ("M5.5 5h13A1.5 1.5 0 0 1 20 6.5v8a1.5 1.5 0 0 1-1.5 1.5h-13A1.5 1.5 0 0 1 4 14.5v-8A1.5 1.5 0 0 1 5.5 5zM2 19h20", false),
        OverviewIcon.Server => ("M5 4h14a2 2 0 0 1 2 2v3a2 2 0 0 1-2 2H5a2 2 0 0 1-2-2V6a2 2 0 0 1 2-2zM5 13h14a2 2 0 0 1 2 2v3a2 2 0 0 1-2 2H5a2 2 0 0 1-2-2v-3a2 2 0 0 1 2-2zM7 7.5h.01M7 16.5h.01", false),
        OverviewIcon.Disk => ("M22 12H2M5.5 5h13l3.5 7v6a2 2 0 0 1-2 2H4a2 2 0 0 1-2-2v-6zM6 16h.01M10 16h.01", false),
        OverviewIcon.File => ("M14 3H7a2 2 0 0 0-2 2v14a2 2 0 0 0 2 2h10a2 2 0 0 0 2-2V8l-5-5zM14 3v5h5", false),
        OverviewIcon.Document => ("M14 3H7a2 2 0 0 0-2 2v14a2 2 0 0 0 2 2h10a2 2 0 0 0 2-2V8l-5-5zM14 3v5h5M9 13h6M9 17h6", false),
        OverviewIcon.Image => ("M5 4h14a2 2 0 0 1 2 2v12a2 2 0 0 1-2 2H5a2 2 0 0 1-2-2V6a2 2 0 0 1 2-2zM11 10a2 2 0 1 1-4 0a2 2 0 0 1 4 0zM21 16l-5-5-9 9", false),
        OverviewIcon.Video => ("M4 5h11a2 2 0 0 1 2 2v10a2 2 0 0 1-2 2H4a2 2 0 0 1-2-2V7a2 2 0 0 1 2-2zM17 10l5-3v10l-5-3", false),
        OverviewIcon.Info => ("M21 12a9 9 0 1 1-18 0a9 9 0 0 1 18 0zM12 11v5M12 7.5v.5", false),
        OverviewIcon.Warning => ("M12 3 2 20h20L12 3zM12 10v4M12 17v.5", false),
        _ => (string.Empty, false),
    };
}
