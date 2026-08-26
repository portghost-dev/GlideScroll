using System.Diagnostics;
using GlideScroll.Config;

namespace GlideScroll.Pipeline;

// =============================================================================
// Wheel-smoothing engines. The ScrollPipeline picks one of these at runtime
// based on WheelConfig.SmoothingMode.
//
// All engines share these design rules:
//   - Direction-reversal clamp: scrolling down then immediately up zeros the
//     prior-direction motion so the user feels an instant reverse, not a
//     coast-through-zero.
//   - Sub-pixel residual accumulation via SmoothingMath.EmitWheelDelta.
//   - Lock-protected mutation; Tick() runs on the timer thread, EnqueueNotch
//     on the hook thread.
//   - Acceleration window/boost is honored by every engine that has any
//     notion of "magnitude per notch" (Flywheel, Spring, Hybrid). LowLatency
//     intentionally skips it for predictability.
// =============================================================================

/// <summary>
/// Momentum / inertia model. Each notch adds an impulse to a single virtual
/// velocity; friction continuously decays it; position is the integral.
///
/// Key property: velocity is *always* continuous. Two rapid notches genuinely
/// compound (velocity doubles) rather than summing two separate ramping curves,
/// so there is no inter-notch dip. Stops decelerate naturally
/// like a real flywheel coasting to rest.
///
/// Feels like macOS trackpad inertia or a Logitech MX Master in free-spin mode.
/// </summary>
public sealed class FlywheelSmoother : ISmoother
{
    private readonly object _lock = new();
    private readonly Stopwatch _sw = Stopwatch.StartNew();
    private readonly bool _isHorizontal;
    public FlywheelSmoother(bool isHorizontal = false) { _isHorizontal = isHorizontal; }

    private double _velocity;        // px/sec
    private double _position;        // virtual integrated position (px)
    private double _lastEmitted;     // position at last emit (px)
    private double _wheelResidual;   // sub-unit wheel-delta carry
    private long _lastTickMs = -1;

    // Acceleration window state
    private long _lastInputMs = -1;
    private double _accelMultiplier = 1.0;

    // Tuning (set in UpdateConfig)
    private double _impulsePerNotch = 600.0;   // px/sec added per notch
    private double _frictionPerSec = 0.001;    // velocity multiplier per second
    private double _maxVelocity = 5000.0;      // clamp absolute velocity (px/sec)
    private int _accelDeltaMs = 70;
    private double _accelMax = 7.0;

    public void UpdateConfig(WheelConfig cfg)
    {
        lock (_lock)
        {
            // Calibrate so total integrated scroll distance per notch == StepSize.
            // For exponential decay v(t) = v0·e^(-k·t), the integral ∫₀^∞ v dt = v0/k.
            // To make that equal StepSize, set v0 = StepSize · k.
            //   k = -ln(0.01) / (animTime_seconds)   (decay rate, "1% remaining")
            double targetMs = Math.Max(50, cfg.AnimationTimeMs);
            double k = -Math.Log(0.01) * 1000.0 / targetMs;
            _frictionPerSec = Math.Exp(-k);
            _impulsePerNotch = cfg.StepSize * k;
            // Velocity ceiling: 4× the per-notch maximum so power-flicking many
            // notches in a row genuinely compounds.
            _maxVelocity = _impulsePerNotch * cfg.AccelBoost * 4.0;
            _accelDeltaMs = cfg.AccelWindowMs;
            _accelMax = cfg.AccelBoost;
        }
    }

    public void EnqueueNotch(int direction)
    {
        lock (_lock)
        {
            long now = _sw.ElapsedMilliseconds;

            // Acceleration: rapid notches compound their impulse multiplier.
            if (_lastInputMs > 0 && (now - _lastInputMs) < _accelDeltaMs)
                _accelMultiplier = Math.Min(_accelMultiplier + 1, _accelMax);
            else
                _accelMultiplier = 1.0;
            _lastInputMs = now;

            double impulse = direction * _impulsePerNotch * _accelMultiplier;

            // Direction reversal: kill prior-direction velocity instantly,
            // discard pending unemitted position, AND clear the wheel-delta
            // residual so the new direction's first emit isn't biased by
            // leftover opposite-sign fractional units.
            if (Math.Sign(impulse) != Math.Sign(_velocity) && _velocity != 0)
            {
                _velocity = 0;
                _position = _lastEmitted;
                _wheelResidual = 0;
            }

            _velocity = Math.Clamp(_velocity + impulse, -_maxVelocity, _maxVelocity);
        }
    }

