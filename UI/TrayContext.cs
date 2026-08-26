using GlideScroll.Config;
using GlideScroll.Native;
using GlideScroll.Pipeline;
using System.Reflection;

namespace GlideScroll.UI;

/// <summary>
/// Tray-only ApplicationContext. Hosts the NotifyIcon, pipeline, and
/// MainForm lifecycle. Right-clicking the tray icon brings up a custom
/// rounded popup menu (CustomPopupMenu), frameless GDI+-rendered to match
/// the LM Studio / Win11 modern look. The dynamic submenu populates with
/// currently running visible apps so you can quickly add to the active list.
/// </summary>
public sealed class TrayContext : ApplicationContext
{
    private readonly NotifyIcon _trayIcon;
    private readonly ScrollPipeline _pipeline;
    private AppConfig _config;
    private MainForm? _mainForm;
    private bool _trayIconIsDarkVariant;

    public TrayContext()
    {
        _config = AppConfig.Load();

        // Reconcile the HKCU\Run autostart entry with the config every launch.
        // Two reasons this is needed (not just on first toggle):
        //   1. First-launch bug: GeneralConfig defaults AutostartWithWindows = true,
        //      but the registry entry only gets written when the user manually
        //      toggles the checkbox. Without this call, the default is a lie , 
        //      UI shows ✓, registry has nothing, app never auto-starts.
        //   2. Path-staleness: if the user moves GlideScroll.exe to a new folder,
        //      the registry entry still points at the old path. SetEnabled writes
        //      Environment.ProcessPath fresh, so each launch self-heals the path.
        // Idempotent, calling SetEnabled(true) when already enabled just rewrites
        // the same entry, and SetEnabled(false) when nothing is there is a no-op.
        Autostart.SetEnabled(_config.General.AutostartWithWindows);

        _pipeline = new ScrollPipeline(_config);
        _pipeline.Start();

        _trayIcon = new NotifyIcon
        {
            Text = "GlideScroll",
            Visible = true,
            Icon = LoadTrayIconForCurrentTheme()
        };
        _trayIcon.DoubleClick += (_, _) => ShowMainForm();
        _trayIcon.MouseUp += OnTrayMouseUp;

        // Re-evaluate tray icon variant when the system theme could have changed.
        // Cheap, runs on each click, doesn't need a registry watcher.

        // Game Mode visibility: mirror the pause reason into the tray tooltip
        // so a tester can see at a glance why smoothing stopped ("paused in
        // game (Valorant)"). 2s cadence; reading a volatile string, trivial.
        _statusTimer = new System.Windows.Forms.Timer { Interval = 2000 };
        _statusTimer.Tick += (_, _) => UpdateTrayStatusText();
        _statusTimer.Start();
    }

    private readonly System.Windows.Forms.Timer _statusTimer;

    private void UpdateTrayStatusText()
    {
        string reason = _pipeline.GamePauseStatus;
        string text = reason.Length == 0
            ? "GlideScroll"
            : $"GlideScroll, paused in game ({reason})";
        // NotifyIcon.Text throws over 63 chars on some Windows builds.
        if (text.Length > 63) text = text.Substring(0, 63);
        if (_trayIcon.Text != text) _trayIcon.Text = text;
    }

    /// <summary>Pick the icon variant (white or black stroke) that contrasts with the current tray theme.</summary>
    private Icon LoadTrayIconForCurrentTheme()
    {
        bool trayDark = SystemTheme.IsTrayDarkMode();
        _trayIconIsDarkVariant = trayDark;
        string fileName = trayDark ? "glidescroll-white.ico" : "glidescroll-black.ico";
        try
        {
            var asm = Assembly.GetExecutingAssembly();
            using var stream = asm.GetManifestResourceStream($"GlideScroll.Resources.{fileName}");
            if (stream is not null) return new Icon(stream);
        }
        catch { }
        return SystemIcons.Application;
    }

    private void RefreshTrayIconIfThemeChanged()
    {
        bool trayDark = SystemTheme.IsTrayDarkMode();
        if (trayDark != _trayIconIsDarkVariant)
        {
            var oldIcon = _trayIcon.Icon;
            _trayIcon.Icon = LoadTrayIconForCurrentTheme();
            try { oldIcon?.Dispose(); } catch { }
        }
    }

    private void OnTrayMouseUp(object? sender, MouseEventArgs e)
    {
        if (e.Button != MouseButtons.Right) return;
        RefreshTrayIconIfThemeChanged();
        ShowContextMenu(Cursor.Position);
    }

    private void ShowContextMenu(Point screenPos)
    {
        using var menu = new CustomPopupMenu();

        menu.AddItem("Settings...", () => ShowMainForm(), bold: true);
        menu.AddSeparator();

        // Quick path: click to target a window rather than scroll a process
        // list. Full/advanced list management lives in Settings.
        bool exclusion = _config.Apps.Mode == AppListMode.Exclusion;
        string targetLabel = exclusion ? "Disable for app..." : "Enable for app...";
        menu.AddItem(targetLabel, BeginAppTargeting);

        menu.AddSeparator();
        menu.AddItem("Exit GlideScroll", ExitApp);

        menu.Show(screenPos.X, screenPos.Y);
    }

