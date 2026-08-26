using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;

namespace GlideScroll.Native;

/// <summary>
/// Type of low-level mouse event reaching our hook. Mirrors a subset of
/// Win32 WM_* messages we actually act on.
/// </summary>
public enum MouseEventType
{
    Move,
    LeftDown, LeftUp,
    RightDown, RightUp,
    MiddleDown, MiddleUp,
    XDown, XUp,
    Wheel,        // vertical wheel
    HWheel        // horizontal wheel
}

/// <summary>
/// Decoded event passed to handlers. WheelDelta carries the signed
/// 120-per-notch value for Wheel/HWheel events; Position is screen-coords.
/// </summary>
public readonly struct MouseEvent
{
    public readonly MouseEventType Type;
    public readonly int X;
    public readonly int Y;
    public readonly int WheelDelta;
    public readonly bool IsSynthetic;

    public MouseEvent(MouseEventType type, int x, int y, int wheelDelta, bool isSynthetic)
    {
        Type = type;
        X = x;
        Y = y;
        WheelDelta = wheelDelta;
        IsSynthetic = isSynthetic;
    }
}

/// <summary>
/// Result returned by an event handler: PassThrough lets the OS/app see
/// the original event normally; Suppress drops it so only our synthetic
/// events reach the foreground app.
/// </summary>
public enum HookResult
{
    PassThrough,
    Suppress
}

/// <summary>
/// Installs a WH_MOUSE_LL hook and delegates each event to a handler.
/// Must be created on a thread with a message pump (the WinForms UI thread
/// is fine), Windows pumps hook callbacks through that thread's queue.
/// </summary>
public sealed class MouseHook : IDisposable
{
    private readonly Func<MouseEvent, HookResult> _handler;
    private IntPtr _hookId = IntPtr.Zero;

    // Field, not local, so the GC doesn't collect the delegate while the
    // unmanaged hook still holds a reference to it.
    private readonly Win32.LowLevelMouseProc _proc;

    public MouseHook(Func<MouseEvent, HookResult> handler)
    {
        _handler = handler ?? throw new ArgumentNullException(nameof(handler));
        _proc = HookCallback;
    }

    public void Install()
    {
        if (_hookId != IntPtr.Zero) return;

        // GetModuleHandle(null) returns the current process's HMODULE
        // directly. WH_MOUSE_LL doesn't actually use it to load a DLL,
        // it just needs a valid module handle to satisfy the API contract.
        IntPtr hMod = Win32.GetModuleHandle(null);

        _hookId = Win32.SetWindowsHookEx(Win32.WH_MOUSE_LL, _proc, hMod, 0);
        if (_hookId == IntPtr.Zero)
            throw new InvalidOperationException(
                $"SetWindowsHookEx failed (error {Marshal.GetLastWin32Error()}).");
    }

    public void Uninstall()
    {
        if (_hookId == IntPtr.Zero) return;
        Win32.UnhookWindowsHookEx(_hookId);
        _hookId = IntPtr.Zero;
    }

    private unsafe IntPtr HookCallback(int nCode, IntPtr wParam, IntPtr lParam)
    {
        // Only act on HC_ACTION (>= 0). Anything else: chain through.
        if (nCode != Win32.HC_ACTION)
            return Win32.CallNextHookEx(_hookId, nCode, wParam, lParam);

        // Read the MSLLHOOKSTRUCT directly via pointer instead of going through
        // Marshal.PtrToStructure. Saves an allocation + reflection-based
        // marshalling on every mouse event (1000+ Hz on modern mice). The
        // struct is blittable [Sequential] so the read is a simple memcpy.
        ref var raw = ref Unsafe.AsRef<Win32.MSLLHOOKSTRUCT>((void*)lParam);
        bool synthetic = (uint)raw.dwExtraInfo.ToInt64() == Win32.GLIDESCROLL_MAGIC;

        // Always pass our own synthetic events through untouched.
        if (synthetic)
            return Win32.CallNextHookEx(_hookId, nCode, wParam, lParam);

        int msg = wParam.ToInt32();
        MouseEventType? type = msg switch
        {
            Win32.WM_MOUSEMOVE   => MouseEventType.Move,
            Win32.WM_LBUTTONDOWN => MouseEventType.LeftDown,
            Win32.WM_LBUTTONUP   => MouseEventType.LeftUp,
            Win32.WM_RBUTTONDOWN => MouseEventType.RightDown,
            Win32.WM_RBUTTONUP   => MouseEventType.RightUp,
            Win32.WM_MBUTTONDOWN => MouseEventType.MiddleDown,
            Win32.WM_MBUTTONUP   => MouseEventType.MiddleUp,
            Win32.WM_XBUTTONDOWN => MouseEventType.XDown,
            Win32.WM_XBUTTONUP   => MouseEventType.XUp,
            Win32.WM_MOUSEWHEEL  => MouseEventType.Wheel,
            Win32.WM_MOUSEHWHEEL => MouseEventType.HWheel,
            _ => null
        };

        // Unknown message: chain through.
        if (type is null)
            return Win32.CallNextHookEx(_hookId, nCode, wParam, lParam);

        int delta = (type == MouseEventType.Wheel || type == MouseEventType.HWheel)
            ? Win32.HiWord(raw.mouseData)
            : 0;

        var ev = new MouseEvent(type.Value, raw.pt.X, raw.pt.Y, delta, false);

        HookResult result;
        try
        {
            result = _handler(ev);
        }
        catch
        {
            // Never let a handler exception kill the hook chain, pass
            // through and let the rest of the system proceed.
            result = HookResult.PassThrough;
        }

        return result == HookResult.Suppress
            ? new IntPtr(1)
            : Win32.CallNextHookEx(_hookId, nCode, wParam, lParam);
    }

    public void Dispose() => Uninstall();
}