    public bool Tick()
    {
        double deltaPixels;
        bool stillWorking;

        lock (_lock)
        {
            long now = _sw.ElapsedMilliseconds;
            if (_lastTickMs < 0) _lastTickMs = now;
            double dt = (now - _lastTickMs) / 1000.0;
            _lastTickMs = now;
            if (dt <= 0) dt = 0.001;

            // Settle threshold, below this, treat as fully stopped.
            if (Math.Abs(_velocity) < 0.5) _velocity = 0;

            // Frame-rate-independent exponential decay.
            _velocity *= Math.Pow(_frictionPerSec, dt);

            _position += _velocity * dt;
            deltaPixels = _position - _lastEmitted;
            _lastEmitted = _position;

            stillWorking = Math.Abs(_velocity) > 0.5;
            if (!stillWorking)
            {
                // Reset position tracking to prevent floating-point drift over
                // long sessions. Safe because no motion is pending.
                _position = 0;
                _lastEmitted = 0;
                _wheelResidual = 0;
            }
        }

        SmoothingMath.EmitWheelDelta(deltaPixels, ref _wheelResidual, _isHorizontal);
        return stillWorking;
    }

    public void Reset()
    {
        lock (_lock)
        {
            _velocity = 0;
            _position = 0;
            _lastEmitted = 0;
            _wheelResidual = 0;
            _lastTickMs = -1;
            _accelMultiplier = 1.0;
            _lastInputMs = -1;
        }
    }
}

/// <summary>
/// Critically-damped second-order spring. Each notch advances
/// a target position; current position chases target with no overshoot. Spring
/// gives "controlled" feel, the page feels like it's on a rubber band being
/// yanked toward where you're scrolling, settling exactly to the target.
///
/// vs Flywheel: spring always reaches a definite settled position. Flywheel
/// can drift past where you "wanted" if you flick hard. Pick spring if you
/// like precise/predictable; pick flywheel if you like kinetic/coasting.
/// </summary>
public sealed class SpringSmoother : ISmoother
{
    private readonly object _lock = new();
    private readonly Stopwatch _sw = Stopwatch.StartNew();
    private readonly bool _isHorizontal;
    public SpringSmoother(bool isHorizontal = false) { _isHorizontal = isHorizontal; }

    private double _target;
    private double _current;
    private double _springVelocity;   // 2nd-order spring velocity term
    private double _lastEmitted;
    private double _wheelResidual;
    private long _lastTickMs = -1;

    private long _lastInputMs = -1;
    private double _accelMultiplier = 1.0;

    private double _stepSize = 120.0;
    private double _smoothTime = 0.25;   // seconds to ~99% convergence
    private double _dampingRatio = 1.0;  // 1.0 = critical (no overshoot)
    private int _accelDeltaMs = 70;
    private double _accelMax = 7.0;

    public void UpdateConfig(WheelConfig cfg)
    {
        lock (_lock)
        {
            _stepSize = cfg.StepSize;
            _smoothTime = Math.Max(0.05, cfg.AnimationTimeMs / 1000.0);
            // "Bounce / Glide" slider, range -10 to 10 with 0 = neutral (critical
            // damping, the textbook no-overshoot sweet spot).
            //   -10 maps to damping ratio 0.5 (underdamped, visible bounce)
            //     0 maps to damping ratio 1.0 (critical, no bounce, DEFAULT)
            //   +10 maps to damping ratio 2.0 (overdamped, slow glide)
            double p = Math.Clamp(cfg.FeelBias, -10.0, 10.0);
            _dampingRatio = p <= 0.0
                ? 1.0 + p * 0.05
                : 1.0 + p * 0.1;
            _accelDeltaMs = cfg.AccelWindowMs;
            _accelMax = cfg.AccelBoost;
        }
    }

    public void EnqueueNotch(int direction)
    {
        lock (_lock)
        {
            long now = _sw.ElapsedMilliseconds;

            if (_lastInputMs > 0 && (now - _lastInputMs) < _accelDeltaMs)
                _accelMultiplier = Math.Min(_accelMultiplier + 1, _accelMax);
            else
                _accelMultiplier = 1.0;
            _lastInputMs = now;

            double advance = direction * _stepSize * _accelMultiplier;

            // Direction reversal: snap target back to current, clear spring
            // velocity, AND zero the wheel-delta residual so the new direction's
            // first emit isn't biased by leftover opposite-sign fractional units.
            double pending = _target - _current;
            if (Math.Sign(advance) != Math.Sign(pending) && pending != 0)
            {
                _target = _current;
                _springVelocity = 0;
                _wheelResidual = 0;
            }

            _target += advance;
        }
    }

