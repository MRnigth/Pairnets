using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Interop;
using System.Windows.Media;
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

    /// <summary>
    /// "Simple window drawing" (<see cref="Pairnets.Core.Client.SimpleDrawing"/>): the windows are drawn without the
    /// graphics card. Also on by itself when Windows reports no usable graphics card (WPF draws in software then anyway).
    /// </summary>
    public static bool SimpleDrawing { get; private set; }

    /// <summary>Turns simple drawing on or off, for the windows open now and every window opened later.</summary>
    public static void SetSimpleDrawing(Application app, bool on)
    {
        SimpleDrawing = on;
        foreach (Window w in app.Windows)
            ApplyDrawing(w);
    }

    /// <summary>Applies the theme to <paramref name="app"/>. <paramref name="dark"/> null follows Windows.</summary>
    public static void Apply(Application app, bool? dark = null)
    {
        Ui.ScreenReaderNames.Register();
        var resources = app.Resources.MergedDictionaries;
        if (_palette is null)
        {
            resources.Add(new ResourceDictionary { Source = new Uri(Base + "Controls.xaml") });
            if (Pairnets.Core.DevBetaLook.On)
                UseDisplayTitles(app);
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

    /// <summary>
    /// The Developer beta look sets page titles and card headings in the condensed display face (Barlow Condensed Bold).
    /// The same style keys, restyled on top of the originals, so every page that uses them follows without changes.
    /// </summary>
    private static void UseDisplayTitles(Application app)
    {
        var display = (FontFamily)app.FindResource("F.Display");
        foreach (var (key, size) in new[] { ("Title", 38.0), ("PgTitle", 38.0), ("PgCardTitle", 18.0) })
        {
            var baseStyle = (Style)app.FindResource(key);
            var style = new Style(typeof(TextBlock), baseStyle);
            style.Setters.Add(new Setter(TextBlock.FontFamilyProperty, display));
            style.Setters.Add(new Setter(TextBlock.FontSizeProperty, size));
            style.Setters.Add(new Setter(TextBlock.FontWeightProperty, FontWeights.Bold));
            app.Resources[key] = style;
        }
    }

    /// <summary>Call from a window's constructor: styles it and keeps the title bar in step with the theme.</summary>
    public static void Attach(Window window)
    {
        window.SetResourceReference(FrameworkElement.StyleProperty, "PairnetsWindow");
        window.SourceInitialized += (_, _) =>
        {
            ApplyTitleBar(window);
            ApplyDrawing(window);
        };
    }

    /// <summary>
    /// Software or hardware drawing for this window only. Windows that draw through their own see-through layer (the
    /// tray panel) already reach the screen without the graphics card's direct path, so they are left as they are.
    /// </summary>
    private static void ApplyDrawing(Window window)
    {
        if (window.AllowsTransparency || PresentationSource.FromVisual(window) is not HwndSource { CompositionTarget: { } target })
            return;
        var simple = SimpleDrawing || RenderCapability.Tier >> 16 == 0;
        target.RenderMode = simple ? RenderMode.SoftwareOnly : RenderMode.Default;
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
