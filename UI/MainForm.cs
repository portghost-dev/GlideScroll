using GlideScroll.Config;
using GlideScroll.Native;
using GlideScroll.Pipeline;
using Microsoft.Web.WebView2.WinForms;
using Microsoft.Web.WebView2.Core;
using System.Reflection;
using System.Text.Json;

namespace GlideScroll.UI;

/// <summary>
/// Hosts a WebView2 with the GlideScroll settings UI. JS sends typed
/// messages via window.chrome.webview.postMessage; we deserialize, mutate
/// the config, push to the pipeline + disk, then echo updates back to
/// the JS side as needed.
/// </summary>
public sealed class MainForm : Form
{
    private readonly AppConfig _config;
    private readonly ScrollPipeline _pipeline;
    private WebView2 _webView = null!;
    private bool _initialized;

    // Coalesces rapid setting changes (spinner +/- held down, slider drag,
    // color-swatch edits) into one disk write per 250ms quiet window.
    // The pipeline still gets updated immediately for live feel; only the
    // config.json fsync is deferred.
    private System.Windows.Forms.Timer? _saveDebounceTimer;

    public MainForm(AppConfig config, ScrollPipeline pipeline)
    {
        _config = config;
        _pipeline = pipeline;

        Text = "GlideScroll";
        MinimumSize = new Size(580, 540);
        Size = new Size(640, 620);
        StartPosition = FormStartPosition.CenterScreen;
        // Frameless: native chrome is gone; the title bar is drawn inside the
        // WebView2 (see WebUI.cs #titlebar). Window dragging, resize, snap,
        // and min/max are routed back through C# message handlers.
        FormBorderStyle = FormBorderStyle.None;
        BackColor = Color.FromArgb(30, 30, 30);

        // Load the white-stroke icon (visible in alt-tab + taskbar; we draw our
        // own copy inside the HTML title bar too).
        try { Icon = LoadEmbeddedIcon("glidescroll-white.ico"); }
        catch { /* fall through to default form icon */ }

        BuildUi();
    }

    /// <summary>
    /// Re-add WS_THICKFRAME so the OS still treats this as a sizable/snappable
    /// window even though we have no visible native chrome. Without this,
    /// Win+arrow snap and the standard maximize-on-titlebar-doubleclick wouldn't
    /// work. WS_MINIMIZEBOX/MAXIMIZEBOX make sure those system commands are
    /// allowed; neither button is visually present.
    /// </summary>
    protected override CreateParams CreateParams
    {
        get
        {
            var cp = base.CreateParams;
            cp.Style |= Win32.WS_THICKFRAME | Win32.WS_MINIMIZEBOX | Win32.WS_MAXIMIZEBOX;
            return cp;
        }
    }

    /// <summary>
    /// Frameless windows that maximize naively spill ~8px past the working
    /// area on every edge (the OS expects the window to draw a 8px border
    /// where the resize frame would go). We catch maximize-state changes here
    /// and notify the JS side so it can pad accordingly.
    /// </summary>
    protected override void WndProc(ref Message m)
    {
        // Eat WM_NCCALCSIZE so the OS doesn't reserve a non-client frame at the
        // top edge. WS_THICKFRAME (added in CreateParams above) normally draws a
        // 1-2px resize border that bleeds through above our HTML title bar as a
        // light artifact. Returning 0 with wParam=TRUE tells Windows the entire
        // window rect IS the client rect, no NC area, no top sliver, while
        // still letting Aero Snap / Win+arrow work because the style flag stays.
        if (m.Msg == Win32.WM_NCCALCSIZE && m.WParam != IntPtr.Zero)
        {
            m.Result = IntPtr.Zero;
            return;
        }

        if (m.Msg == Win32.WM_SIZE)
        {
            int kind = m.WParam.ToInt32();
            if (kind == Win32.SIZE_MAXIMIZED || kind == Win32.SIZE_RESTORED)
            {
                // Defer until base handling is done so dimensions are stable.
                BeginInvoke(new Action(() => PushWindowState(kind == Win32.SIZE_MAXIMIZED)));
            }
        }
        base.WndProc(ref m);
    }

    private void PushWindowState(bool maximized)
    {
        PostToWeb(new { type = "window-state", maximized });
    }