    public bool Tick()
    {
        double deltaPixels;
        bool stillWorking;

        lock (_lock)
        {
            long now = _sw.ElapsedMilliseconds;
            if (_lastTickMs < 0) _lastTickMs = now;
            double dt = (now - _lastTickMs) / 1000.0;
            _lastTickMs = now;
            if (dt <= 0) dt = 0.001;

            // Tunable damping ratio via our own 2nd-order ODE (symplectic Euler).
            // At dampingRatio = 1.0 it is critically damped (no overshoot);
            // lower values overshoot, higher values glide.
            SmoothingMath.Spring2ndOrder(
                ref _current, ref _springVelocity, _target,
                _smoothTime, _dampingRatio, dt);

            deltaPixels = _current - _lastEmitted;
            _lastEmitted = _current;

            // For underdamped springs (dampingRatio < 1), the stop condition
            // must also check that velocity is small, otherwise we'd freeze
            // mid-oscillation when current happens to cross target with non-
            // zero velocity. Both target proximity AND low velocity required.
            stillWorking = Math.Abs(_target - _current) > 0.5
                           || Math.Abs(_springVelocity) > 1.0;
            if (!stillWorking)
            {
                // Settled: snap to exact target and recenter to prevent drift.
                _current = _target;
                _lastEmitted = _current;
                _target = 0;
                _current = 0;
                _lastEmitted = 0;
                _springVelocity = 0;
                _wheelResidual = 0;
            }
        }

        SmoothingMath.EmitWheelDelta(deltaPixels, ref _wheelResidual, _isHorizontal);
        return stillWorking;
    }

    public void Reset()
    {
        lock (_lock)
        {
            _target = 0;
            _current = 0;
            _springVelocity = 0;
            _lastEmitted = 0;
            _wheelResidual = 0;
            _lastTickMs = -1;
            _accelMultiplier = 1.0;
            _lastInputMs = -1;
        }
    }
}

/// <summary>
/// Hybrid: spring-chase while user is actively scrolling (recent notches),
/// then transitions to flywheel coast when input stops. Best of both worlds , 
/// tight controllable response while spinning the wheel, kinetic decay after
/// release.
///
/// Internally tracks a "release threshold", if no notch arrives within
/// ReleaseDelayMs, the smoother captures current spring velocity, freezes
/// the target at current, and decays via flywheel friction until settled.
/// </summary>
public sealed class HybridSmoother : ISmoother
{
    private const long ReleaseDelayMs = 150;

    private readonly object _lock = new();
    private readonly Stopwatch _sw = Stopwatch.StartNew();
    private readonly bool _isHorizontal;
    public HybridSmoother(bool isHorizontal = false) { _isHorizontal = isHorizontal; }

    private double _target;
    private double _current;
    private double _springVelocity;
    private double _coastVelocity;     // captured at handoff, decays after release
    private double _previousCurrent;   // for handoff velocity capture
    private double _lastEmitted;
    private double _wheelResidual;
    private long _lastTickMs = -1;
    private long _lastNotchMs = -1;

    private long _lastInputMs = -1;
    private double _accelMultiplier = 1.0;

    private double _stepSize = 120.0;
    private double _smoothTime = 0.18;
    private double _coastFriction = 0.05;
    private int _accelDeltaMs = 70;
    private double _accelMax = 7.0;

    public void UpdateConfig(WheelConfig cfg)
    {
        lock (_lock)
        {
            _stepSize = cfg.StepSize;
            // "Spring / Coast" slider, range -10 to 10 with 0 = balanced 50/50.
            //   -10 maps to spring fraction 0.2 (mostly coast)
            //     0 maps to spring fraction 0.5 (balanced, DEFAULT)
            //   +10 maps to spring fraction 0.9 (mostly spring)
            double p = Math.Clamp(cfg.FeelBias, -10.0, 10.0);
            double springFraction = p <= 0.0
                ? 0.5 + p * 0.03
                : 0.5 + p * 0.04;
            _smoothTime = Math.Max(0.05, cfg.AnimationTimeMs / 1000.0 * springFraction);
            double coastMs = Math.Max(50, cfg.AnimationTimeMs * (1.0 - springFraction));
            _coastFriction = Math.Exp(Math.Log(0.01) * 1000.0 / coastMs);
            _accelDeltaMs = cfg.AccelWindowMs;
            _accelMax = cfg.AccelBoost;
        }
    }

