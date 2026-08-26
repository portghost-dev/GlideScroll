using System.Runtime.InteropServices;

namespace GlideScroll.Native;

/// <summary>
/// Wrapper around SendInput for emitting synthetic mouse events. All
/// events emitted here are tagged with GLIDESCROLL_MAGIC in dwExtraInfo
/// so our hook can ignore them on the way back through.
/// </summary>
public static class InputSynth
{
    // Cached struct size, was previously Marshal.SizeOf<INPUT>() on every
    // call which reflects the type each time. INPUT is blittable so its
    // size is fixed and known at JIT time via sizeof().
    private static readonly unsafe int InputSize = sizeof(Win32.INPUT);

    // Synthetic-event tag. The 0xCAFEBABE constant requires unchecked when
    // converted to nint on 64-bit because the value has the high bit set
    // for 32-bit, even though it fits trivially in 64. Computing once here
    // also avoids the per-call (IntPtr)(long) cast chain.
    private static readonly IntPtr SyntheticTag = unchecked((IntPtr)(long)Win32.GLIDESCROLL_MAGIC);

    /// <summary>Send a vertical wheel event. Delta is in WHEEL_DELTA units (120 per notch).</summary>
    public static unsafe void SendVerticalWheel(int delta)
    {
        Win32.INPUT input = default;
        input.type = Win32.INPUT_MOUSE;
        input.u.mi = new Win32.MOUSEINPUT
        {
            mouseData = (uint)delta,
            dwFlags = Win32.MOUSEEVENTF_WHEEL,
            dwExtraInfo = SyntheticTag
        };
        Win32.SendInput(1, &input, InputSize);
    }

    /// <summary>Send a horizontal wheel event. Delta in WHEEL_DELTA units (120 per notch).</summary>
    public static unsafe void SendHorizontalWheel(int delta)
    {
        Win32.INPUT input = default;
        input.type = Win32.INPUT_MOUSE;
        input.u.mi = new Win32.MOUSEINPUT
        {
            mouseData = (uint)delta,
            dwFlags = Win32.MOUSEEVENTF_HWHEEL,
            dwExtraInfo = SyntheticTag
        };
        Win32.SendInput(1, &input, InputSize);
    }

    /// <summary>Send a synthetic middle button down+up pair, used when we
    /// suppressed a real middle-click while disambiguating click vs. pan
    /// and decided after the fact it was just a click.</summary>
    public static unsafe void SendMiddleClick()
    {
        Win32.INPUT* inputs = stackalloc Win32.INPUT[2];
        inputs[0] = default;
        inputs[0].type = Win32.INPUT_MOUSE;
        inputs[0].u.mi = new Win32.MOUSEINPUT
        {
            dwFlags = Win32.MOUSEEVENTF_MIDDLEDOWN,
            dwExtraInfo = SyntheticTag
        };
        inputs[1] = default;
        inputs[1].type = Win32.INPUT_MOUSE;
        inputs[1].u.mi = new Win32.MOUSEINPUT
        {
            dwFlags = Win32.MOUSEEVENTF_MIDDLEUP,
            dwExtraInfo = SyntheticTag
        };
        Win32.SendInput(2, inputs, InputSize);
    }
}
