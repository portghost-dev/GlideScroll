using System.Diagnostics;
using GlideScroll.Config;
using GlideScroll.Native;

namespace GlideScroll.Pipeline;

/// <summary>
/// Middle-button pan engine. Converts cursor displacement during a held middle
/// button into synthetic wheel events; optionally captures exit velocity on
/// release for a kinetic coast.
///
/// Two engines share this controller, selected by <see cref="Mode"/>:
///   Feather (default) - DIRECT DISPLACEMENT drive. Each timer tick emits
///     exactly how far the hand moved since the last tick, scaled by
///     sensitivity. No velocity window, no low-pass filter, so content tracks
///     the hand 1:1 with only one frame of latency. This is the "paper under
///     the palm" feel. A short sample window is still kept, but read ONLY at
///     release, so a flick still coasts without adding any drag-time lag.
///   Cushion - the WINDOWED-SAMPLING velocity model described below, plus the
///     optional Smoothness low-pass. Softer and more damped, at the cost of the
///     averaging delay inherent to a velocity model.
///
/// Architecture (Cushion): WINDOWED-SAMPLING velocity model.
///   While the user is panning, every mouse-move delta is appended to a sliding
///   window of recent (timestamp, dx, dy) samples. The pipeline timer reads
///   "current velocity" each tick as the average slope across samples within
///   a short time window (default 100ms). On release, that windowed velocity
///   becomes the initial coast velocity and decays via exponential friction.
///
/// The defining benefit of sampling over accumulation: velocity always
/// reflects the user's CURRENT motion rather than integrated history. There's
/// no need for active-drag damping hacks because old motion naturally falls
/// out of the window. Stops feel crisp ("stop = stop"), and flick exit
/// velocity faithfully captures the user's intent (windowed average, not
/// noisy single-frame).
///
/// Thread model: OnPanMotion runs on the low-level mouse hook thread; Tick
/// runs on the pipeline timer thread. All shared state is _lock-protected.
/// </summary>
public sealed class PanController
{
    // Velocity sampling window. 100ms is the sweet spot, long enough to
    // smooth single-frame jitter, short enough to feel responsive.
    private const int WindowMs = 100;
    // If no motion in this long, treat next motion as a fresh pan session
    // (clear stale samples). Prevents windowed velocity from including
    // pre-pause motion.
    private const int StaleSampleMs = 500;
    // Below these per-second thresholds, motion is treated as "stopped"
    // (in pixels/sec). Avoids emitting infinitesimal residual scroll.
    private const double StopThresholdPxPerSec = 1.0;

    private readonly object _lock = new();
    private readonly Stopwatch _sw = Stopwatch.StartNew();

    // Recent (time, dx, dy) samples for windowed velocity. List is sufficient
    //, at typical mouse rates the buffer holds <= ~100 entries.
    private readonly List<(long timeMs, int dx, int dy)> _samples = new();

    // Coast-phase velocity carried from end-of-pan into the decay window.
    private double _coastVx, _coastVy;
    private bool _isCoasting;

    // Sub-pixel emit residuals so fractional scroll-pixels are not lost
    // between ticks.
    private double _residualX, _residualY;

    // Feather mode: net cursor displacement accumulated since the last tick.
    // OnPanMotion (hook thread) adds to these; Tick (timer thread) consumes and
    // zeroes them, emitting the displacement directly with no averaging. Sign
    // (ReverseDirection) is already baked in when they are accumulated.
    private double _pendingDx, _pendingDy;

    // Last tick time for proper dt integration in coast and active phases.
    private long _lastTickMs;

    // ----- Tunables (public properties, set by ScrollPipeline.ApplyConfig) -----

    /// <summary>Active engine. Feather = direct displacement (default), Cushion =
    /// windowed velocity + low-pass filter. Only affects the active-drag phase;
    /// the release coast is identical in both.</summary>
    public PanMode Mode { get; set; } = PanMode.Feather;

    /// <summary>Coast decay rate. Velocity is multiplied by exp(-Friction*dt)
    /// per second of elapsed time during coast. Higher values stop faster.
    /// Default 30 ⇒ velocity decays to ~1% over ~150ms.</summary>
    public double Friction { get; set; } = 30.0;

    /// <summary>Horizontal scroll-pixels emitted per cursor-pixel of motion.
    /// Default 100 maps to ~1.79x scroll-to-cursor ratio (liquid feel).</summary>
    public double SensitivityX { get; set; } = 100.0;

    /// <summary>Vertical scroll-pixels emitted per cursor-pixel of motion.</summary>
    public double SensitivityY { get; set; } = 100.0;

    /// <summary>Minimum accumulated scroll pixels before an emit fires.
    /// Filters tiny jitter that would otherwise create chatty wheel events.</summary>
    public int MinStepPixels { get; set; } = 5;

    /// <summary>If true, releasing the middle button captures exit velocity
    /// and decays via friction. If false, motion stops instantly on release.</summary>
    public bool FlickMode { get; set; } = true;