    public void EnqueueNotch(int direction)
    {
        lock (_lock)
        {
            long now = _sw.ElapsedMilliseconds;

            if (_lastInputMs > 0 && (now - _lastInputMs) < _accelDeltaMs)
                _accelMultiplier = Math.Min(_accelMultiplier + 1, _accelMax);
            else
                _accelMultiplier = 1.0;
            _lastInputMs = now;
            _lastNotchMs = now;

            double advance = direction * _stepSize * _accelMultiplier;

            // Direction reversal: clear spring pending, coast velocity, AND
            // wheel-delta residual so the new direction starts unbiased.
            double pending = _target - _current;
            if ((Math.Sign(advance) != Math.Sign(pending) && pending != 0) ||
                (Math.Sign(advance) != Math.Sign(_coastVelocity) && _coastVelocity != 0))
            {
                _target = _current;
                _springVelocity = 0;
                _coastVelocity = 0;
                _wheelResidual = 0;
            }

            _target += advance;
            // Re-engaging spring phase, cancel any active coast.
            _coastVelocity = 0;
        }
    }

    public bool Tick()
    {
        double deltaPixels;
        bool stillWorking;

        lock (_lock)
        {
            long now = _sw.ElapsedMilliseconds;
            if (_lastTickMs < 0) _lastTickMs = now;
            double dt = (now - _lastTickMs) / 1000.0;
            _lastTickMs = now;
            if (dt <= 0) dt = 0.001;

            bool inActivePhase = _lastNotchMs > 0 && (now - _lastNotchMs) < ReleaseDelayMs;
            bool atTarget = Math.Abs(_target - _current) < 0.5;

            if (inActivePhase || !atTarget)
            {
                // Spring phase
                _previousCurrent = _current;
                // Critically-damped chase via our own 2nd-order spring (same
                // helper the Spring engine uses), so there is no third-party math.
                SmoothingMath.Spring2ndOrder(ref _current, ref _springVelocity, _target, _smoothTime, 1.0, dt);

                // Capture handoff velocity once we're past the active window.
                // Actual displacement / dt is more reliable than reading the
                // spring's internal velocity term directly.
                if (_lastNotchMs > 0 && (now - _lastNotchMs) >= ReleaseDelayMs / 2)
                {
                    _coastVelocity = (_current - _previousCurrent) / dt;
                }
            }
            else if (Math.Abs(_coastVelocity) > 0.5)
            {
                // Coast phase, flywheel decay
                _coastVelocity *= Math.Pow(_coastFriction, dt);
                _current += _coastVelocity * dt;
                _target = _current;  // keep target glued so spring doesn't fight
            }

            deltaPixels = _current - _lastEmitted;
            _lastEmitted = _current;

            stillWorking = !atTarget || Math.Abs(_coastVelocity) > 0.5;
            if (!stillWorking)
            {
                _target = 0;
                _current = 0;
                _previousCurrent = 0;
                _lastEmitted = 0;
                _springVelocity = 0;
                _coastVelocity = 0;
                _wheelResidual = 0;
            }
        }

        SmoothingMath.EmitWheelDelta(deltaPixels, ref _wheelResidual, _isHorizontal);
        return stillWorking;
    }

    public void Reset()
    {
        lock (_lock)
        {
            _target = 0;
            _current = 0;
            _springVelocity = 0;
            _coastVelocity = 0;
            _previousCurrent = 0;
            _lastEmitted = 0;
            _wheelResidual = 0;
            _lastTickMs = -1;
            _lastNotchMs = -1;
            _accelMultiplier = 1.0;
            _lastInputMs = -1;
        }
    }
}

/// <summary>
/// Minimal smoothing, each notch becomes a quick linear lerp toward target.
/// No physics, no curve, no acceleration. Fastest perceived response, slight
/// smoothing to remove the discrete-notch jolt.
///
/// Use case: gaming, vestibular sensitivity, "I want it smooth but I want it
/// NOW." Lerp duration is derived from AnimationTimeMs but capped at 80ms
/// regardless, Low Latency means Low Latency.
/// </summary>
public sealed class LowLatencySmoother : ISmoother
{
    private readonly object _lock = new();
    private readonly Stopwatch _sw = Stopwatch.StartNew();
    private readonly bool _isHorizontal;
    public LowLatencySmoother(bool isHorizontal = false) { _isHorizontal = isHorizontal; }

