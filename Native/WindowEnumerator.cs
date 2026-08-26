namespace GlideScroll.Native;

/// <summary>One running app entry: exe filename plus a friendly display name.</summary>
public readonly struct RunningApp
{
    public readonly string ExeName;     // matches against config app lists
    public readonly string DisplayName; // shown in UI

    public RunningApp(string exe, string display)
    {
        ExeName = exe;
        DisplayName = display;
    }
}

/// <summary>
/// Enumerates visible, top-level windows that look like real user-facing
/// applications (filters tool windows, cloaked windows, hidden message
/// windows, etc.) and dedupes by exe so multiple windows of the same
/// process appear once.
/// </summary>
public static class WindowEnumerator
{
    /// <summary>Returns up to <paramref name="maxResults"/> visible apps, deduped by exe.</summary>
    public static List<RunningApp> EnumerateVisibleApps(int maxResults = 30)
    {
        // First pass: collect candidate windows that pass our visibility filter.
        var candidates = new List<(string Exe, string Title, bool TitleIsRich, IntPtr Hwnd)>();

        Win32.EnumWindows((hwnd, _) =>
        {
            try
            {
                if (!IsRealAppWindow(hwnd)) return true;

                // Title (skip if blank, already filtered, but defensive)
                int len = Win32.GetWindowTextLength(hwnd);
                if (len <= 0) return true;
                var sb = new System.Text.StringBuilder(len + 1);
                Win32.GetWindowText(hwnd, sb, sb.Capacity);
                string title = sb.ToString().Trim();
                if (title.Length == 0) return true;

                // Owner process exe via QueryFullProcessImageNameW (avoids the
                // Process.MainModule loader-lock cost and the per-process
                // access-denied exception storm on a busy desktop).
                Win32.GetWindowThreadProcessId(hwnd, out uint pid);
                if (pid == 0) return true;
                string exe = Win32.GetProcessExeName(pid);
                if (string.IsNullOrEmpty(exe)) return true;

                candidates.Add((exe, title, true, hwnd));
            }
            catch { /* skip flaky windows */ }
            return true;
        }, IntPtr.Zero);

        // Second pass: dedupe by exe. For deduped exes, fall back to
        // friendly name (FileVersionInfo.FileDescription) since picking
        // one arbitrary window's title would be misleading.
        var byExe = new Dictionary<string, (string Display, int Count)>(StringComparer.OrdinalIgnoreCase);
        foreach (var c in candidates)
        {
            if (byExe.TryGetValue(c.Exe, out var existing))
            {
                byExe[c.Exe] = (existing.Display, existing.Count + 1);
            }
            else
            {
                byExe[c.Exe] = (c.Title, 1);
            }
        }

        var results = new List<RunningApp>();
        foreach (var kv in byExe)
        {
            string display = kv.Value.Count > 1
                ? FriendlyNameFor(kv.Key) ?? StripExe(kv.Key)
                : kv.Value.Display;
            results.Add(new RunningApp(kv.Key, display));
        }

        results.Sort((a, b) => string.Compare(a.DisplayName, b.DisplayName, StringComparison.OrdinalIgnoreCase));
        return results.Count > maxResults ? results.GetRange(0, maxResults) : results;
    }

    /// <summary>Standard "what counts as a real app window" filter chain.</summary>
    private static bool IsRealAppWindow(IntPtr hwnd)
    {
        if (!Win32.IsWindowVisible(hwnd)) return false;

        int exStyle = Win32.GetWindowLong(hwnd, Win32.GWL_EXSTYLE);
        if ((exStyle & Win32.WS_EX_TOOLWINDOW) != 0) return false;

        // DWM cloaked attribute filters UWP apps that are technically
        // running but not actually visible (suspended in background).
        int cloaked = 0;
        if (Win32.DwmGetWindowAttribute(hwnd, Win32.DWMWA_CLOAKED, out cloaked, sizeof(int)) == 0)
        {
            if (cloaked != 0) return false;
        }

        return true;
    }

    private static string? FriendlyNameFor(string exeName)
    {
        try
        {
            // Look up a running process with that exe to get FileDescription.
            string nameNoExt = exeName.EndsWith(".exe", StringComparison.OrdinalIgnoreCase)
                ? exeName.Substring(0, exeName.Length - 4)
                : exeName;
            var procs = System.Diagnostics.Process.GetProcessesByName(nameNoExt);
            foreach (var p in procs)
            {
                try
                {
                    string? desc = p.MainModule?.FileVersionInfo.FileDescription;
                    if (!string.IsNullOrWhiteSpace(desc)) return desc;
                }
                catch { }
                finally { p.Dispose(); }
            }
        }
        catch { }
        return null;
    }

    private static string StripExe(string exeName)
        => exeName.EndsWith(".exe", StringComparison.OrdinalIgnoreCase)
            ? exeName.Substring(0, exeName.Length - 4)
            : exeName;
}
