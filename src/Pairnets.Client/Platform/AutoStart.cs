using Microsoft.Win32;

namespace Pairnets.Client.Platform;

/// <summary>"Start with Windows" via the per-user Run key (no admin rights needed).</summary>
public static class AutoStart
{
    private const string RunKey = @"Software\Microsoft\Windows\CurrentVersion\Run";
    private const string ValueName = "Pairnets";

    public static bool IsEnabled()
    {
        using var key = Registry.CurrentUser.OpenSubKey(RunKey);
        return key?.GetValue(ValueName) is string;
    }

    /// <summary>Removes Tether's start-at-login entry. True when there was one (the user had it on).</summary>
    public static bool RemoveLegacy()
    {
        using var key = Registry.CurrentUser.OpenSubKey(RunKey, writable: true);
        if (key?.GetValue(Pairnets.Core.Legacy.TetherNames.WindowsRunValue) is null)
            return false;
        key.DeleteValue(Pairnets.Core.Legacy.TetherNames.WindowsRunValue);
        return true;
    }

    public static void Set(bool enabled)
    {
        using var key = Registry.CurrentUser.CreateSubKey(RunKey);
        if (enabled)
            key.SetValue(ValueName, $"\"{Environment.ProcessPath}\" --autostart");
        else if (key.GetValue(ValueName) is not null)
            key.DeleteValue(ValueName);
    }
}