    /// <summary>If true, only the dominant axis emits each tick, suppresses
    /// diagonal scrolling for content that doesn't handle it well.</summary>
    public bool SnapToAxis { get; set; } = false;

    /// <summary>If true, inverts the scroll direction (drag down ⇒ scroll up,
    /// vs the default drag down ⇒ scroll down).</summary>
    public bool ReverseDirection { get; set; } = false;

    /// <summary>Low-pass filter strength (0-20) applied to scroll velocity.
    /// 0 = filter off (raw windowed velocity passes through). Higher values
    /// reject more high-frequency jitter at the cost of slight lag. Driven
    /// by the "Smoothness" slider in the Pan UI.
    ///
    /// Math: time constant τ = Smoothness × 5 ms. Each tick:
    ///   alpha    = 1 − exp(−dt / τ)
    ///   smoothed = smoothed + (raw − smoothed) × alpha
    /// </summary>
    public double Smoothness { get; set; } = 5.0;

    // Smoothed-velocity state (output of the low-pass filter). Persists
    // across ticks so the filter has memory.
    private double _smoothedVx, _smoothedVy;

    // ----- API called by PanStateMachine -----

    /// <summary>Append a cursor displacement sample during active panning.
    /// Samples drop out of the velocity window automatically as they age past
    /// WindowMs. Long pauses (> StaleSampleMs) trigger a buffer reset so a
    /// fresh pan session starts with no historical bias.</summary>
    public void OnPanMotion(int dx, int dy)
    {
        lock (_lock)
        {
            _isCoasting = false;
            long now = _sw.ElapsedMilliseconds;

            // Stale-sample detection: if too long since last sample, this is
            // effectively a new pan session, discard old data.
            if (_samples.Count > 0 && (now - _samples[^1].timeMs) > StaleSampleMs)
                _samples.Clear();

            double sign = ReverseDirection ? -1.0 : 1.0;
            double sdx = dx * sign;
            double sdy = dy * sign;

            // Feather reads these directly each tick. Cushion ignores them.
            _pendingDx += sdx;
            _pendingDy += sdy;

            _samples.Add((now, (int)sdx, (int)sdy));

            // Drop entries beyond the velocity window so the next slope
            // calculation reflects only recent motion.
            while (_samples.Count > 0 && (now - _samples[0].timeMs) > WindowMs)
                _samples.RemoveAt(0);
        }
    }

    /// <summary>End of an active pan. If FlickMode, capture the windowed
    /// exit velocity and transition to coasting. Otherwise stop immediately.</summary>
    public void OnRelease()
    {
        lock (_lock)
        {
            if (FlickMode)
            {
                long now = _sw.ElapsedMilliseconds;
                var (vx, vy) = ComputeWindowedVelocity(now);
                _coastVx = vx;
                _coastVy = vy;
                _isCoasting = Math.Abs(vx) > StopThresholdPxPerSec
                              || Math.Abs(vy) > StopThresholdPxPerSec;
            }
            else
            {
                _coastVx = 0;
                _coastVy = 0;
                _isCoasting = false;
            }
            _samples.Clear();
            _pendingDx = 0;
            _pendingDy = 0;
        }
    }

    /// <summary>Hard cancel, drop all motion, reset state. Called when other
    /// input arrives (different button, wheel scroll, etc.) or on mode change.</summary>
    public void Cancel()
    {
        lock (_lock)
        {
            _coastVx = 0;
            _coastVy = 0;
            _residualX = 0;
            _residualY = 0;
            _smoothedVx = 0;
            _smoothedVy = 0;
            _pendingDx = 0;
            _pendingDy = 0;
            _samples.Clear();
            _isCoasting = false;
        }
    }

    /// <summary>True while there's still motion to emit (active samples or
    /// active coast). PanStateMachine queries this to decide when to drop
    /// from Coasting back to Idle.</summary>
    public bool HasMotion
    {
        get { lock (_lock) return _isCoasting || _samples.Count > 0; }
    }