    private static Icon LoadEmbeddedIcon(string fileName)
    {
        var asm = Assembly.GetExecutingAssembly();
        // Embedded resource names are namespaced by default; csproj root namespace
        // is "GlideScroll", folder is "Resources", so logical path is built up.
        string resourceName = $"GlideScroll.Resources.{fileName}";
        using var stream = asm.GetManifestResourceStream(resourceName)
            ?? throw new FileNotFoundException($"Embedded icon not found: {resourceName}");
        return new Icon(stream);
    }

    private void BuildUi()
    {
        _webView = new WebView2 { Dock = DockStyle.Fill };
        Controls.Add(_webView);
        _ = InitializeAsync();
    }

    private async Task InitializeAsync()
    {
        try
        {
            string dataFolder = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "GlideScroll", "WebView2");
            Directory.CreateDirectory(dataFolder);

            var env = await CoreWebView2Environment.CreateAsync(null, dataFolder);
            await _webView.EnsureCoreWebView2Async(env);

            var core = _webView.CoreWebView2;
            // DevTools only in Debug, never expose a console over the
            // privileged postMessage channel in a shipped build.
#if DEBUG
            core.Settings.AreDevToolsEnabled = true;
#else
            core.Settings.AreDevToolsEnabled = false;
#endif
            core.Settings.AreDefaultContextMenusEnabled = false;
            core.Settings.IsStatusBarEnabled = false;
            core.Settings.AreBrowserAcceleratorKeysEnabled = false;
            core.Settings.IsZoomControlEnabled = false;

            core.WebMessageReceived += OnWebMessage;
            core.NavigationCompleted += (_, e) =>
            {
                if (e.IsSuccess) _initialized = true;
            };

            core.NavigateToString(WebUI.Html);
        }
        catch (Exception ex)
        {
            MessageBox.Show(this,
                "WebView2 failed to initialize. Make sure the Microsoft Edge WebView2 Runtime is installed.\n\n" + ex.Message,
                "GlideScroll", MessageBoxButtons.OK, MessageBoxIcon.Error);
        }
    }

    private void PostToWeb(object payload)
    {
        if (_webView?.CoreWebView2 is null) return;
        try
        {
            string json = JsonSerializer.Serialize(payload, JsonOpts);
            _webView.CoreWebView2.PostWebMessageAsString(json);
        }
        catch { }
    }

    private void OnWebMessage(object? sender, CoreWebView2WebMessageReceivedEventArgs e)
    {
        try
        {
            string raw = e.TryGetWebMessageAsString();
            using var doc = JsonDocument.Parse(raw);
            var root = doc.RootElement;
            string type = root.GetProperty("type").GetString() ?? "";

            switch (type)
            {
                case "ready":              SendInit(); PushWindowState(WindowState == FormWindowState.Maximized); break;
                case "set":                HandleSet(root); break;
                case "apps-add":           HandleAppsAdd(root); break;
                case "apps-remove":        HandleAppsRemove(root); break;
                case "list-running-apps":  HandleListRunningApps(); break;
                case "apps-browse":        HandleAppsBrowse(); break;
                case "reset-wheel":        HandleResetWheel(); break;
                case "reset-pan":          HandleResetPan(); break;
                case "gamemode-add":       HandleGameModeAdd(root); break;
                case "gamemode-remove":    HandleGameModeRemove(root); break;
                case "gamemode-restore":   HandleGameModeRestore(); break;
                case "gamemode-browse":    HandleGameModeBrowse(); break;
                case "theme-edit":         HandleThemeEdit(root); break;
                case "theme-reset-current": HandleThemeResetCurrent(); break;
                case "optimize-monitor":   HandleOptimizeMonitor(); break;
                case "optimize-pan":       HandleOptimizePan(); break;
                case "restore-defaults":   HandleRestoreDefaults(root); break;
                case "capture-start":      HandleCaptureStart(root); break;
                case "open-url":           HandleOpenUrl(root); break;

                // Frameless title bar
                case "titlebar-drag":      HandleTitlebarDrag(); break;
                case "titlebar-resize":    HandleTitlebarResize(root); break;
                case "titlebar-min":       WindowState = FormWindowState.Minimized; break;
                case "titlebar-max":       ToggleMaximize(); break;
                case "titlebar-close":     Close(); break;
            }
        }
        catch { }
    }

    private void SendInit()
    {
        bool systemDark = SystemTheme.IsDarkMode();
        PostToWeb(new
        {
            type = "init",
            config = _config,
            activePalette = _config.Theme.ResolveActivePalette(systemDark),
            systemDark
        });
    }

    private void PushPalette()
    {
        bool systemDark = SystemTheme.IsDarkMode();
        PostToWeb(new
        {
            type = "palette",
            activePalette = _config.Theme.ResolveActivePalette(systemDark)
        });
    }

    // ----- editable themes -----
    // Every baked-in preset is user-editable. An edit applies to the theme the
    // user is LOOKING AT (with Auto that is whichever of Light/Dark is active)
    // and persists as an override; the Reset button drops only that theme's
    // override. Custom slots mutate directly.

    private void HandleThemeEdit(JsonElement root)
    {
        string slot = root.TryGetProperty("slot", out var s) ? (s.GetString() ?? "") : "";
        string value = root.TryGetProperty("value", out var v) ? (v.GetString() ?? "") : "";
        if (slot.Length == 0 || value.Length == 0) return;
        _config.Theme.EditActive(SystemTheme.IsDarkMode(), slot, value);
        ScheduleConfigSave();
        PushPalette();
    }

    private void HandleThemeResetCurrent()
    {
        _config.Theme.ResetCurrent(SystemTheme.IsDarkMode());
        _config.Save();
        PushPalette();
    }

    private void HandleSet(JsonElement root)
    {
        string key = root.GetProperty("key").GetString() ?? "";
        var valueEl = root.GetProperty("value");
        if (!ApplySettingByPath(key, valueEl)) return;

        if (key == "general.autostartWithWindows")
        {
            // Autostart writes registry, not config, so do it immediately.
            Autostart.SetEnabled(_config.General.AutostartWithWindows);
        }

        // Pipeline sees the change live; disk write coalesces.
        _pipeline.UpdateConfig(_config);
        ScheduleConfigSave();

        // Theme-affecting keys → recompute and push the active palette so the JS
        // applies CSS variables in real time without a full reload. (Per-color
        // edits arrive via the dedicated theme-edit message, not here.)
        if (key == "theme.appearance")
            PushPalette();
    }

    /// <summary>
    /// Debounced config persistence. Resets a 250ms quiet timer on each call;
    /// the actual JsonSerializer + File.WriteAllText runs only after no further
    /// changes for the window. Result: holding a spinner button no longer
    /// fsyncs the JSON 10x/sec.
    /// </summary>
    private void ScheduleConfigSave()
    {
        if (_saveDebounceTimer is null)
        {
            _saveDebounceTimer = new System.Windows.Forms.Timer { Interval = 250 };
            _saveDebounceTimer.Tick += (_, _) =>
            {
                _saveDebounceTimer!.Stop();
                try { _config.Save(); } catch { /* AppConfig.Save already swallows */ }
            };
        }
        _saveDebounceTimer.Stop();
        _saveDebounceTimer.Start();
    }

    /// <summary>Force the pending debounced save to disk now. Called on window
    /// close so a tight close-after-edit doesn't lose the last change.</summary>
    private void FlushPendingConfigSave()
    {
        if (_saveDebounceTimer is not null && _saveDebounceTimer.Enabled)
        {
            _saveDebounceTimer.Stop();
            try { _config.Save(); } catch { }
        }
    }

    private bool ApplySettingByPath(string path, JsonElement value)
    {
        try
        {
            switch (path)
            {
                // wheel
                case "wheel.enabled": _config.Wheel.Enabled = value.GetBoolean(); break;
                case "wheel.smoothingMode":
                    _config.Wheel.SmoothingMode = value.GetString() switch
                    {
                        "flywheel" => SmoothingMode.Flywheel,
                        "spring" => SmoothingMode.Spring,
                        "hybrid" => SmoothingMode.Hybrid,
                        "lowLatency" => SmoothingMode.LowLatency,
                        "silk" => SmoothingMode.Silk,
                        "cadence" => SmoothingMode.Cadence,
                        _ => SmoothingMode.Silk
                    };
                    break;
                case "wheel.stepSize": _config.Wheel.StepSize = Math.Clamp(value.GetInt32(), 10, 1000); break;
                case "wheel.animationTimeMs": _config.Wheel.AnimationTimeMs = Math.Clamp(value.GetInt32(), 50, 2000); break;
                case "wheel.feelBias": _config.Wheel.FeelBias = Math.Clamp(value.GetDouble(), -10.0, 10.0); break;
                case "wheel.accelWindowMs": _config.Wheel.AccelWindowMs = Math.Clamp(value.GetInt32(), 0, 1000); break;
                case "wheel.accelBoost": _config.Wheel.AccelBoost = Math.Clamp(value.GetDouble(), 1.0, 20.0); break;
                case "wheel.shiftWheelHorizontal": _config.Wheel.ShiftWheelHorizontal = value.GetBoolean(); break;
                case "wheel.smoothHorizontal": _config.Wheel.SmoothHorizontal = value.GetBoolean(); break;
                case "wheel.reverseDirection": _config.Wheel.ReverseDirection = value.GetBoolean(); break;

                // pan
                case "pan.enabled": _config.Pan.Enabled = value.GetBoolean(); break;
                case "pan.mode":
                    _config.Pan.Mode = value.GetString() switch
                    {
                        "feather" => PanMode.Feather,
                        "cushion" => PanMode.Cushion,
                        _ => PanMode.Feather
                    };
                    break;
                case "pan.friction": _config.Pan.Friction = Math.Clamp(value.GetDouble(), 1.0, 200.0); break;
                case "pan.sensitivityX": _config.Pan.SensitivityX = Math.Clamp(value.GetDouble(), 1.0, 500.0); break;
                case "pan.sensitivityY": _config.Pan.SensitivityY = Math.Clamp(value.GetDouble(), 1.0, 500.0); break;
                case "pan.activationThresholdPixels": _config.Pan.ActivationThresholdPixels = Math.Clamp(value.GetInt32(), 0, 50); break;
                case "pan.minStepPixels": _config.Pan.MinStepPixels = Math.Clamp(value.GetInt32(), 1, 50); break;
                case "pan.smoothness": _config.Pan.Smoothness = Math.Clamp(value.GetDouble(), 0.0, 20.0); break;
                case "pan.flickMode": _config.Pan.FlickMode = value.GetBoolean(); break;
                case "pan.lockCursorDuringPan": _config.Pan.LockCursorDuringPan = value.GetBoolean(); break;
                case "pan.snapToAxis": _config.Pan.SnapToAxis = value.GetBoolean(); break;
                case "pan.reverseDirection": _config.Pan.ReverseDirection = value.GetBoolean(); break;

                // game mode
                case "gameMode.enabled": _config.GameMode.Enabled = value.GetBoolean(); break;

                // apps
                case "apps.mode":
                    _config.Apps.Mode = value.GetString() == "inclusion"
                        ? AppListMode.Inclusion : AppListMode.Exclusion;
                    break;

                // theme appearance (ParseAppearance also maps legacy v1.2 names)
                case "theme.appearance":
                    _config.Theme.Appearance = ThemeConfig.ParseAppearance(value.GetString() ?? "auto");
                    break;

                // general
                case "general.autostartWithWindows": _config.General.AutostartWithWindows = value.GetBoolean(); break;
                case "general.minimizeToTrayOnClose": _config.General.MinimizeToTrayOnClose = value.GetBoolean(); break;
                case "general.showNotificationOnModeToggle": _config.General.ShowNotificationOnModeToggle = value.GetBoolean(); break;
                case "general.screenDiagonalInches":
                    _config.General.ScreenDiagonalInches = Math.Clamp(value.GetDouble(), 10.0, 65.0);
                    break;
                case "general.supporterName": _config.General.SupporterName = (value.GetString() ?? "").Trim(); break;
                case "general.supporterOrder": _config.General.SupporterOrder = (value.GetString() ?? "").Trim(); break;

                default: return false;
            }
            return true;
        }
        catch { return false; }
    }

    private void HandleAppsAdd(JsonElement root)
    {
        string exe = root.GetProperty("exe").GetString() ?? "";
        if (string.IsNullOrEmpty(exe)) return;
        var list = _config.Apps.Mode == AppListMode.Exclusion
            ? _config.Apps.Blacklist : _config.Apps.Whitelist;
        if (!list.Any(n => string.Equals(n, exe, StringComparison.OrdinalIgnoreCase)))
            list.Add(exe);
        _config.Save();
        _pipeline.UpdateConfig(_config);
    }

    private void HandleAppsRemove(JsonElement root)
    {
        string exe = root.GetProperty("exe").GetString() ?? "";
        if (string.IsNullOrEmpty(exe)) return;
        var list = _config.Apps.Mode == AppListMode.Exclusion
            ? _config.Apps.Blacklist : _config.Apps.Whitelist;
        list.RemoveAll(n => string.Equals(n, exe, StringComparison.OrdinalIgnoreCase));
        _config.Save();
        _pipeline.UpdateConfig(_config);
    }

    /// <summary>Restore-defaults choice from the modal. "reset" wipes the active
    /// list back to its factory state (Exclusion list ships the default class
    /// blocks; Inclusion list ships empty). "readd" only re-adds any missing
    /// default blocks and leaves every user entry intact.</summary>
    private void HandleRestoreDefaults(JsonElement root)
    {
        string mode = root.TryGetProperty("mode", out var m) ? (m.GetString() ?? "") : "";
        var apps = _config.Apps;
        var list = apps.Mode == AppListMode.Exclusion ? apps.Blacklist : apps.Whitelist;

        if (mode == "reset")
        {
            list.Clear();
            if (apps.Mode == AppListMode.Exclusion)
                list.AddRange(AppListConfig.DefaultBlocks);
        }
        else // "readd"
        {
            if (apps.Mode == AppListMode.Exclusion)
            {
                foreach (var b in AppListConfig.DefaultBlocks)
                    if (!list.Any(e => string.Equals(e, b, StringComparison.OrdinalIgnoreCase)))
                        list.Add(b);
            }
        }

        apps.DefaultsSeeded = true; // an explicit restore counts as seeded
        _config.Save();
        _pipeline.UpdateConfig(_config);
        PostToWeb(new { type = "apps-list-changed", apps = _config.Apps });
    }

    // ----- Finder tool (Add running app + Add class) -----
    //
    // Two entry points share one capture engine, distinguished by "kind":
    //   kind "app"   (Add running app picker)  -> resolves the window and adds
    //                its EXE, except for the curated earmark classes (File
    //                Explorer, Photo Viewer) which are added as class rules.
    //   kind "class" (Add class advanced modal) -> always resolves the class.
    // The user presses the crosshair (JS "capture-start") and drags over any
    // window. We hold the mouse capture on this form so stray button events
    // don't leak to the target, then poll on a UI timer: GetCursorPos ->
    // WindowFromPoint -> root class/exe, plus the physical left-button state.
    // Physical pixels (not JS screen coords) keep it correct under any DPI. A
    // native follow-cursor tooltip shows what will be added. On release we
    // commit the last non-own window.
    private System.Windows.Forms.Timer? _captureTimer;
    private CaptureTooltip? _captureTip;
    private string _captureKind = "class";
    private long _captureStartMs;
    private bool _captureSawButtonDown;
    private string _pendingEntry = "";   // "notepad.exe" or "class:CabinetWClass"
    private string _pendingLabel = "";   // tooltip label, e.g. "Notepad, exe"
    private string _pendingCls = "";
    private string _pendingApp = "";
    private bool _pendingOk = false;

    /// <summary>Opens the buy page in the system browser. Deliberately not a
    /// general-purpose URL opener: only the one known URL is accepted, so a
    /// compromised page script can't launch arbitrary targets.</summary>
    private void HandleOpenUrl(JsonElement root)
    {
        string url = root.TryGetProperty("url", out var u) ? (u.GetString() ?? "") : "";
        if (url != "https://portghost.com/buy")
        {
            System.Diagnostics.Debug.WriteLine($"open-url rejected: {url}");
            return;
        }
        try
        {
            System.Diagnostics.Process.Start(
                new System.Diagnostics.ProcessStartInfo(url) { UseShellExecute = true });
        }
        catch { /* no browser available; nothing sensible to do */ }
    }

    private void HandleCaptureStart(JsonElement root)
    {
        string kind = root.TryGetProperty("kind", out var k) ? (k.GetString() ?? "class") : "class";
        if (InvokeRequired) { BeginInvoke(() => StartCapture(kind)); return; }
        StartCapture(kind);
    }

    private void StartCapture(string kind)
    {
        StopCapture();
        _captureKind = kind;
        _captureStartMs = Environment.TickCount64;
        _captureSawButtonDown = false;
        _pendingEntry = ""; _pendingLabel = ""; _pendingCls = ""; _pendingApp = ""; _pendingOk = false;
        _captureTip ??= new CaptureTooltip();
        Win32.SetCapture(Handle);

        _captureTimer = new System.Windows.Forms.Timer { Interval = 30 };
        _captureTimer.Tick += (_, _) => CaptureTick();
        _captureTimer.Start();
    }

    private void CaptureTick()
    {
        // Safety valve: never poll forever if a button-up is somehow missed.
        if (Environment.TickCount64 - _captureStartMs > 12000) { FinishCapture(false); return; }

        bool lbDown = (Win32.GetAsyncKeyState(Win32.VK_LBUTTON) & 0x8000) != 0;
        if (lbDown) _captureSawButtonDown = true;

        if (lbDown)
        {
            Win32.GetCursorPos(out Win32.POINT p);
            IntPtr root = Win32.CursorRootWindow();
            Win32.GetWindowThreadProcessId(root, out uint rootPid);
            // Ignore any window from our own process (the settings window, its
            // modals, and the tooltip) so we only ever capture a real target.
            if (root == IntPtr.Zero || rootPid == (uint)Environment.ProcessId)
            {
                _pendingOk = false;
                _captureTip?.ShowAt("Drag onto another window…", p.X, p.Y);
            }
            else
            {
                string cls = Win32.ClassNameOf(root);
                string exe = rootPid != 0 ? Win32.GetProcessExeName(rootPid) : "";
                ComputePending(cls, exe);
                _captureTip?.ShowAt(_pendingOk ? "Will add: " + _pendingLabel : "Can't target this window", p.X, p.Y);
            }
        }
        else if (_captureSawButtonDown)
        {
            // Button released after a drag: commit the last window we saw.
            FinishCapture(true);
        }
    }

    /// <summary>Decide, for the current kind, what a given window resolves to.</summary>
    private void ComputePending(string cls, string exe)
    {
        _pendingCls = cls;
        _pendingApp = WindowTargeting.FriendlyAppName(exe);
        (_pendingEntry, _pendingLabel, _pendingOk) = WindowTargeting.Resolve(_captureKind, cls, exe);
    }

    private void FinishCapture(bool released)
    {
        StopCapture();
        bool ok = released && _pendingOk && _pendingEntry.Length > 0;
        if (_captureKind == "class")
        {
            if (ok)
                PostToWeb(new { type = "capture-result", kind = "class", ok = true, cls = _pendingCls, app = _pendingApp });
            else
                PostToWeb(new { type = "capture-result", kind = "class", ok = false, note = "No window captured. Try again." });
        }
        else
        {
            // "app" and "game" share the exe-entry result shape; the JS side
            // routes on kind (app -> active list, game -> Game Mode list).
            if (ok)
                PostToWeb(new { type = "capture-result", kind = _captureKind, ok = true, entry = _pendingEntry, label = _pendingLabel });
            else
                PostToWeb(new { type = "capture-result", kind = _captureKind, ok = false });
        }
    }

    private void StopCapture()
    {
        if (_captureTimer is not null)
        {
            _captureTimer.Stop();
            _captureTimer.Dispose();
            _captureTimer = null;
            try { Win32.ReleaseCapture(); } catch { }
        }
        _captureTip?.HideTip();
    }

    private void HandleListRunningApps()
    {
        // Enumeration walks every visible window and can hit the loader lock
        // (FileVersionInfo / MainModule). Run it off the UI thread so it can't
        // stall the message pump that services the low-level mouse hook.
        _ = System.Threading.Tasks.Task.Run(() =>
        {
            List<RunningApp> apps;
            try { apps = WindowEnumerator.EnumerateVisibleApps(60); }
            catch { apps = new List<RunningApp>(); }
            var payload = apps.Select(a => new { exeName = a.ExeName, displayName = a.DisplayName }).ToArray();
            try { BeginInvoke(new Action(() => PostToWeb(new { type = "running-apps", apps = payload }))); }
            catch { /* form closing, ignore */ }
        });
    }

    private void HandleAppsBrowse()
    {
        if (InvokeRequired) { Invoke(HandleAppsBrowse); return; }

        using var dlg = new OpenFileDialog
        {
            Filter = "Executables (*.exe)|*.exe|All files (*.*)|*.*",
            Title = "Select an executable"
        };
        if (dlg.ShowDialog(this) != DialogResult.OK) return;

        string name = Path.GetFileName(dlg.FileName);
        if (string.IsNullOrEmpty(name)) return;

        var list = _config.Apps.Mode == AppListMode.Exclusion
            ? _config.Apps.Blacklist : _config.Apps.Whitelist;
        if (!list.Any(n => string.Equals(n, name, StringComparison.OrdinalIgnoreCase)))
            list.Add(name);
        _config.Save();
        _pipeline.UpdateConfig(_config);

        PostToWeb(new { type = "apps-list-changed", apps = _config.Apps });
    }

    private void HandleResetWheel()
    {
        var defaults = new WheelConfig { Enabled = _config.Wheel.Enabled };
        _config.Wheel = defaults;
        _config.Save();
        _pipeline.UpdateConfig(_config);
        PushFullConfig();
    }

    // ----- Game Mode list management -----
    // Same mutate + save + live-push pattern as the app rules handlers. The
    // gate inside the pipeline re-reads the list on UpdateConfig, so an edit
    // takes effect on the next input event.

    private void PushGameModeChanged()
    {
        _config.Save();
        _pipeline.UpdateConfig(_config);
        PostToWeb(new { type = "gamemode-changed", gameMode = _config.GameMode });
    }

    private void HandleGameModeAdd(JsonElement root)
    {
        string exe = root.TryGetProperty("exe", out var e) ? (e.GetString() ?? "") : "";
        exe = exe.Trim();
        if (exe.Length == 0) return;
        if (!_config.GameMode.Games.Any(g => string.Equals(g, exe, StringComparison.OrdinalIgnoreCase)))
        {
            _config.GameMode.Games.Add(exe);
            PushGameModeChanged();
        }
    }

    private void HandleGameModeRemove(JsonElement root)
    {
        string exe = root.TryGetProperty("exe", out var e) ? (e.GetString() ?? "") : "";
        int idx = _config.GameMode.Games.FindIndex(g => string.Equals(g, exe, StringComparison.OrdinalIgnoreCase));
        if (idx >= 0)
        {
            _config.GameMode.Games.RemoveAt(idx);
            PushGameModeChanged();
        }
    }

    private void HandleGameModeRestore()
    {
        // Re-add any missing seeds; user additions are kept.
        bool changed = false;
        foreach (var g in GameModeConfig.DefaultGames)
        {
            if (!_config.GameMode.Games.Any(e => string.Equals(e, g, StringComparison.OrdinalIgnoreCase)))
            {
                _config.GameMode.Games.Add(g);
                changed = true;
            }
        }
        if (changed) PushGameModeChanged();
    }

    private void HandleGameModeBrowse()
    {
        if (InvokeRequired) { Invoke(HandleGameModeBrowse); return; }

        using var dlg = new OpenFileDialog
        {
            Filter = "Executables (*.exe)|*.exe|All files (*.*)|*.*",
            Title = "Select a game executable"
        };
        if (dlg.ShowDialog(this) != DialogResult.OK) return;

        string exe = Path.GetFileName(dlg.FileName);
        if (exe.Length == 0) return;
        if (!_config.GameMode.Games.Any(g => string.Equals(g, exe, StringComparison.OrdinalIgnoreCase)))
        {
            _config.GameMode.Games.Add(exe);
            PushGameModeChanged();
        }
    }

    private void HandleResetPan()
    {
        var defaults = new PanConfig { Enabled = _config.Pan.Enabled };
        _config.Pan = defaults;
        _config.Save();
        _pipeline.UpdateConfig(_config);
        PushFullConfig();
    }

    private void HandleOptimizeMonitor()
    {
        int hz = DisplayInfo.GetPrimaryRefreshRate();
        var (w, h) = DisplayInfo.GetPrimaryResolution();
        double diagonal = _config.General.ScreenDiagonalInches;
        var mode = _config.Wheel.SmoothingMode;
        var (animMs, step, ppi) = DisplayInfo.RecommendedFor(hz, w, h, diagonal, mode);
        _config.Wheel.AnimationTimeMs = animMs;
        _config.Wheel.StepSize = step;
        // FeelBias 0 is the per-mode "ideal" point on the new -10..10 scale:
        // critical damping for Spring (no bounce), balanced 50/50 split for
        // Hybrid. Flywheel and LowLatency do not read it (slider is hidden).
        _config.Wheel.FeelBias = 0.0;
        _config.Save();
        _pipeline.UpdateConfig(_config);

        PushFullConfig();
        PostToWeb(new
        {
            type = "optimize-result",
            refreshRate = hz,
            animationTimeMs = animMs,
            stepSize = step,
            widthPx = w,
            heightPx = h,
            diagonalInches = diagonal,
            ppi = (int)Math.Round(ppi),
            mode = mode.ToString()
        });
    }

    private void HandleOptimizePan()
    {
        var (w, h) = DisplayInfo.GetPrimaryResolution();
        double diagonal = _config.General.ScreenDiagonalInches;
        var (sensitivity, activation, minStep, ppi) = DisplayInfo.RecommendedPanFor(w, h, diagonal);

        // Both axes get the same PPI-scaled sensitivity. Users can still
        // manually skew X vs Y after Optimize if they prefer asymmetric.
        _config.Pan.SensitivityX = sensitivity;
        _config.Pan.SensitivityY = sensitivity;
        _config.Pan.ActivationThresholdPixels = activation;
        _config.Pan.MinStepPixels = minStep;
        // Friction intentionally not touched, it's a time-based feel preference.

        _config.Save();
        _pipeline.UpdateConfig(_config);
        PushFullConfig();

        PostToWeb(new
        {
            type = "pan-optimize-result",
            sensitivity,
            activation,
            minStep,
            widthPx = w,
            heightPx = h,
            diagonalInches = diagonal,
            ppi = (int)Math.Round(ppi)
        });
    }

    private void PushFullConfig()
    {
        bool systemDark = SystemTheme.IsDarkMode();
        PostToWeb(new
        {
            type = "config-update",
            config = _config,
            activePalette = _config.Theme.ResolveActivePalette(systemDark)
        });
    }

    // ----- Frameless title bar handlers -----
    //
    // The HTML title bar can't drag or resize the window directly, WebView2
    // is a child HWND, so mouse events there don't reach the form's WndProc.
    // The standard fix is to message C# on mousedown, then call the OS native
    // window-drag / window-resize entry point: ReleaseCapture() followed by
    // SendMessage(WM_NCLBUTTONDOWN, HT*, ...). Windows then drives the move
    // or resize loop natively, so Aero Snap, double-click-to-maximize, and
    // edge snapping all work for free.

    private void HandleTitlebarDrag()
    {
        if (WindowState == FormWindowState.Maximized) return;
        Win32.ReleaseCapture();
        Win32.SendMessage(Handle, Win32.WM_NCLBUTTONDOWN,
            (IntPtr)Win32.HTCAPTION, IntPtr.Zero);
    }

    private void HandleTitlebarResize(JsonElement root)
    {
        if (WindowState == FormWindowState.Maximized) return;
        if (!root.TryGetProperty("edge", out var edgeEl)) return;
        string edge = edgeEl.GetString() ?? "";
        int htCode = edge switch
        {
            "n"  => Win32.HTTOP,
            "s"  => Win32.HTBOTTOM,
            "w"  => Win32.HTLEFT,
            "e"  => Win32.HTRIGHT,
            "nw" => Win32.HTTOPLEFT,
            "ne" => Win32.HTTOPRIGHT,
            "sw" => Win32.HTBOTTOMLEFT,
            "se" => Win32.HTBOTTOMRIGHT,
            _    => 0
        };
        if (htCode == 0) return;
        Win32.ReleaseCapture();
        Win32.SendMessage(Handle, Win32.WM_NCLBUTTONDOWN,
            (IntPtr)htCode, IntPtr.Zero);
    }

    private void ToggleMaximize()
    {
        WindowState = WindowState == FormWindowState.Maximized
            ? FormWindowState.Normal
            : FormWindowState.Maximized;
    }

    protected override void OnFormClosing(FormClosingEventArgs e)
    {
        // Flush any debounced save before going away (minimizing to tray
        // doesn't strictly need this since the timer keeps running while
        // hidden, but it ensures the last change is durable if the user
        // exits the process via tray Exit shortly after).
        FlushPendingConfigSave();
        StopCapture();

        if (_config.General.MinimizeToTrayOnClose && e.CloseReason == CloseReason.UserClosing)
        {
            e.Cancel = true;
            Hide();
        }
        else base.OnFormClosing(e);
    }

    /// <summary>Re-broadcast system theme so Auto-mode follows changes without restart.</summary>
    public void RefreshSystemTheme()
    {
        if (!_initialized) return;
        bool dark = SystemTheme.IsDarkMode();
        PostToWeb(new { type = "system-theme", systemDark = dark });
        // If appearance is Auto, recompute and push the palette too.
        if (_config.Theme.Appearance == ThemeAppearance.Auto) PushPalette();
    }

    /// <summary>Push the current apps list into the WebView so the visible
    /// blacklist/whitelist refreshes. Called by TrayContext after the user
    /// adds an app via the tray menu while the settings window is open.</summary>
    public void NotifyAppsListChangedFromTray()
    {
        if (!_initialized) return;
        PostToWeb(new { type = "apps-list-changed", apps = _config.Apps });
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            StopCapture();
            _captureTip?.Dispose();
            _saveDebounceTimer?.Dispose();
        }
        base.Dispose(disposing);
    }

    private static readonly JsonSerializerOptions JsonOpts = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        Converters = { new System.Text.Json.Serialization.JsonStringEnumConverter(JsonNamingPolicy.CamelCase) }
    };
}
