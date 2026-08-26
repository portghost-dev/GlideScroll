using GlideScroll.Config;
using GlideScroll.Native;

namespace GlideScroll.Pipeline;

/// <summary>
/// Top-level pipeline: receives MouseEvents from MouseHook, routes wheel
/// events to the active <see cref="ISmoother"/> implementation (chosen by
/// WheelConfig.SmoothingMode) and middle-button events through PanStateMachine
/// + PanController. A dedicated background thread drives both pipelines at
/// the display refresh rate (60-360 Hz) so synthetic events are emitted
/// smoothly even when the UI thread
/// is busy.
///
/// Smoother instances are kept alive across config changes, switching modes
/// only swaps which one is "active" and Resets() the prior to drop in-flight
/// state. Tunables flow into all of them each ApplyConfig so the user can
/// switch modes mid-session and feel the new mode immediately.
/// </summary>
public sealed class ScrollPipeline : IDisposable
{
    private readonly MouseHook _hook;
    // Two parallel smoother sets, vertical and horizontal axes have completely
    // independent state. Same SmoothingMode applies to both axes (one mode
    // selector controls everything). Pipeline picks active V smoother and
    // active H smoother from the matching dictionary.
    private readonly Dictionary<SmoothingMode, ISmoother> _smoothersV;
    private readonly Dictionary<SmoothingMode, ISmoother> _smoothersH;
    private ISmoother _wheel;        // active vertical smoother
    private ISmoother _wheelH;       // active horizontal smoother
    private SmoothingMode _activeMode;
    private readonly PanController _pan;
    private readonly PanStateMachine _state;

    // Game Mode auto-pause. Consulted per wheel event and at pan start with
    // the event's own screen point; also polled per tick while motion is in
    // flight so a coast or glide can't dribble into a game window.
    private readonly GamePauseGate _gameGate;

    private readonly Thread _timerThread;
    private volatile bool _running;

    // volatile: read by the hook thread (HandleEvent) and the timer thread
    // (TimerLoop), written by the UI thread (UpdateConfig). Marking volatile
    // ensures readers always see the most recent reference rather than a
    // stale cache. (Per-smoother UpdateConfig now also takes its own lock
    // for tunable mutation, see Smoothers.cs.)
    private volatile AppConfig _config;

    // Window-class rules extracted from the active app list (blacklist in
    // Exclusion mode, whitelist in Inclusion mode). Class entries are stored as
    // "class:NAME" and matched against the ROOT window class under the cursor.
    // Rebuilt on every ApplyConfig; the reference is swapped atomically so the
    // hook thread always reads a complete set without locking. Empty set means
    // the per-event WindowFromPoint probe is skipped entirely.
    private volatile HashSet<string> _activeClassSet = new(StringComparer.OrdinalIgnoreCase);

    private string _foregroundExe = string.Empty;
    // Cached membership of the foreground exe in the active list's exe entries.
    // Refreshed at most every 150ms so the per-event hot path doesn't re-scan
    // the list on every mouse event.
    private bool _foregroundExeInList = false;
    private long _lastForegroundCheckMs = -1000;

    // Cached "is the root window class under the cursor one of the active class
    // rules". Cached ~120ms because the cursor is effectively stationary over
    // the scroll target during a gesture and WindowFromPoint+GetClassName per
    // wheel event would tax the hook thread.
    private bool _cursorClassInList = false;
    private long _lastCursorClassCheckMs = -1000;
    private readonly System.Diagnostics.Stopwatch _stopwatch = System.Diagnostics.Stopwatch.StartNew();

    // Tray "click to target" finder. When armed, the next left-click is
    // intercepted here: we resolve the window under the cursor, stash its class
    // + exe for the tray to pick up, and suppress the click so it never reaches
    // the app being pointed at. Right-click cancels. All volatile: written by
    // the hook thread, armed/read by the UI thread. Idle cost is one bool test.
    private volatile bool _targeting;
    private volatile bool _swallowNextUp;
    private volatile bool _picked;
    private volatile string _pickedCls = string.Empty;
    private volatile string _pickedExe = string.Empty;

