using System.Diagnostics;
using GlideScroll.Config;
using GlideScroll.Native;

namespace GlideScroll.Pipeline;

/// <summary>
/// Game Mode auto-pause. Decides, per input event, whether GlideScroll should
/// stand down because the input is headed into a game, so smoothing never
/// interferes with in-game scrolling and injected input stays clear of
/// anti-cheat while a game is on screen.
///
/// Design rules (see ARCHITECTURE-v1.3-game-pause.md):
///  - The verdict is anchored to the window UNDER THE CURSOR (wheel) or the
///    press target (pan), never to a global "a game is running" flag. That is
///    what makes multi-monitor correct: a game on one screen and a browser on
///    another are decided independently, per event.
///  - The per-window verdict is computed live on the event and cached by
///    window handle with a short TTL, so a fast flick from game to browser is
///    correct on the very first notch, and an alt-enter (windowed to
///    fullscreen, same hwnd) is picked up within the TTL.
///  - Machine-global signals (is a protected game live, exclusive fullscreen)
///    are flick-invariant, so they are refreshed on a slow background timer
///    and only ever READ on the event path.
///  - We never open a handle into the game process beyond the limited-info
///    exe-path query, and we never read its modules or memory. Detection uses
///    evidence the anti-cheat ecosystem itself produces: watchdog processes,
///    driver services, and runtime files shipped in the game's install folder.
///
/// Decision tiers, strongest first:
///  1. Exe match against the Game Mode list (seeded + user-added). Certain.
///  2. Install footprint: an anti-cheat runtime folder (EasyAntiCheat,
///     BattlEye, GameGuard) next to the hovered exe. Near-certain, no list.
///  3. Game-shaped window (borderless, fills its own monitor) corroborated by
///     a machine-global signal (anti-cheat watchdog or driver live, a known
///     game process live, exclusive fullscreen, or a Steam game running).
/// A maximized normal window keeps WS_CAPTION so tier 3 never catches it, and
/// an F11 browser is only ever caught while a game is genuinely live, the
/// acknowledged low-stakes residual.
/// </summary>
public sealed class GamePauseGate : IDisposable
{
    // ----- anti-cheat engine fingerprints (engines, never games) -----
    // On-demand watchdog processes: these run ONLY while their protected game
    // runs, so presence alone proves a protected game is live right now.
    // Names are compared as lowercase stems (extension stripped) so
    // GameGuard's "GameMon.des" matches however the process table reports it.
    private static readonly (string Engine, string[] Stems)[] OnDemandEngines =
    {
        ("Easy Anti-Cheat", new[] { "easyanticheat", "easyanticheat_eos" }),
        ("BattlEye",        new[] { "beservice", "beservice_x64", "pnkbstrb" }),
        ("GameGuard",       new[] { "gamemon", "gamemon64" }),
        ("Anti-Cheat Expert", new[] { "sguard64", "sguardsvc64" }),
    };

    // On-demand DRIVER services: engines whose only separate runtime artifact
    // is a kernel driver (no user-mode watchdog). Queried via SCM status,
    // which needs no admin rights and touches no process.
    // NOTE: Vanguard's vgk/vgc are deliberately ABSENT. Vanguard is
    // boot-resident, so its presence proves nothing about a game being live;
    // Riot titles are covered by the seeded exe list instead (review fix 1).
    private static readonly (string Engine, string Service)[] OnDemandDriverServices =
    {
        ("XIGNCODE3", "xhunter1"),
        ("GameGuard", "npggNT"),
    };

    // Anti-cheat runtime folders shipped inside game install directories.
    // Checked next to the hovered exe (and up to two parent levels, which
    // covers Unreal-style trees like FortniteGame\Binaries\Win64).
    private static readonly string[] FootprintDirs =
    {
        "EasyAntiCheat", "BattlEye", "GameGuard",
    };

    private const int VerdictTtlMs = 400;    // same-hwnd revalidation (alt-enter, hwnd reuse)
    private const int PollIntervalMs = 2500; // machine-global signal refresh

    // ----- state -----
    private readonly object _lock = new();
    private readonly Stopwatch _sw = Stopwatch.StartNew();

    // Config snapshot (rebuilt on OnConfigChanged).
    private volatile bool _enabled;
    private HashSet<string> _gameSet = new(StringComparer.OrdinalIgnoreCase);   // "cs2.exe"
    private HashSet<string> _gameStems = new(StringComparer.OrdinalIgnoreCase); // "cs2"

    // hwnd-keyed verdict cache (guarded by _lock).
    private IntPtr _lastHwnd;
    private bool _lastPause;
    private string _lastReason = "";
    private long _lastCheckedMs;

    // Machine-global signals, written by the poller thread, read on the event
    // path. volatile is sufficient: single writer, torn reads impossible for
    // bool/reference.
    private volatile bool _protectedGameLive;
    private volatile string _liveReason = "";
    private volatile bool _fullscreenActive;