    private double _target;
    private double _current;
    private double _lastEmitted;
    private double _wheelResidual;
    private long _lastTickMs = -1;

    private double _stepSize = 120.0;
    private double _lerpDurationSec = 0.05;

    public void UpdateConfig(WheelConfig cfg)
    {
        lock (_lock)
        {
            _stepSize = cfg.StepSize;
            // 20% of glide-duration budget, capped between 20-80ms.
            _lerpDurationSec = Math.Clamp(cfg.AnimationTimeMs / 1000.0 * 0.2, 0.020, 0.080);
        }
    }

    public void EnqueueNotch(int direction)
    {
        lock (_lock)
        {
            // Direction reversal, discard pending forward motion AND clear
            // the wheel-delta residual so the new direction's first emit
            // isn't biased by leftover opposite-sign fractional units.
            double pending = _target - _current;
            if (Math.Sign(direction) != Math.Sign(pending) && pending != 0)
            {
                _target = _current;
                _wheelResidual = 0;
            }

            _target += direction * _stepSize;
        }
    }

    public bool Tick()
    {
        double deltaPixels;
        bool stillWorking;

        lock (_lock)
        {
            long now = _sw.ElapsedMilliseconds;
            if (_lastTickMs < 0) _lastTickMs = now;
            double dt = (now - _lastTickMs) / 1000.0;
            _lastTickMs = now;
            if (dt <= 0) dt = 0.001;

            // Linear approach at fixed speed (= one full step distance per
            // lerp duration). Clamp prevents overshoot.
            double maxSpeed = _stepSize / _lerpDurationSec;
            double maxStep = maxSpeed * dt;
            double remaining = _target - _current;

            if (Math.Abs(remaining) <= maxStep)
                _current = _target;
            else
                _current += Math.Sign(remaining) * maxStep;

            deltaPixels = _current - _lastEmitted;
            _lastEmitted = _current;

            stillWorking = Math.Abs(_target - _current) > 0.5;
            if (!stillWorking)
            {
                _target = 0;
                _current = 0;
                _lastEmitted = 0;
                _wheelResidual = 0;
            }
        }

        SmoothingMath.EmitWheelDelta(deltaPixels, ref _wheelResidual, _isHorizontal);
        return stillWorking;
    }

    public void Reset()
    {
        lock (_lock)
        {
            _target = 0;
            _current = 0;
            _lastEmitted = 0;
            _wheelResidual = 0;
            _lastTickMs = -1;
        }
    }
}

/// <summary>
/// Silk: online minimum-jerk. Each notch (re)plans a 5th-order minimum-jerk
/// trajectory from the current position/velocity/acceleration to the new target
/// over a FIXED short "settle time" T. Unlike a spring, whose settle is
/// asymptotic (a long imperceptible creep to rest), a min-jerk segment reaches
/// its target and stops EXACTLY at T with zero end-velocity and zero
/// end-acceleration, so it is as smooth as the spring but settles in a hard,
/// short, bounded time. Re-planning from live velocity/accel makes rapid notches
/// blend velocity-continuously.
///
/// Reversal rule: while there is real motion, the FIRST opposite notch is a stop
/// command, not a move. It triggers a very short, smooth emergency brake (a
/// smootherstep velocity ramp to rest) and is consumed, no motion travels the
/// new way. Only a SECOND notch (queued during / after the brake) launches the
/// new direction from the stopped position.
///
/// The math (min-jerk quintic to rest, general nonzero start state) is the
/// classic Flash-Hogan model, implemented here from the equations; no
/// third-party code.
/// </summary>
public sealed class SilkSmoother : ISmoother
{
    private readonly object _lock = new();
    private readonly Stopwatch _sw = Stopwatch.StartNew();
    private readonly bool _isHorizontal;
    public SilkSmoother(bool isHorizontal = false) { _isHorizontal = isHorizontal; }

    // Emitted trajectory state (pixel space).
    private double _pos, _vel, _acc;
    private double _lastEmitted;
    private double _wheelResidual;
    private long _lastTickMs = -1;

