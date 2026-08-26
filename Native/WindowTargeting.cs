using GlideScroll.Config;

namespace GlideScroll.Native;

/// <summary>
/// Single source of truth for turning a window (its root class + owning exe)
/// into an app-rules entry. Shared by the settings-window finders and the tray
/// click-to-target finder so both decide identically.
///
///   kind "class" -> always the window class.
///   kind "app"   -> the exe, except for the curated earmark classes (File
///                   Explorer, Photo Viewer) which resolve to a class rule.
///
/// The earmark set is deliberately tiny and cross-app-safe: each class uniquely
/// identifies one surface. Shared classes (Chromium, UWP, dialogs) are never
/// auto-added here, only reachable via the deliberate Add class path.
/// </summary>
internal static class WindowTargeting
{
    public static readonly HashSet<string> EarmarkClasses = BuildEarmark();

    private static HashSet<string> BuildEarmark()
    {
        var set = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var b in AppListConfig.DefaultBlocks)
            if (b.StartsWith("class:", StringComparison.OrdinalIgnoreCase))
                set.Add(b.Substring(6));
        return set;
    }

    /// <summary>Resolve to (entry, humanLabel, ok). entry is "" when ok is false.</summary>
    public static (string entry, string label, bool ok) Resolve(string kind, string cls, string exe)
    {
        if (kind == "class")
        {
            return cls.Length > 0
                ? ("class:" + cls, FriendlyClass(cls) + " (class)", true)
                : ("", "", false);
        }

        // "game" (Game Mode list): always the exe, never a class rule. Games
        // are ordinary exes; the earmark surfaces are not games.
        if (kind == "game")
        {
            return exe.Length > 0
                ? (exe, FriendlyAppName(exe) + " (game)", true)
                : ("", "", false);
        }

        // "app"
        if (cls.Length > 0 && EarmarkClasses.Contains(cls))
            return ("class:" + cls, FriendlyClass(cls) + " (class)", true);
        if (exe.Length > 0)
            return (exe, FriendlyAppName(exe) + " (exe)", true);
        return ("", "", false);
    }

    public static string FriendlyAppName(string exe)
    {
        if (string.IsNullOrEmpty(exe)) return "";
        string noExt = exe.EndsWith(".exe", StringComparison.OrdinalIgnoreCase)
            ? exe.Substring(0, exe.Length - 4) : exe;
        if (noExt.Length == 0) return "";
        return char.ToUpperInvariant(noExt[0]) + noExt.Substring(1);
    }

    public static string FriendlyClass(string cls) => cls switch
    {
        "CabinetWClass" => "Windows File Explorer",
        "Photo_Lightweight_Viewer" => "Windows Photo Viewer",
        _ => cls
    };
}