    public ScrollPipeline(AppConfig config)
    {
        _config = config;
        _smoothersV = new Dictionary<SmoothingMode, ISmoother>
        {
            [SmoothingMode.Flywheel]   = new FlywheelSmoother(isHorizontal: false),
            [SmoothingMode.Spring]     = new SpringSmoother(isHorizontal: false),
            [SmoothingMode.Hybrid]     = new HybridSmoother(isHorizontal: false),
            [SmoothingMode.LowLatency] = new LowLatencySmoother(isHorizontal: false),
            [SmoothingMode.Silk]       = new SilkSmoother(isHorizontal: false),
            [SmoothingMode.Cadence]    = new CadenceSmoother(isHorizontal: false),
        };
        _smoothersH = new Dictionary<SmoothingMode, ISmoother>
        {
            [SmoothingMode.Flywheel]   = new FlywheelSmoother(isHorizontal: true),
            [SmoothingMode.Spring]     = new SpringSmoother(isHorizontal: true),
            [SmoothingMode.Hybrid]     = new HybridSmoother(isHorizontal: true),
            [SmoothingMode.LowLatency] = new LowLatencySmoother(isHorizontal: true),
            [SmoothingMode.Silk]       = new SilkSmoother(isHorizontal: true),
            [SmoothingMode.Cadence]    = new CadenceSmoother(isHorizontal: true),
        };
        _activeMode = _config.Wheel.SmoothingMode;
        _wheel  = _smoothersV[_activeMode];
        _wheelH = _smoothersH[_activeMode];

        _pan = new PanController();
        _state = new PanStateMachine(_pan);
        _gameGate = new GamePauseGate();
        _hook = new MouseHook(HandleEvent);

        ApplyConfig();

        _timerThread = new Thread(TimerLoop)
        {
            IsBackground = true,
            Name = "GlideScroll-Pipeline",
            Priority = ThreadPriority.AboveNormal
        };
    }

    private bool _timerResolutionRaised;

    public void Start()
    {
        // Raise system timer resolution to 1ms so the pipeline thread can sleep
        // close to its 6.66ms target tick instead of overshooting by ~15ms.
        // Reduces CPU spent in SpinWait without sacrificing emit timing.
        try { Win32.TimeBeginPeriod(1); _timerResolutionRaised = true; } catch { }

        _hook.Install();
        _running = true;
        _gameGate.Start();
        _timerThread.Start();
    }

    public void Stop()
    {
        _running = false;
        _gameGate.Stop();
        _hook.Uninstall();
        if (_timerThread.IsAlive)
            _timerThread.Join(500);

        if (_timerResolutionRaised)
        {
            try { Win32.TimeEndPeriod(1); } catch { }
            _timerResolutionRaised = false;
        }
    }

    public void UpdateConfig(AppConfig config)
    {
        _config = config;
        ApplyConfig();
    }