    // Active min-jerk segment: p(u) = c0 + c1 u + c2 u^2 + c3 u^3 + c4 u^4 + c5 u^5,
    // with u the seconds elapsed within the segment, u in [0, segT].
    private double _c0, _c1, _c2, _c3, _c4, _c5;
    private double _segT, _segElapsed;
    private double _target;        // absolute segment destination
    private bool _gliding;         // an active min-jerk segment is in flight

    // Emergency brake (reversal) state.
    private bool _braking;
    private double _brakeV0, _brakeT, _brakeElapsed;
    private double _pendingAdvance; // notches queued during the brake (relative px)

    // Acceleration window (rapid-notch compounding).
    private long _lastInputMs = -1;
    private double _accelMultiplier = 1.0;

    // Tuning.
    private double _stepSize = 120.0;
    private double _settleSec = 0.16;   // "settle time" T, the headline knob (finite)
    private double _brakeSec = 0.06;    // emergency-stop duration
    private int _accelDeltaMs = 70;
    private double _accelMax = 7.0;
    // Below this speed, motion is "basically stopped": an opposite notch then
    // just moves normally instead of braking.
    private const double ReversalVelThreshold = 60.0; // px/sec

    public void UpdateConfig(WheelConfig cfg)
    {
        lock (_lock)
        {
            _stepSize = cfg.StepSize;
            _settleSec = Math.Max(0.05, cfg.AnimationTimeMs / 1000.0);
            _brakeSec = Math.Clamp(_settleSec * 0.33, 0.04, 0.09);
            _accelDeltaMs = cfg.AccelWindowMs;
            _accelMax = cfg.AccelBoost;
        }
    }

    // Plan a min-jerk segment from the live (_pos,_vel,_acc) to (target, 0, 0) over T.
    private void PlanTo(double target, double T)
    {
        double h = target - _pos, v0 = _vel, a0 = _acc;
        double t2 = T * T, t3 = t2 * T, t4 = t3 * T, t5 = t4 * T;
        _c0 = _pos; _c1 = v0; _c2 = a0 * 0.5;
        _c3 = (20.0 * h - 12.0 * v0 * T - 3.0 * a0 * t2) / (2.0 * t3);
        _c4 = (-30.0 * h + 16.0 * v0 * T + 3.0 * a0 * t2) / (2.0 * t4);
        _c5 = (12.0 * h - 6.0 * v0 * T - a0 * t2) / (2.0 * t5);
        _segT = T; _segElapsed = 0.0; _target = target; _gliding = true;
    }

    public void EnqueueNotch(int direction)
    {
        lock (_lock)
        {
            long now = _sw.ElapsedMilliseconds;
            if (_lastInputMs > 0 && (now - _lastInputMs) < _accelDeltaMs)
                _accelMultiplier = Math.Min(_accelMultiplier + 1, _accelMax);
            else
                _accelMultiplier = 1.0;
            _lastInputMs = now;

            double advance = direction * _stepSize * _accelMultiplier;

            if (_braking)
            {
                // Brake already underway (first opposite notch consumed it).
                // This and further notches queue the new direction, launched
                // from rest when the brake completes.
                _pendingAdvance += advance;
                return;
            }

            bool meaningful = Math.Abs(_vel) > ReversalVelThreshold;
            bool opposite = _vel != 0 && Math.Sign(direction) != Math.Sign(_vel);

            if (opposite && meaningful)
            {
                // First opposite notch during real motion = stop command.
                // Start a smooth emergency brake and consume the notch (no
                // new-direction motion, nothing queued yet).
                _braking = true;
                _gliding = false;
                _brakeV0 = _vel;
                _brakeElapsed = 0.0;
                _brakeT = _brakeSec;
                _pendingAdvance = 0.0;
                _wheelResidual = 0.0;
                return;
            }

            // Normal case: advance the target and re-plan from live state.
            double baseTarget = _gliding ? _target : _pos;
            PlanTo(baseTarget + advance, _settleSec);
        }
    }