    // Last verdict surfaced to the tray ("" = not paused).
    private volatile string _pauseStatus = "";

    private System.Threading.Timer? _pollTimer;

    /// <summary>"" when not pausing; otherwise a short human reason like
    /// "Valorant" or "Easy Anti-Cheat". Read by the tray for its tooltip.</summary>
    public string PauseStatus => _pauseStatus;

    public bool Enabled => _enabled;

    public void Start()
    {
        _pollTimer ??= new System.Threading.Timer(_ => RefreshGlobalSignals(),
            null, 0, PollIntervalMs);
    }

    public void Stop()
    {
        _pollTimer?.Dispose();
        _pollTimer = null;
    }

    public void Dispose() => Stop();

    /// <summary>Rebuild the config snapshot. Called from ApplyConfig whenever
    /// settings change; also invalidates the verdict cache so an edit takes
    /// effect on the next event.</summary>
    public void OnConfigChanged(AppConfig cfg)
    {
        var set = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var stems = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var g in cfg.GameMode.Games)
        {
            if (string.IsNullOrWhiteSpace(g)) continue;
            string name = g.Trim();
            set.Add(name);
            stems.Add(StripExt(name));
        }
        lock (_lock)
        {
            _gameSet = set;
            _gameStems = stems;
            _lastHwnd = IntPtr.Zero; // invalidate
        }
        _enabled = cfg.GameMode.Enabled;
        if (!_enabled) _pauseStatus = "";
    }

    /// <summary>The live per-event decision. (x, y) is the event's screen
    /// point: the cursor for wheel events, the press point for pan. Safe from
    /// both the hook thread and the pipeline timer thread.</summary>
    public bool ShouldPause(int x, int y)
    {
        if (!_enabled) return false;

        var pt = new Win32.POINT { X = x, Y = y };
        IntPtr hwnd = Win32.WindowFromPoint(pt);
        if (hwnd == IntPtr.Zero) return false;
        IntPtr root = Win32.GetAncestor(hwnd, Win32.GA_ROOT);
        if (root != IntPtr.Zero) hwnd = root;

        long now = _sw.ElapsedMilliseconds;
        lock (_lock)
        {
            // hwnd-keyed with a short TTL: fresh on every window change (fast
            // flick correctness), revalidated within 400ms on the same window
            // (alt-enter, recycled handles).
            if (hwnd == _lastHwnd && (now - _lastCheckedMs) < VerdictTtlMs)
                return _lastPause;

            (bool pause, string reason) = Classify(hwnd, pt);
            _lastHwnd = hwnd;
            _lastPause = pause;
            _lastReason = reason;
            _lastCheckedMs = now;
            _pauseStatus = pause ? reason : "";
            return pause;
        }
    }

    /// <summary>Convenience for the pipeline timer: decision at the current
    /// cursor position. Used to stop in-flight motion (wheel animation, pan
    /// coast) from dribbling into a game the cursor just moved onto.</summary>
    public bool ShouldPauseAtCursor()
    {
        if (!_enabled) return false;
        if (!Win32.GetCursorPos(out Win32.POINT p)) return false;
        return ShouldPause(p.X, p.Y);
    }

    // ----- per-window classification (caller holds _lock) -----

    private (bool pause, string reason) Classify(IntPtr hwnd, Win32.POINT pt)
    {
        Win32.GetWindowThreadProcessId(hwnd, out uint pid);
        if (pid == 0 || pid == (uint)Environment.ProcessId) return (false, "");

        string exePath = Win32.GetProcessExePath(pid);
        string exeName = exePath.Length > 0
            ? exePath.Substring(exePath.LastIndexOf('\\') + 1) : "";

        // Tier 1: exact match against the Game Mode list (seeds + user adds).
        if (exeName.Length > 0 && _gameSet.Contains(exeName))
            return (true, WindowTargeting.FriendlyAppName(exeName));

        // Tier 2: anti-cheat runtime files shipped next to the exe. Positive
        // proof this specific window's app is anti-cheat protected, with no
        // list and no process access. Absence proves nothing (VAC ships no
        // files), so this only ever upgrades, never clears.
        if (exePath.Length > 0 && HasAntiCheatFootprint(exePath, out string engine))
            return (true, engine);

        // Tier 3: game-shaped window + a corroborating machine-global signal.
        // Borderless means no caption and no resize frame; a maximized normal
        // app keeps WS_CAPTION and can never land here.
        if (_protectedGameLive || _fullscreenActive)
        {
            int style = Win32.GetWindowLong(hwnd, Win32.GWL_STYLE);
            bool borderless = (style & (Win32.WS_CAPTION | Win32.WS_THICKFRAME)) == 0;
            if (borderless && WindowFillsItsMonitor(hwnd, pt))
            {
                string reason = _protectedGameLive ? _liveReason : "fullscreen game";
                return (true, reason);
            }
        }

        return (false, "");
    }

    private static bool HasAntiCheatFootprint(string exePath, out string engine)
    {
        engine = "";
        try
        {
            string? dir = Path.GetDirectoryName(exePath);
            // Walk up to two parents: Unreal-style games keep the exe in
            // <Game>\Binaries\Win64 with the anti-cheat folder at the root.
            for (int level = 0; level < 3 && dir is not null; level++)
            {
                foreach (var f in FootprintDirs)
                {
                    if (Directory.Exists(Path.Combine(dir, f)))
                    {
                        engine = f == "EasyAntiCheat" ? "Easy Anti-Cheat" : f;
                        return true;
                    }
                }
                dir = Path.GetDirectoryName(dir);
            }
        }
        catch { /* malformed path, treat as no footprint */ }
        return false;
    }

    private static bool WindowFillsItsMonitor(IntPtr hwnd, Win32.POINT pt)
    {
        if (!Win32.GetWindowRect(hwnd, out Win32.RECT wr)) return false;
        // The cursor is over the window, so the monitor at the cursor IS the
        // window's monitor. Compare against the full monitor rect (not the
        // work area): fullscreen games cover the taskbar.
        IntPtr mon = Win32.MonitorFromPoint(pt, Win32.MONITOR_DEFAULTTONEAREST);
        if (mon == IntPtr.Zero) return false;
        var mi = new Win32.MONITORINFO { cbSize = (uint)System.Runtime.InteropServices.Marshal.SizeOf<Win32.MONITORINFO>() };
        if (!Win32.GetMonitorInfo(mon, ref mi)) return false;
        return wr.Left <= mi.rcMonitor.Left && wr.Top <= mi.rcMonitor.Top
            && wr.Right >= mi.rcMonitor.Right && wr.Bottom >= mi.rcMonitor.Bottom;
    }

    // ----- machine-global signals (poller thread only) -----

    private void RefreshGlobalSignals()
    {
        if (!_enabled)
        {
            _protectedGameLive = false;
            _fullscreenActive = false;
            return;
        }

        try
        {
            bool live = false;
            string reason = "";

            // Single process sweep, one enumeration for all fingerprints.
            // Names are normalized to lowercase stems so "GameMon.des" and
            // "BEService" match regardless of how the table reports them.
            var stems = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var p in Process.GetProcesses())
            {
                try { stems.Add(StripExt(p.ProcessName)); }
                finally { p.Dispose(); }
            }

            foreach (var (engineName, engineStems) in OnDemandEngines)
            {
                foreach (var s in engineStems)
                {
                    if (stems.Contains(s)) { live = true; reason = engineName; break; }
                }
                if (live) break;
            }

            // A seeded/user-listed game process running anywhere corroborates
            // too. This is what covers borderless VAC titles (no watchdog) and
            // boot-resident engines (Vanguard: presence proves nothing, but
            // VALORANT-Win64-Shipping actually running does).
            if (!live)
            {
                HashSet<string> gameStems;
                lock (_lock) gameStems = _gameStems;
                foreach (var s in gameStems)
                {
                    if (stems.Contains(s)) { live = true; reason = "game running"; break; }
                }
            }

            // Driver-only engines: SCM status query, no process to look for.
            if (!live)
            {
                foreach (var (engineName, service) in OnDemandDriverServices)
                {
                    if (Win32.IsServiceRunning(service)) { live = true; reason = engineName; break; }
                }
            }

            // Steam's "a game is running" hint. Weak (fires for any Steam
            // title, anti-cheat or not) so it only corroborates tier 3.
            if (!live)
            {
                try
                {
                    object? v = Microsoft.Win32.Registry.GetValue(
                        @"HKEY_CURRENT_USER\Software\Valve\Steam", "RunningAppID", 0);
                    if (v is int appId && appId != 0) { live = true; reason = "Steam game"; }
                }
                catch { }
            }

            _liveReason = reason;
            _protectedGameLive = live;

            // Exclusive-fullscreen D3D state (session-global, attribution-free,
            // so it is only ever a tier-3 corroborator, never a trigger).
            try
            {
                _fullscreenActive = Win32.SHQueryUserNotificationState(out int quns) == 0
                                    && quns == Win32.QUNS_RUNNING_D3D_FULL_SCREEN;
            }
            catch { _fullscreenActive = false; }
        }
        catch
        {
            // A failed sweep must never take the input pipeline down. Keep the
            // previous signals; the next poll retries.
        }
    }

    private static string StripExt(string name)
    {
        int dot = name.LastIndexOf('.');
        return dot > 0 ? name.Substring(0, dot) : name;
    }
}