    // ----- click-to-target finder -----
    // Reuses the pipeline's already-installed mouse hook: arm targeting, then a
    // UI timer follows the cursor with the native tooltip and, when the hook
    // reports a captured click, adds that window. Esc / right-click / 20s cancel.
    private System.Windows.Forms.Timer? _targetTimer;
    private CaptureTooltip? _targetTip;
    private long _targetStartMs;
    private string _targetVerb = "disable";

    private void BeginAppTargeting()
    {
        _targetVerb = _config.Apps.Mode == AppListMode.Exclusion ? "disable" : "enable";
        _targetTip ??= new CaptureTooltip();
        _targetStartMs = Environment.TickCount64;
        _pipeline.BeginTargeting();

        _targetTimer?.Stop();
        _targetTimer?.Dispose();
        _targetTimer = new System.Windows.Forms.Timer { Interval = 30 };
        _targetTimer.Tick += (_, _) => TargetTick();
        _targetTimer.Start();
    }

    private void TargetTick()
    {
        // Esc or a ~20s idle timeout cancels.
        bool esc = (Win32.GetAsyncKeyState(Win32.VK_ESCAPE) & 0x8000) != 0;
        if (esc || Environment.TickCount64 - _targetStartMs > 20000)
        {
            _pipeline.CancelTargeting();
            EndAppTargeting();
            return;
        }

        if (!_pipeline.TargetingActive)
        {
            // A click landed (or right-click cancelled it).
            if (_pipeline.TryTakePick(out string cls, out string exe))
            {
                var (entry, _, ok) = WindowTargeting.Resolve("app", cls, exe);
                if (ok) AddAppToActiveList(entry);
            }
            EndAppTargeting();
            return;
        }

        // Still targeting: follow the cursor with a live preview.
        Win32.GetCursorPos(out Win32.POINT p);
        IntPtr root = Win32.CursorRootWindow();
        Win32.GetWindowThreadProcessId(root, out uint pid);
        string text;
        if (root == IntPtr.Zero || pid == (uint)Environment.ProcessId)
        {
            text = $"Click a window to {_targetVerb} it";
        }
        else
        {
            string cls = Win32.ClassNameOf(root);
            string exe = pid != 0 ? Win32.GetProcessExeName(pid) : "";
            var (_, label, ok) = WindowTargeting.Resolve("app", cls, exe);
            text = ok ? $"{Cap(_targetVerb)}: {label}" : $"Click a window to {_targetVerb} it";
        }
        _targetTip?.ShowAt(text, p.X, p.Y);
    }

    private void EndAppTargeting()
    {
        _targetTimer?.Stop();
        _targetTimer?.Dispose();
        _targetTimer = null;
        _targetTip?.HideTip();
    }

    private static string Cap(string s) => s.Length == 0 ? s : char.ToUpperInvariant(s[0]) + s.Substring(1);

    private void AddAppToActiveList(string exeName)
    {
        var list = _config.Apps.Mode == AppListMode.Exclusion
            ? _config.Apps.Blacklist : _config.Apps.Whitelist;
        if (!list.Any(n => string.Equals(n, exeName, StringComparison.OrdinalIgnoreCase)))
            list.Add(exeName);
        _config.Save();
        _pipeline.UpdateConfig(_config);

        // If the settings window is currently open, push the updated apps list
        // into its WebView so the visible blacklist/whitelist reflects the new
        // entry immediately. Without this, the in-memory config and the
        // displayed list desync until the user closes and reopens the window.
        if (_mainForm is not null && !_mainForm.IsDisposed)
        {
            _mainForm.NotifyAppsListChangedFromTray();
        }

        if (_config.General.ShowNotificationOnModeToggle)
        {
            string verb = _config.Apps.Mode == AppListMode.Exclusion ? "Disabled for" : "Enabled for";
            _trayIcon.ShowBalloonTip(2000, "GlideScroll", $"{verb} {exeName}", ToolTipIcon.Info);
        }
    }

    private void ShowMainForm()
    {
        if (_mainForm is null || _mainForm.IsDisposed)
        {
            _mainForm = new MainForm(_config, _pipeline);
            _mainForm.FormClosed += (_, _) => _mainForm = null;
        }

        _mainForm.Show();
        _mainForm.WindowState = FormWindowState.Normal;
        _mainForm.Activate();
        _mainForm.BringToFront();
        _mainForm.RefreshSystemTheme();
    }

    private void ExitApp()
    {
        _trayIcon.Visible = false;
        _pipeline.Stop();
        _pipeline.Dispose();
        _mainForm?.Close();
        ExitThread();
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            _statusTimer?.Dispose();
            _targetTimer?.Dispose();
            _targetTip?.Dispose();
            _trayIcon?.Dispose();
            _pipeline?.Dispose();
        }
        base.Dispose(disposing);
    }
}
