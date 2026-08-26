using GlideScroll.Native;

namespace GlideScroll.Config;

/// <summary>Reads the primary display's current refresh rate via Win32.</summary>
public static class DisplayInfo
{
    /// <summary>Returns refresh rate in Hz of the primary display, or 60 if unknown.</summary>
    public static int GetPrimaryRefreshRate()
    {
        var dm = QueryPrimary();
        int hz = (int)dm.dmDisplayFrequency;
        return (hz > 0 && hz < 1000) ? hz : 60;
    }

    /// <summary>Returns the primary display's pixel resolution (width, height),
    /// or (1920, 1080) if the query fails.</summary>
    public static (int widthPx, int heightPx) GetPrimaryResolution()
    {
        var dm = QueryPrimary();
        int w = (int)dm.dmPelsWidth;
        int h = (int)dm.dmPelsHeight;
        if (w <= 0 || h <= 0) return (1920, 1080);
        return (w, h);
    }

    private static Win32.DEVMODE QueryPrimary()
    {
        var dm = new Win32.DEVMODE
        {
            dmSize = (ushort)System.Runtime.InteropServices.Marshal.SizeOf<Win32.DEVMODE>()
        };
        try { Win32.EnumDisplaySettings(null, Win32.ENUM_CURRENT_SETTINGS, ref dm); }
        catch { }
        return dm;
    }

    /// <summary>Reference PPI: a 27" 1440p display. Step size of 120 maps here.</summary>
    private const double ReferencePpi = 109.0;

    /// <summary>
    /// Compute recommended wheel tuning per smoothing mode. Step size is
    /// universal (PPI-scaled from a 27"/1440p reference of 109 PPI → step 120),
    /// because "physical scroll distance per notch" is a function of display
    /// density, not algorithm. Animation/glide duration IS mode-specific
    /// because each algorithm has a different sweet spot for what "feels right":
    ///   - Flywheel: 350-450ms coast feels best, long enough to glide
    ///     noticeably, short enough not to feel mushy.
    ///   - Spring: 200-280ms settle, snappy without feeling abrupt.
    ///   - Hybrid: 280-380ms total budget (auto-split spring/coast).
    ///   - LowLatency: 50-80ms lerp, pure responsiveness, capped intentionally.
    /// </summary>
    public static (int animationTimeMs, int stepSize, double ppi) RecommendedFor(
        int hz, int widthPx, int heightPx, double diagonalInches, SmoothingMode mode)
    {
        // Per-mode glide duration. Each mode has a different sweet spot for
        // what "feels right":
        //   Flywheel: 350-450ms coast, long enough to glide, short enough not
        //     to feel mushy.
        //   Spring: 200-280ms settle, snappy without feeling abrupt.
        //   Hybrid: 280-380ms total budget (auto-split spring/coast).
        //   LowLatency: 50-80ms lerp, pure responsiveness, capped intentionally.
        //   Silk: ~150ms bounded settle, smooth but no creep tail.
        //   Cadence: ~300ms baseline; adapts crisp/coast from input rate.
        int anim = mode switch
        {
            SmoothingMode.Flywheel   => 400,
            SmoothingMode.Spring     => 250,
            SmoothingMode.Hybrid     => 320,
            SmoothingMode.LowLatency => 60,
            SmoothingMode.Silk       => 150,
            SmoothingMode.Cadence    => 300,
            _ => 360
        };

        // PPI-scaled step is universal, physical scroll distance per notch
        // should feel the same across monitors regardless of which engine.
        double ppi = ReferencePpi;
        if (diagonalInches > 0 && widthPx > 0 && heightPx > 0)
        {
            double diagPx = Math.Sqrt((double)widthPx * widthPx + (double)heightPx * heightPx);
            ppi = diagPx / diagonalInches;
        }

        int step = (int)Math.Round(120.0 * ppi / ReferencePpi);
        step = Math.Clamp(step, 60, 240);

        return (anim, step, ppi);
    }

    /// <summary>
    /// Compute recommended pan tuning from the native pixel resolution and
    /// user-supplied physical diagonal. PPI scaling is the only meaningful
    /// monitor-aware tuning for pan, all three numerics (sensitivity,
    /// activation threshold, min step) are pixel quantities that should map
    /// to consistent *physical* distances regardless of display density.
    ///
    /// Refresh rate is intentionally ignored, pan motion is continuous and
    /// integrated in real time at the pipeline tick rate; refresh rate
    /// doesn't change the perceived feel meaningfully.
    ///
    /// Friction is intentionally NOT tuned, it's a time-based decay
    /// constant, decoupled from display characteristics, and best left as
    /// a user-driven feel preference.
    ///
    /// Same 27" 1440p reference (109 PPI) as the wheel calibration:
    ///   - Sensitivity: 90 at reference (1.34× scroll-to-cursor), scales
    ///     linearly with PPI ratio, clamped [30, 250].
    ///   - ActivationThreshold: 5px at reference, scales with PPI ratio,
    ///     clamped [2, 20], so activation distance is physically consistent
    ///     across displays (not "5px on 4K = half a millimeter").
    ///   - MinStepPixels: 5px at reference, same scaling logic.
    /// </summary>
    public static (double sensitivity, int activation, int minStep, double ppi) RecommendedPanFor(
        int widthPx, int heightPx, double diagonalInches)
    {
        double ppi = ReferencePpi;
        if (diagonalInches > 0 && widthPx > 0 && heightPx > 0)
        {
            double diagPx = Math.Sqrt((double)widthPx * widthPx + (double)heightPx * heightPx);
            ppi = diagPx / diagonalInches;
        }

        double ratio = ppi / ReferencePpi;

        double sensitivity = Math.Clamp(Math.Round(100.0 * ratio), 30.0, 250.0);
        int activation     = Math.Clamp((int)Math.Round(5.0 * ratio), 2, 20);
        // MinStep base is 1 (per-pixel emit), modern apps render precision
        // deltas smoothly. PPI scaling keeps the physical jitter floor
        // consistent: 1px on reference, 2px on 4K (still imperceptibly small).
        int minStep        = Math.Clamp((int)Math.Round(1.0 * ratio), 1, 5);

        return (sensitivity, activation, minStep, ppi);
    }
}