    public bool Tick()
    {
        double deltaPixels;
        bool stillWorking;

        lock (_lock)
        {
            long now = _sw.ElapsedMilliseconds;
            if (_lastTickMs < 0) _lastTickMs = now;
            double dt = (now - _lastTickMs) / 1000.0;
            _lastTickMs = now;
            if (dt <= 0) dt = 0.001;

            if (_braking)
            {
                _brakeElapsed += dt;
                double u = Math.Min(_brakeElapsed / _brakeT, 1.0);
                // smootherstep 6u^5-15u^4+10u^3: zero 1st AND 2nd derivative at
                // u=1, so velocity and acceleration both ease to zero at the stop.
                double s = u * u * u * (u * (u * 6.0 - 15.0) + 10.0);
                _vel = _brakeV0 * (1.0 - s);
                _pos += _vel * dt;
                _acc = 0.0;

                if (_brakeElapsed >= _brakeT)
                {
                    _vel = 0.0; _acc = 0.0; _braking = false;
                    if (Math.Abs(_pendingAdvance) > 0.0)
                    {
                        PlanTo(_pos + _pendingAdvance, _settleSec);
                        _pendingAdvance = 0.0;
                    }
                }
            }
            else if (_gliding)
            {
                _segElapsed += dt;
                double u = _segElapsed;
                if (u >= _segT)
                {
                    // Segment complete: land exactly on target, at rest.
                    _pos = _target; _vel = 0.0; _acc = 0.0; _gliding = false;
                }
                else
                {
                    double u2 = u * u, u3 = u2 * u, u4 = u3 * u, u5 = u4 * u;
                    _pos = _c0 + _c1 * u + _c2 * u2 + _c3 * u3 + _c4 * u4 + _c5 * u5;
                    _vel = _c1 + 2.0 * _c2 * u + 3.0 * _c3 * u2 + 4.0 * _c4 * u3 + 5.0 * _c5 * u4;
                    _acc = 2.0 * _c2 + 6.0 * _c3 * u + 12.0 * _c4 * u2 + 20.0 * _c5 * u3;
                }
            }

            deltaPixels = _pos - _lastEmitted;
            _lastEmitted = _pos;

            stillWorking = _braking || _gliding;
            if (!stillWorking)
            {
                // Recenter to avoid float drift over long sessions.
                _pos = 0.0; _lastEmitted = 0.0; _target = 0.0;
                _vel = 0.0; _acc = 0.0; _wheelResidual = 0.0;
            }
        }

        SmoothingMath.EmitWheelDelta(deltaPixels, ref _wheelResidual, _isHorizontal);
        return stillWorking;
    }

    public void Reset()
    {
        lock (_lock)
        {
            _pos = _vel = _acc = 0.0;
            _lastEmitted = 0.0; _wheelResidual = 0.0;
            _target = 0.0; _segElapsed = 0.0; _segT = 0.0; _gliding = false;
            _braking = false; _brakeElapsed = 0.0; _pendingAdvance = 0.0;
            _lastTickMs = -1;
            _accelMultiplier = 1.0; _lastInputMs = -1;
        }
    }
}

/// <summary>
/// Cadence: input-rate adaptive. Reads how fast you are actually spinning the
/// wheel and morphs its character. Isolated / slow notches settle crisply and
/// quickly (reading); fast flick-bursts build momentum and coast (long-page
/// travel). Same velocity-register core as our Flywheel, but the decay rate is
/// modulated by measured notch cadence rather than fixed, and it carries the
/// same emergency-brake reversal rule as Silk.
///
/// Distinct from Hybrid, which hands spring to coast on a fixed silence timer
/// regardless of how hard you scrolled; Cadence chooses the feel from the input.
/// </summary>
public sealed class CadenceSmoother : ISmoother
{
    private readonly object _lock = new();
    private readonly Stopwatch _sw = Stopwatch.StartNew();
    private readonly bool _isHorizontal;
    public CadenceSmoother(bool isHorizontal = false) { _isHorizontal = isHorizontal; }

    private double _vel, _pos, _lastEmitted, _wheelResidual;
    private long _lastTickMs = -1;
    private double _frictionK = 12.0;   // current decay rate (1/s), set per notch by cadence

    // Emergency brake (reversal).
    private bool _braking;
    private double _brakeV0, _brakeT, _brakeElapsed, _pendingImpulse;

    private long _lastInputMs = -1;
    private double _accelMultiplier = 1.0;

    // Tuning.
    private double _stepSize = 120.0;
    private double _kCrisp = 21.0, _kCoast = 6.0;   // decay-rate endpoints (1/s)
    private double _maxVelocity = 30000.0;
    private double _brakeSec = 0.06;
    private int _accelDeltaMs = 70;
    private double _accelMax = 7.0;
    private const double SlowGapMs = 280.0, FastGapMs = 45.0;
    private const double ReversalVelThreshold = 60.0;

