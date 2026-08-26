using GlideScroll.Config;

namespace GlideScroll.Pipeline;

/// <summary>
/// Common interface for all wheel-smoothing engines. ScrollPipeline holds a
/// dictionary keyed by <see cref="SmoothingMode"/> and routes input notches
/// to whichever implementation matches the user's current selection.
///
/// Lifecycle:
///   - Construction: implementations create internal state.
///   - UpdateConfig: called once on startup and on every config change.
///   - EnqueueNotch: called on the hook thread when a real wheel event arrives
///     (post-acceleration logic is implementation-specific).
///   - Tick: called on the pipeline timer thread (refresh-matched, 60-360Hz).
///     Returns true while
///     there is still motion to emit; false when settled.
///   - Reset: called when the user switches modes mid-scroll, so leftover
///     state from one engine doesn't leak into the next.
/// </summary>
public interface ISmoother
{
    /// <summary>Apply latest wheel-config values. Safe to call any time.</summary>
    void UpdateConfig(WheelConfig cfg);

    /// <summary>Queue one real wheel notch. direction = +1 (up) or -1 (down).</summary>
    void EnqueueNotch(int direction);

    /// <summary>Advance physics one timer tick. Emits synthetic wheel events
    /// via InputSynth when delta accumulates to ≥1 wheel unit. Returns true
    /// if there is still pending motion (caller should keep ticking).</summary>
    bool Tick();

    /// <summary>Clear all internal state. Used when user switches modes.</summary>
    void Reset();
}

/// <summary>
/// Shared math helpers used across multiple smoothers.
/// </summary>
internal static class SmoothingMath
{
    /// <summary>
    /// Convert pixel-space scroll delta to Win32 wheel-delta units (120 per
    /// notch convention) and emit through InputSynth. Carries a sub-unit
    /// residual across calls so fractional values aren't rounded away , 
    /// without this, the slow-start of any smoothing curve stutters because
    /// sub-pixel deltas round to zero each tick.
    /// <paramref name="horizontal"/> selects which wheel axis the synthesized
    /// event targets (default: vertical, the common case).
    /// </summary>
    public static void EmitWheelDelta(double pixelDelta, ref double residual, bool horizontal = false)
    {
        // Always accumulate, even infinitesimal deltas contribute to the
        // running residual and eventually emit. The engine's settle threshold
        // catches truly stopped motion before pixelDelta gets pathological.
        residual += pixelDelta * 120.0 / 100.0;
        int wheelDelta = (int)residual;
        if (wheelDelta != 0)
        {
            residual -= wheelDelta;
            if (horizontal) Native.InputSynth.SendHorizontalWheel(wheelDelta);
            else Native.InputSynth.SendVerticalWheel(wheelDelta);
        }
    }

    /// <summary>
    /// Generic 2nd-order spring with tunable damping ratio (symplectic Euler).
    /// Updates <paramref name="current"/> and <paramref name="velocity"/> in place.
    ///   - dampingRatio = 1.0 → critically damped (no overshoot, fastest)
    ///   - dampingRatio &lt; 1.0 → underdamped (oscillates around target)
    ///   - dampingRatio &gt; 1.0 → overdamped (slow approach, no overshoot)
    /// <paramref name="smoothTime"/> sets the natural frequency: omega = 2/T.
    /// Stable for omega·dt &lt; ~1.5, fine across 60-360Hz with smoothTime ≥ 0.05s.
    /// </summary>
    public static void Spring2ndOrder(
        ref double current, ref double velocity,
        double target, double smoothTime, double dampingRatio, double dt)
    {
        if (smoothTime < 0.001) { current = target; velocity = 0; return; }
        double omega = 2.0 / smoothTime;
        double accel = -omega * omega * (current - target) - 2.0 * dampingRatio * omega * velocity;
        // Symplectic Euler: update velocity first (using new accel), then position
        // using new velocity. More stable than explicit Euler for oscillators.
        velocity += accel * dt;
        current += velocity * dt;
    }
}