    private void ApplyConfig()
    {
        // Push tunables into ALL smoothers (both axes) so switching modes
        // mid-session immediately reflects the user's current sliders.
        foreach (var s in _smoothersV.Values) s.UpdateConfig(_config.Wheel);
        foreach (var s in _smoothersH.Values) s.UpdateConfig(_config.Wheel);

        _gameGate.OnConfigChanged(_config);

        // Switch active smoothers if user changed mode. Reset both old and new
        // (each axis) so leftover motion doesn't leak across the handoff.
        if (_config.Wheel.SmoothingMode != _activeMode)
        {
            _wheel.Reset();
            _wheelH.Reset();
            _activeMode = _config.Wheel.SmoothingMode;
            _wheel  = _smoothersV[_activeMode];
            _wheelH = _smoothersH[_activeMode];
            _wheel.Reset();
            _wheelH.Reset();
        }

        _pan.Mode = _config.Pan.Mode;
        _pan.Friction = _config.Pan.Friction;
        _pan.SensitivityX = _config.Pan.SensitivityX;
        _pan.SensitivityY = _config.Pan.SensitivityY;
        _pan.MinStepPixels = _config.Pan.MinStepPixels;
        _pan.Smoothness = _config.Pan.Smoothness;
        _pan.FlickMode = _config.Pan.FlickMode;
        _pan.SnapToAxis = _config.Pan.SnapToAxis;
        _pan.ReverseDirection = _config.Pan.ReverseDirection;

        _state.ActivationThreshold = _config.Pan.ActivationThresholdPixels;
        _state.LockCursor = _config.Pan.LockCursorDuringPan;

        // Re-derive the window-class rules from the (possibly changed) active
        // list + mode, then invalidate the per-event caches so a just-edited
        // rule takes effect on the next event instead of after the cache TTL.
        var apps = _config.Apps;
        var active = apps.Mode == AppListMode.Exclusion ? apps.Blacklist : apps.Whitelist;
        var classSet = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var entry in active)
        {
            if (entry.Length > 6 && entry.StartsWith("class:", StringComparison.OrdinalIgnoreCase))
                classSet.Add(entry.Substring(6));
        }
        _activeClassSet = classSet;
        _lastForegroundCheckMs = -1000;
        _lastCursorClassCheckMs = -1000;
    }

    private HookResult HandleEvent(MouseEvent ev)
    {
        // Tray click-to-target finder: when armed, the next left-click picks the
        // window under the cursor and is swallowed so it never reaches that app.
        // Right-click cancels. The matching button-up is swallowed too.
        if (_targeting)
        {
            if (ev.Type == MouseEventType.LeftDown)
            {
                CaptureTargetClick();
                _swallowNextUp = true;
                return HookResult.Suppress;
            }
            if (ev.Type == MouseEventType.RightDown)
            {
                _targeting = false;
                _swallowNextUp = true;
                return HookResult.Suppress;
            }
        }
        if (_swallowNextUp && (ev.Type == MouseEventType.LeftUp || ev.Type == MouseEventType.RightUp))
        {
            _swallowNextUp = false;
            return HookResult.Suppress;
        }

        // Non-wheel events (pan, coast-cancel) gate on the exe include/exclude
        // list only. But NOT while a pan interaction is already in flight, or a
        // mid-pan foreground change (Alt-Tab to a listed app) would strand the
        // state machine in Panning with the cursor pinned. Wheel events
        // additionally consult the window-class rules, handled per-case below.
        bool isWheel = ev.Type is MouseEventType.Wheel or MouseEventType.HWheel;
        if (!isWheel && !_state.IsActive && !ExeAffectsForeground())
            return HookResult.PassThrough;

        switch (ev.Type)
        {
            case MouseEventType.Wheel:
                if (!_config.Wheel.Enabled) return HookResult.PassThrough;
                if (ev.WheelDelta == 0) return HookResult.PassThrough;
                // App rules decide whether this wheel event is smoothed: the exe
                // include/exclude list plus window-class rules (File Explorer,
                // Photo Viewer, and anything the user added). A blocked surface
                // passes straight through to native scrolling, the best result
                // when the target drops sub-notch injected wheel.
                if (!ShouldSmoothWheel(ev.X, ev.Y)) return HookResult.PassThrough;
                {
                    int dir = ev.WheelDelta > 0 ? 1 : -1;
                    if (_config.Wheel.ReverseDirection) dir = -dir;

                    // Shift+wheel = horizontal: when the user holds Shift while
                    // scrolling vertically, treat the notch as horizontal. If
                    // SmoothHorizontal is on, route through the active horizontal
                    // smoother for matching feel; otherwise emit a raw horizontal
                    // notch directly so apps that handle hwheel see something.
                    if (_config.Wheel.ShiftWheelHorizontal && Win32.IsShiftDown())
                    {
                        if (_config.Wheel.SmoothHorizontal)
                            _wheelH.EnqueueNotch(dir);
                        else
                            InputSynth.SendHorizontalWheel(dir * 120);
                        return HookResult.Suppress;
                    }

                    _wheel.EnqueueNotch(dir);
                }
                return HookResult.Suppress;

            case MouseEventType.HWheel:
                if (!_config.Wheel.Enabled) return HookResult.PassThrough;
                if (!_config.Wheel.SmoothHorizontal) return HookResult.PassThrough;
                if (ev.WheelDelta == 0) return HookResult.PassThrough;
                if (!ShouldSmoothWheel(ev.X, ev.Y)) return HookResult.PassThrough;
                {
                    int hdir = ev.WheelDelta > 0 ? 1 : -1;
                    if (_config.Wheel.ReverseDirection) hdir = -hdir;
                    _wheelH.EnqueueNotch(hdir);
                }
                return HookResult.Suppress;

            case MouseEventType.MiddleDown:
                if (!_config.Pan.Enabled) return HookResult.PassThrough;
                // Game Mode: never start a pan on a game window. The press
                // point is the pan target, so the decision anchors there.
                if (_gameGate.ShouldPause(ev.X, ev.Y)) return HookResult.PassThrough;
                return _state.OnMiddleDown(ev.X, ev.Y);

            case MouseEventType.MiddleUp:
                // Always finish an in-flight interaction, even if Pan was just
                // disabled, so the state machine releases instead of stranding.
                if (!_state.IsActive) return HookResult.PassThrough;
                return _state.OnMiddleUp(ev.X, ev.Y);

            case MouseEventType.Move:
                if (!_state.IsActive) return HookResult.PassThrough;
                return _state.OnMove(ev.X, ev.Y);

            case MouseEventType.LeftDown:
            case MouseEventType.LeftUp:
            case MouseEventType.RightDown:
            case MouseEventType.RightUp:
            case MouseEventType.XDown:
            case MouseEventType.XUp:
                // Any other button input cancels coast.
                _state.CancelCoast();
                return HookResult.PassThrough;

            default:
                return HookResult.PassThrough;
        }
    }

    /// <summary>
    /// Refresh the cached foreground exe + its membership in the active list's
    /// exe entries. Both are cached for 150ms so the hot path per event is one
    /// stopwatch read + one bool read in the steady state.
    /// </summary>
    private void RefreshForegroundExeIfStale()
    {
        long now = _stopwatch.ElapsedMilliseconds;
        if (now - _lastForegroundCheckMs > 150)
        {
            _lastForegroundCheckMs = now;
            _foregroundExe = ResolveForegroundExe();
            _foregroundExeInList = ExeInActiveList(_foregroundExe);
        }
    }

    private bool ExeInActiveList(string exe)
    {
        if (string.IsNullOrEmpty(exe)) return false;
        // Concrete List<string> + index loop: no LINQ closure/enumerator
        // allocation on the hook thread. "class:" entries never match an exe
        // (exe filenames can't contain a colon) so they're skipped for free.
        var apps = _config.Apps;
        var list = apps.Mode == AppListMode.Exclusion ? apps.Blacklist : apps.Whitelist;
        for (int i = 0; i < list.Count; i++)
        {
            if (string.Equals(list[i], exe, StringComparison.OrdinalIgnoreCase))
                return true;
        }
        return false;
    }

    /// <summary>
    /// Cached check of whether the root window class under the cursor is one of
    /// the active class rules. Re-resolved at most every ~120ms; when there are
    /// no class rules the WindowFromPoint probe is skipped entirely.
    /// </summary>
    private bool CursorClassInList()
    {
        var set = _activeClassSet;
        if (set.Count == 0) return false;
        long now = _stopwatch.ElapsedMilliseconds;
        if (now - _lastCursorClassCheckMs > 120)
        {
            _lastCursorClassCheckMs = now;
            try
            {
                string cls = Win32.CursorWindowRootClass();
                _cursorClassInList = cls.Length > 0 && set.Contains(cls);
            }
            catch { _cursorClassInList = false; }
        }
        return _cursorClassInList;
    }

    /// <summary>
    /// Exe-list decision for non-wheel events (pan). Exclusion mode: affect
    /// unless the foreground exe is listed. Inclusion mode: affect only if it
    /// is. Class rules do not gate pan, so middle-button pan still works on
    /// class-blocked surfaces like File Explorer.
    /// </summary>
    private bool ExeAffectsForeground()
    {
        RefreshForegroundExeIfStale();
        return _config.Apps.Mode == AppListMode.Exclusion ? !_foregroundExeInList : _foregroundExeInList;
    }

    /// <summary>
    /// Wheel decision. Game Mode is consulted first with the event's own
    /// cursor point (hwnd-keyed, fresh on flicks), then the foreground exe
    /// match and the under-cursor window-class match against the active list,
    /// then the mode. Exclusion: smooth unless matched. Inclusion: smooth only
    /// if matched.
    /// </summary>
    private bool ShouldSmoothWheel(int x, int y)
    {
        if (_gameGate.ShouldPause(x, y)) return false;
        RefreshForegroundExeIfStale();
        bool inList = _foregroundExeInList || CursorClassInList();
        return _config.Apps.Mode == AppListMode.Exclusion ? !inList : inList;
    }

    /// <summary>"" when Game Mode is not pausing; otherwise a short reason
    /// ("Valorant", "Easy Anti-Cheat"). Surfaced in the tray tooltip.</summary>
    public string GamePauseStatus => _gameGate.PauseStatus;

    // ----- tray click-to-target finder -----

    /// <summary>Arm targeting: the next left-click is captured instead of
    /// reaching its window. Poll <see cref="TargetingActive"/> and
    /// <see cref="TryTakePick"/> from the UI thread.</summary>
    public void BeginTargeting()
    {
        _pickedCls = string.Empty;
        _pickedExe = string.Empty;
        _picked = false;
        _swallowNextUp = false;
        _targeting = true;
    }

    public void CancelTargeting() => _targeting = false;

    public bool TargetingActive => _targeting;

    /// <summary>If a click was captured, hand back its class + exe once.</summary>
    public bool TryTakePick(out string cls, out string exe)
    {
        if (!_picked) { cls = string.Empty; exe = string.Empty; return false; }
        cls = _pickedCls;
        exe = _pickedExe;
        _picked = false;
        return true;
    }

    private void CaptureTargetClick()
    {
        _targeting = false;
        try
        {
            IntPtr root = Win32.CursorRootWindow();
            Win32.GetWindowThreadProcessId(root, out uint pid);
            // Ignore our own windows and dead targets; that click just cancels.
            if (root != IntPtr.Zero && pid != 0 && pid != (uint)Environment.ProcessId)
            {
                _pickedCls = Win32.ClassNameOf(root);
                _pickedExe = Win32.GetProcessExeName(pid);
                _picked = true;
            }
        }
        catch { /* leave _picked false so the tray treats it as a cancel */ }
    }

    private static string ResolveForegroundExe()
    {
        // Direct QueryFullProcessImageNameW instead of Process.MainModule.
        // MainModule opens the loader lock and can throw on protected
        // processes (elevated apps, antivirus, etc), this call doesn't.
        // Result: faster + no exception on access-denied + lower handle
        // pressure since we pass PROCESS_QUERY_LIMITED_INFORMATION which
        // works on processes a full PROCESS_QUERY_INFORMATION would reject.
        IntPtr hwnd = Win32.GetForegroundWindow();
        if (hwnd == IntPtr.Zero) return string.Empty;
        Win32.GetWindowThreadProcessId(hwnd, out uint pid);
        if (pid == 0) return string.Empty;
        return Win32.GetProcessExeName(pid);
    }

    private void TimerLoop()
    {
        // Match tick rate to the detected display refresh so emit cadence
        // aligns with vsync. A fixed 150Hz aliases against common refresh
        // rates (60/144/240Hz), some frames get extra emits, others get
        // none, producing micro-stutter. Matching gives ~1 emit per frame.
        // Clamped 60-360Hz to stay sane on edge cases (extremely slow or
        // ultra-high-refresh displays). timeBeginPeriod(1) called in Start()
        // ensures the OS scheduler can hit these rates without spinning.
        int refreshHz = Math.Clamp(Config.DisplayInfo.GetPrimaryRefreshRate(), 60, 360);
        double targetIntervalMs = 1000.0 / refreshHz;
        var sw = System.Diagnostics.Stopwatch.StartNew();
        double nextTickMs = sw.Elapsed.TotalMilliseconds;

        while (_running)
        {
            try
            {
                // Game Mode in-flight flush: gating new notches is not enough,
                // because a glide already animating (or a pan coast) keeps
                // emitting wheel events, and the OS routes those to whatever
                // is under the cursor. If the cursor is now over a paused
                // target, kill all in-flight motion so nothing dribbles into
                // the game. Cheap: hwnd-keyed cache means this is a couple of
                // microseconds per tick, and zero when Game Mode is off.
                if (_gameGate.Enabled && _gameGate.ShouldPauseAtCursor())
                {
                    _wheel.Reset();
                    _wheelH.Reset();
                    if (_state.IsActive)
                    {
                        _pan.Cancel();
                        _state.CancelCoast();
                    }
                }
                else
                {
                    _wheel.Tick();
                    _wheelH.Tick();
                    _pan.Tick();
                }
            }
            catch
            {
                // Don't kill the pipeline thread on transient errors.
            }

            nextTickMs += targetIntervalMs;
            double remaining = nextTickMs - sw.Elapsed.TotalMilliseconds;
            if (remaining > 2)
                Thread.Sleep((int)(remaining - 1));
            // Spin the last bit for tighter timing.
            while (sw.Elapsed.TotalMilliseconds < nextTickMs && _running)
                Thread.SpinWait(100);
        }
    }

    public void Dispose() => Stop();
}