    public void UpdateConfig(WheelConfig cfg)
    {
        lock (_lock)
        {
            _stepSize = cfg.StepSize;
            double crispMs = Math.Max(80.0, cfg.AnimationTimeMs * 0.6);
            double coastMs = Math.Max(200.0, cfg.AnimationTimeMs * 2.2);
            double ln = -Math.Log(0.01) * 1000.0; // "1% remaining", ~4.6 * 1000
            _kCrisp = ln / crispMs;
            _kCoast = ln / coastMs;
            _brakeSec = Math.Clamp(cfg.AnimationTimeMs / 1000.0 * 0.33, 0.04, 0.09);
            _maxVelocity = _stepSize * 250.0;
            _accelDeltaMs = cfg.AccelWindowMs;
            _accelMax = cfg.AccelBoost;
        }
    }

    public void EnqueueNotch(int direction)
    {
        lock (_lock)
        {
            long now = _sw.ElapsedMilliseconds;
            long gap = _lastInputMs > 0 ? now - _lastInputMs : long.MaxValue;

            if (_lastInputMs > 0 && (now - _lastInputMs) < _accelDeltaMs)
                _accelMultiplier = Math.Min(_accelMultiplier + 1, _accelMax);
            else
                _accelMultiplier = 1.0;
            _lastInputMs = now;

            // Cadence 0 (isolated, slow) -> 1 (fast burst).
            double c = Math.Clamp((SlowGapMs - gap) / (SlowGapMs - FastGapMs), 0.0, 1.0);
            _frictionK = _kCrisp + (_kCoast - _kCrisp) * c;

            // Calibrated impulse: distance per notch stays ~StepSize (integral
            // v0/k), so the crisp path lands quickly and the coast path travels far.
            double impulse = direction * _stepSize * _frictionK * _accelMultiplier;

            if (_braking)
            {
                _pendingImpulse += impulse;
                return;
            }

            bool meaningful = Math.Abs(_vel) > ReversalVelThreshold;
            bool opposite = _vel != 0 && Math.Sign(direction) != Math.Sign(_vel);
            if (opposite && meaningful)
            {
                _braking = true;
                _brakeV0 = _vel;
                _brakeElapsed = 0.0;
                _brakeT = _brakeSec;
                _pendingImpulse = 0.0;
                _wheelResidual = 0.0;
                return;
            }

            _vel = Math.Clamp(_vel + impulse, -_maxVelocity, _maxVelocity);
        }
    }

    public bool Tick()
    {
        double deltaPixels;
        bool stillWorking;

        lock (_lock)
        {
            long now = _sw.ElapsedMilliseconds;
            if (_lastTickMs < 0) _lastTickMs = now;
            double dt = (now - _lastTickMs) / 1000.0;
            _lastTickMs = now;
            if (dt <= 0) dt = 0.001;

            if (_braking)
            {
                _brakeElapsed += dt;
                double u = Math.Min(_brakeElapsed / _brakeT, 1.0);
                double s = u * u * u * (u * (u * 6.0 - 15.0) + 10.0);
                _vel = _brakeV0 * (1.0 - s);
                _pos += _vel * dt;
                if (_brakeElapsed >= _brakeT)
                {
                    _vel = 0.0; _braking = false;
                    if (Math.Abs(_pendingImpulse) > 0.0)
                    {
                        _vel = Math.Clamp(_pendingImpulse, -_maxVelocity, _maxVelocity);
                        _pendingImpulse = 0.0;
                    }
                }
            }
            else
            {
                if (Math.Abs(_vel) < 0.5) _vel = 0.0;
                _vel *= Math.Exp(-_frictionK * dt);
                _pos += _vel * dt;
            }

            deltaPixels = _pos - _lastEmitted;
            _lastEmitted = _pos;

            stillWorking = _braking || Math.Abs(_vel) > 0.5;
            if (!stillWorking)
            {
                _pos = 0.0; _lastEmitted = 0.0; _wheelResidual = 0.0;
            }
        }

        SmoothingMath.EmitWheelDelta(deltaPixels, ref _wheelResidual, _isHorizontal);
        return stillWorking;
    }

    public void Reset()
    {
        lock (_lock)
        {
            _vel = _pos = _lastEmitted = _wheelResidual = 0.0;
            _braking = false; _brakeElapsed = 0.0; _pendingImpulse = 0.0;
            _lastTickMs = -1; _accelMultiplier = 1.0; _lastInputMs = -1;
        }
    }
}