    /// <summary>Pipeline timer hook. Reads current velocity (sampled or coast),
    /// integrates with dt, accumulates residual, emits wheel events when
    /// accumulated scroll exceeds MinStepPixels.</summary>
    public void Tick()
    {
        long now = _sw.ElapsedMilliseconds;
        if (_lastTickMs == 0) _lastTickMs = now;
        double dt = (now - _lastTickMs) / 1000.0;
        _lastTickMs = now;
        if (dt <= 0) dt = 0.001;

        // Scroll-pixels to emit this tick, per axis. Computed under the lock,
        // emitted after releasing it.
        double emitX, emitY;

        lock (_lock)
        {
            if (_isCoasting)
            {
                // Exponential friction decay: v(t+dt) = v(t) * exp(-Friction*dt).
                // Frame-rate independent: same total decay over the same elapsed
                // time regardless of tick rate. Shared by both engines.
                double decay = Math.Exp(-Friction * dt);
                _coastVx *= decay;
                _coastVy *= decay;

                if (Math.Abs(_coastVx) < StopThresholdPxPerSec
                    && Math.Abs(_coastVy) < StopThresholdPxPerSec)
                {
                    _coastVx = 0;
                    _coastVy = 0;
                    _isCoasting = false;
                }

                double svx = _coastVx * (SensitivityX / 56.0);
                double svy = _coastVy * (SensitivityY / 56.0);
                // Coast is already smooth (exponential), so no low-pass; just keep
                // the filter state aligned for a clean handoff if a drag resumes.
                _smoothedVx = svx;
                _smoothedVy = svy;
                emitX = svx * dt;
                emitY = svy * dt;
            }
            else if (Mode == PanMode.Feather)
            {
                // DIRECT DISPLACEMENT: emit exactly how far the hand moved since
                // the last tick, scaled by sensitivity. No velocity window, no
                // low-pass, so content tracks the hand 1:1 with one frame of
                // latency. Sensitivity divisor 56 matches Cushion's calibration
                // (~1.79x scroll-to-cursor at the default Sensitivity=100).
                emitX = _pendingDx * (SensitivityX / 56.0);
                emitY = _pendingDy * (SensitivityY / 56.0);
                _pendingDx = 0;
                _pendingDy = 0;
                // Keep filter state neutral so switching to Cushion, or coasting
                // on release, starts from a clean slate.
                _smoothedVx = 0;
                _smoothedVy = 0;
            }
            else
            {
                // CUSHION active pan: read velocity from the windowed sample
                // buffer, then apply the optional low-pass. Softer and more
                // damped, and it carries the window's averaging delay.
                (double vx, double vy) = ComputeWindowedVelocity(now);
                // Trim aged samples now so subsequent calls reflect fresh motion.
                while (_samples.Count > 0 && (now - _samples[0].timeMs) > WindowMs)
                    _samples.RemoveAt(0);

                // Sensitivity divisor 56 calibrates the default Sensitivity=100 to
                // a ~1.79x scroll-to-cursor ratio for a liquid, free-flowing feel.
                double svx = vx * (SensitivityX / 56.0);
                double svy = vy * (SensitivityY / 56.0);

                // First-order low-pass on scroll velocity for jitter rejection.
                // Smoothness 0 = filter off. Smoothness above 0 = EMA with
                // alpha = 1 - exp(-dt / tau), tau = Smoothness x 5 ms. Frame-rate
                // independent.
                if (Smoothness > 0)
                {
                    double tauSec = Smoothness * 0.005;
                    double alpha = 1.0 - Math.Exp(-dt / tauSec);
                    _smoothedVx += (svx - _smoothedVx) * alpha;
                    _smoothedVy += (svy - _smoothedVy) * alpha;
                    svx = _smoothedVx;
                    svy = _smoothedVy;
                }
                else
                {
                    // Filter disabled: keep state aligned with current velocity so
                    // turning the slider on later doesn't introduce a transient.
                    _smoothedVx = svx;
                    _smoothedVy = svy;
                }

                emitX = svx * dt;
                emitY = svy * dt;
            }
        }

        if (SnapToAxis)
        {
            // Suppress whichever axis has the lower magnitude this tick.
            if (Math.Abs(emitX) > Math.Abs(emitY)) emitY = 0;
            else emitX = 0;
        }

        EmitAxis(ref _residualY, emitY, isHorizontal: false);
        EmitAxis(ref _residualX, emitX, isHorizontal: true);
    }

    // ----- Internal helpers -----

    /// <summary>Compute average velocity (px/sec) from the current sample
    /// window. Caller must hold _lock. Returns (0, 0) if buffer is empty or
    /// span is too short to be meaningful.</summary>
    private (double vx, double vy) ComputeWindowedVelocity(long now)
    {
        if (_samples.Count == 0) return (0, 0);

        long firstTime = _samples[0].timeMs;
        long span = now - firstTime;
        if (span < 1) return (0, 0);

        long totalDx = 0, totalDy = 0;
        foreach (var s in _samples)
        {
            totalDx += s.dx;
            totalDy += s.dy;
        }
        // span is in ms; multiply by 1000 to convert to px/sec.
        return (totalDx * 1000.0 / span, totalDy * 1000.0 / span);
    }

    /// <summary>Accumulate scroll-pixels into the residual; when accumulated
    /// magnitude exceeds MinStepPixels, emit a synthetic wheel event in the
    /// appropriate axis. Sub-MinStep amounts are carried forward so they
    /// eventually accumulate enough to fire, no motion is lost.</summary>
    private void EmitAxis(ref double residual, double scrollPx, bool isHorizontal)
    {
        residual += scrollPx;
        int pixelDelta = (int)residual;
        if (Math.Abs(pixelDelta) < MinStepPixels) return;

        residual -= pixelDelta;

        // Convert pixel-space delta to Win32 wheel-delta units (120 per ~100px
        // by convention).
        int wheelDelta = (int)Math.Round(pixelDelta * 1.2);
        if (wheelDelta == 0) return;

        if (isHorizontal)
            InputSynth.SendHorizontalWheel(wheelDelta);
        else
            InputSynth.SendVerticalWheel(wheelDelta);
    }
}
