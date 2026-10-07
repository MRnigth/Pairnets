using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;
using Microsoft.Win32;

namespace Pairnets.Client.Themes;

/// <summary>
/// Loads the shared styles and the light or dark palette, follows the Windows "app mode" setting
/// live, and gives every Pairnets window a matching (dark or light) title bar.
/// </summary>
public static class ThemeManager
{
    private const string Base = "pack://application:,,,/Pairnets;component/Themes/";
    private static ResourceDictionary? _palette;
    private static bool _listening;

    public static bool IsDark { get; private set; }

    /// <summary>Applies the theme to <paramref name="app"/>. <paramref name="dark"/> null follows Windows.</summary>
    public static void Apply(Application app, bool? dark = null)
    {
        var resources = app.Resources.MergedDictionaries;
        if (_palette is null)
        {
            resources.Add(new ResourceDictionary { Source = new Uri(Base + "Controls.xaml") });
        }
        else
        {
            resources.Remove(_palette);
        }
        IsDark = dark ?? SystemPrefersDark();
        _palette = new ResourceDictionary { Source = new Uri(Base + (IsDark ? "Dark.xaml" : "Light.xaml")) };
        resources.Add(_palette);

        foreach (Window w in app.Windows)
            ApplyTitleBar(w);

        if (dark is null && !_listening)
        {
            _listening = true;
            SystemEvents.UserPreferenceChanged += (_, e) =>
            {
                if (e.Category == UserPreferenceCategory.General && SystemPrefersDark() != IsDark)
                    app.Dispatcher.BeginInvoke(() => Apply(app));
            };
        }
    }

    /// <summary>Call from a window's constructor: styles it and keeps the title bar in step with the theme.</summary>
    public static void Attach(Window window)
    {
        window.SetResourceReference(FrameworkElement.StyleProperty, "PairnetsWindow");
        window.SourceInitialized += (_, _) => ApplyTitleBar(window);
    }

    private static bool SystemPrefersDark()
    {
        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(@"Software\Microsoft\Windows\CurrentVersion\Themes\Personalize");
            return key?.GetValue("AppsUseLightTheme") is int light && light == 0;
        }
        catch (Exception ex) when (ex is System.Security.SecurityException or UnauthorizedAccessException or IOException)
        {
            return false;
        }
    }

    [DllImport("dwmapi.dll")]
    private static extern int DwmSetWindowAttribute(IntPtr hwnd, int attribute, ref int value, int size);

    private static void ApplyTitleBar(Window window)
    {
        var hwnd = new WindowInteropHelper(window).Handle;
        if (hwnd == IntPtr.Zero)
            return;
        var value = IsDark ? 1 : 0;
        // DWMWA_USE_IMMERSIVE_DARK_MODE: 20 on Windows 10 2004+ and 11, 19 on older builds. Failure is harmless.
        if (DwmSetWindowAttribute(hwnd, 20, ref value, sizeof(int)) != 0)
            _ = DwmSetWindowAttribute(hwnd, 19, ref value, sizeof(int));
    }
}
