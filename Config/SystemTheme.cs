using Microsoft.Win32;

namespace GlideScroll.Config;

/// <summary>Reads the Windows app theme preference from the registry.</summary>
public static class SystemTheme
{
    private const string PersonalizeKey =
        @"Software\Microsoft\Windows\CurrentVersion\Themes\Personalize";

    /// <summary>Dark mode for application UI (controls Auto-mode in our settings window).</summary>
    public static bool IsDarkMode()
    {
        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(PersonalizeKey);
            object? value = key?.GetValue("AppsUseLightTheme");
            if (value is int i) return i == 0;
        }
        catch { }
        return false;
    }

    /// <summary>
    /// Dark mode for the system chrome (taskbar, tray). Controlled separately
    /// from app theme via SystemUsesLightTheme. Used to pick which tray icon
    /// variant to display so the icon stays legible against the actual tray.
    /// </summary>
    public static bool IsTrayDarkMode()
    {
        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(PersonalizeKey);
            object? value = key?.GetValue("SystemUsesLightTheme");
            if (value is int i) return i == 0;
        }
        catch { }
        // Default to dark, Windows 11's default install has a dark taskbar.
        return true;
    }
}
