using System.Runtime.InteropServices;

namespace GlideScroll.Native;

/// <summary>
/// All Win32 P/Invoke definitions used by GlideScroll.
/// Centralised so any P/Invoke change is in one place.
/// </summary>
internal static class Win32
{
    // ----- hook constants -----
    public const int WH_MOUSE_LL = 14;
    public const int HC_ACTION = 0;

    // ----- mouse message types (received in low-level hook wParam) -----
    public const int WM_MOUSEMOVE   = 0x0200;
    public const int WM_LBUTTONDOWN = 0x0201;
    public const int WM_LBUTTONUP   = 0x0202;
    public const int WM_RBUTTONDOWN = 0x0204;
    public const int WM_RBUTTONUP   = 0x0205;
    public const int WM_MBUTTONDOWN = 0x0207;
    public const int WM_MBUTTONUP   = 0x0208;
    public const int WM_MOUSEWHEEL  = 0x020A;
    public const int WM_XBUTTONDOWN = 0x020B;
    public const int WM_XBUTTONUP   = 0x020C;
    public const int WM_MOUSEHWHEEL = 0x020E;

    // ----- SendInput INPUT type / mouse flags -----
    public const uint INPUT_MOUSE = 0;

    public const uint MOUSEEVENTF_MIDDLEDOWN = 0x0020;
    public const uint MOUSEEVENTF_MIDDLEUP   = 0x0040;
    public const uint MOUSEEVENTF_WHEEL      = 0x0800;
    public const uint MOUSEEVENTF_HWHEEL     = 0x1000;

    /// <summary>
    /// Magic value placed in dwExtraInfo on synthetic events we generate,
    /// so our hook can recognise them and not re-process them.
    /// </summary>
    public const uint GLIDESCROLL_MAGIC = 0xCAFEBABE;

    // ----- structures -----
    [StructLayout(LayoutKind.Sequential)]
    public struct POINT
    {
        public int X;
        public int Y;
    }

    [StructLayout(LayoutKind.Sequential)]
    public struct MSLLHOOKSTRUCT
    {
        public POINT pt;
        public uint mouseData;     // wheel delta in HIWORD (signed); xbutton id for x-buttons
        public uint flags;
        public uint time;
        public IntPtr dwExtraInfo;
    }

    [StructLayout(LayoutKind.Sequential)]
    public struct MOUSEINPUT
    {
        public int dx;
        public int dy;
        public uint mouseData;
        public uint dwFlags;
        public uint time;
        public IntPtr dwExtraInfo;
    }

    [StructLayout(LayoutKind.Sequential)]
    public struct INPUT
    {
        public uint type;
        public InputUnion u;
    }

    [StructLayout(LayoutKind.Explicit)]
    public struct InputUnion
    {
        [FieldOffset(0)] public MOUSEINPUT mi;
        // padding to match KEYBDINPUT / HARDWAREINPUT size on x64
        [FieldOffset(0)] public KEYBDINPUT ki;
        [FieldOffset(0)] public HARDWAREINPUT hi;
    }

    [StructLayout(LayoutKind.Sequential)]
    public struct KEYBDINPUT
    {
        public ushort wVk;
        public ushort wScan;
        public uint dwFlags;
        public uint time;
        public IntPtr dwExtraInfo;
    }

    [StructLayout(LayoutKind.Sequential)]
    public struct HARDWAREINPUT
    {
        public uint uMsg;
        public ushort wParamL;
        public ushort wParamH;
    }

    // ----- delegates -----
    public delegate IntPtr LowLevelMouseProc(int nCode, IntPtr wParam, IntPtr lParam);

    // ----- imports -----
    [DllImport("user32.dll", SetLastError = true)]
    public static extern IntPtr SetWindowsHookEx(int idHook, LowLevelMouseProc lpfn, IntPtr hMod, uint dwThreadId);

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static extern bool UnhookWindowsHookEx(IntPtr hhk);

    [DllImport("user32.dll", SetLastError = true)]
    public static extern IntPtr CallNextHookEx(IntPtr hhk, int nCode, IntPtr wParam, IntPtr lParam);

    [DllImport("kernel32.dll", CharSet = CharSet.Auto, SetLastError = true)]
    public static extern IntPtr GetModuleHandle(string? lpModuleName);

    [DllImport("user32.dll", SetLastError = true)]
    public static extern unsafe uint SendInput(uint nInputs, INPUT* pInputs, int cbSize);

    [DllImport("user32.dll")]
    public static extern IntPtr GetForegroundWindow();

    [DllImport("user32.dll", SetLastError = true)]
    public static extern uint GetWindowThreadProcessId(IntPtr hWnd, out uint lpdwProcessId);

    // ----- process exe-name resolution (replaces Process.MainModule) -----
    // PROCESS_QUERY_LIMITED_INFORMATION (0x1000) is available since Vista
    // and works on more processes than the older PROCESS_QUERY_INFORMATION:
    // notably protected processes (services, antivirus, elevated apps).
    public const uint PROCESS_QUERY_LIMITED_INFORMATION = 0x1000;

    [DllImport("kernel32.dll", SetLastError = true)]
    public static extern IntPtr OpenProcess(uint access, bool inherit, uint pid);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static extern bool CloseHandle(IntPtr h);

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static extern bool QueryFullProcessImageName(
        IntPtr hProcess, uint flags, System.Text.StringBuilder lpExeName, ref int lpdwSize);

    /// <summary>Get the full executable path for the given pid, or empty string
    /// if the process is gone or inaccessible. Uses the least-privileged query
    /// (works against protected processes, never opens their memory).</summary>
    public static string GetProcessExePath(uint pid)
    {
        IntPtr h = OpenProcess(PROCESS_QUERY_LIMITED_INFORMATION, false, pid);
        if (h == IntPtr.Zero) return string.Empty;
        try
        {
            // MAX_PATH-equivalent buffer. Modern Windows supports longer paths
            // but the OS truncates exe names to MAX_PATH for legacy reasons.
            var sb = new System.Text.StringBuilder(260);
            int len = sb.Capacity;
            if (!QueryFullProcessImageName(h, 0, sb, ref len)) return string.Empty;
            return sb.ToString();
        }
        finally
        {
            CloseHandle(h);
        }
    }

    /// <summary>Get the executable filename (no path) for the given pid.
    /// Returns empty string if the process is gone or inaccessible.</summary>
    public static string GetProcessExeName(uint pid)
    {
        string full = GetProcessExePath(pid);
        if (full.Length == 0) return string.Empty;
        int slash = full.LastIndexOf('\\');
        return slash >= 0 ? full.Substring(slash + 1) : full;
    }

    // Async key state, used to detect Shift held during a wheel event for the
    // "Shift+wheel = horizontal" feature, and the left mouse button during the
    // Add-class finder-tool capture. High bit of return = currently down.
    public const int VK_SHIFT = 0x10;
    public const int VK_LBUTTON = 0x01;
    public const int VK_ESCAPE = 0x1B;

    [DllImport("user32.dll")]
    public static extern short GetAsyncKeyState(int vKey);

    /// <summary>True if Shift is currently held (either left or right).</summary>
    public static bool IsShiftDown() => (GetAsyncKeyState(VK_SHIFT) & 0x8000) != 0;

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static extern bool SetCursorPos(int X, int Y);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static extern bool GetCursorPos(out POINT lpPoint);

    // ----- window-under-cursor classification (scroll-target compatibility) -----
    // Some surfaces only honor whole-notch wheel input and drop anything finer,
    // so injected smooth wheel can never be smooth there (proven empirically:
    // 120 = native-equivalent discrete steps, anything smaller is silently
    // dropped). The fix is to NOT intercept at all and let Windows scroll them
    // natively. Those surfaces are identified by the root window class under the
    // cursor. The blocked-class set is config-driven now (the App rules list +
    // AppListConfig.DefaultBlocks) rather than hardcoded, so users can add or
    // remove their own.

    [DllImport("user32.dll")]
    public static extern IntPtr WindowFromPoint(POINT point);

    [DllImport("user32.dll")]
    public static extern IntPtr GetAncestor(IntPtr hwnd, uint gaFlags);

    [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    public static extern int GetClassName(IntPtr hWnd, System.Text.StringBuilder lpClassName, int nMaxCount);

    public const uint GA_ROOT = 2;

    /// <summary>Root (top-level) window handle under the mouse cursor, or
    /// IntPtr.Zero. Used both to classify the scroll target and by the Add-class
    /// finder tool (which skips GlideScroll's own window during capture).</summary>
    public static IntPtr CursorRootWindow()
    {
        if (!GetCursorPos(out POINT p)) return IntPtr.Zero;
        IntPtr hwnd = WindowFromPoint(p);
        if (hwnd == IntPtr.Zero) return IntPtr.Zero;
        IntPtr root = GetAncestor(hwnd, GA_ROOT);
        return root == IntPtr.Zero ? hwnd : root;
    }

    /// <summary>Window class name for a handle, or empty string.</summary>
    public static string ClassNameOf(IntPtr hwnd)
    {
        if (hwnd == IntPtr.Zero) return string.Empty;
        var sb = new System.Text.StringBuilder(256);
        if (GetClassName(hwnd, sb, sb.Capacity) == 0) return string.Empty;
        return sb.ToString();
    }

    /// <summary>Root window class under the mouse cursor, or empty string. This
    /// is exactly what the wheel pipeline matches its class rules against, so a
    /// class captured by the finder tool is what gets tested at scroll time.</summary>
    public static string CursorWindowRootClass() => ClassNameOf(CursorRootWindow());

    // ----- window enumeration (used for the tray submenu and apps tab picker) -----

    public delegate bool EnumWindowsProc(IntPtr hWnd, IntPtr lParam);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static extern bool EnumWindows(EnumWindowsProc lpEnumFunc, IntPtr lParam);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static extern bool IsWindowVisible(IntPtr hWnd);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    public static extern int GetWindowTextLength(IntPtr hWnd);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    public static extern int GetWindowText(IntPtr hWnd, System.Text.StringBuilder lpString, int nMaxCount);

    [DllImport("user32.dll", SetLastError = true)]
    public static extern int GetWindowLong(IntPtr hWnd, int nIndex);

    [DllImport("dwmapi.dll")]
    public static extern int DwmGetWindowAttribute(IntPtr hwnd, int dwAttribute, out int pvAttribute, int cbAttribute);

    public const int GWL_EXSTYLE = -20;
    public const int WS_EX_TOOLWINDOW = 0x00000080;
    public const int DWMWA_CLOAKED = 14;

    // No-activate extended style for the finder-tool tooltip so it can't steal
    // focus from the window being pointed at.
    public const int WS_EX_NOACTIVATE  = 0x08000000;

    // ----- DWM theming for the form's native title bar (Win11) -----
    public const int DWMWA_USE_IMMERSIVE_DARK_MODE = 20;
    public const int DWMWA_CAPTION_COLOR = 35;
    public const int DWMWA_TEXT_COLOR = 36;
    public const int DWMWA_BORDER_COLOR = 34;

    [DllImport("dwmapi.dll")]
    public static extern int DwmSetWindowAttribute(IntPtr hwnd, int attr, ref int attrValue, int attrSize);

    // ----- native Win32 popup menus -----
    public const uint MF_STRING = 0x00000000;
    public const uint MF_SEPARATOR = 0x00000800;
    public const uint MF_POPUP = 0x00000010;
    public const uint MF_GRAYED = 0x00000001;
    public const uint MF_BYCOMMAND = 0x00000000;

    public const uint TPM_LEFTBUTTON = 0x0000;
    public const uint TPM_RIGHTBUTTON = 0x0002;
    public const uint TPM_LEFTALIGN = 0x0000;
    public const uint TPM_RIGHTALIGN = 0x0008;
    public const uint TPM_TOPALIGN = 0x0000;
    public const uint TPM_BOTTOMALIGN = 0x0020;
    public const uint TPM_RETURNCMD = 0x0100;
    public const uint TPM_NONOTIFY = 0x0080;

    [DllImport("user32.dll")]
    public static extern IntPtr CreatePopupMenu();

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static extern bool DestroyMenu(IntPtr hMenu);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static extern bool AppendMenu(IntPtr hMenu, uint uFlags, UIntPtr uIDNewItem, string? lpNewItem);

    [DllImport("user32.dll")]
    public static extern uint TrackPopupMenuEx(IntPtr hMenu, uint fuFlags, int x, int y, IntPtr hwnd, IntPtr lptpm);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static extern bool SetForegroundWindow(IntPtr hWnd);

    // ----- frameless window dragging / resize / state sync -----
    public const int WM_NCLBUTTONDOWN = 0xA1;
    public const int WM_NCHITTEST     = 0x0084;
    public const int WM_NCCALCSIZE    = 0x0083;
    public const int WM_SIZE          = 0x0005;
    public const int WM_GETMINMAXINFO = 0x0024;
    public const int WM_MOUSEACTIVATE = 0x0021;
    public const int WM_ACTIVATEAPP   = 0x001C;

    public const int SIZE_RESTORED  = 0;
    public const int SIZE_MINIMIZED = 1;
    public const int SIZE_MAXIMIZED = 2;

    public const int HTCLIENT      = 1;
    public const int HTCAPTION     = 2;
    public const int HTLEFT        = 10;
    public const int HTRIGHT       = 11;
    public const int HTTOP         = 12;
    public const int HTTOPLEFT     = 13;
    public const int HTTOPRIGHT    = 14;
    public const int HTBOTTOM      = 15;
    public const int HTBOTTOMLEFT  = 16;
    public const int HTBOTTOMRIGHT = 17;

    public const int MA_NOACTIVATE = 3;

    // window styles
    public const int WS_THICKFRAME   = 0x00040000;
    public const int WS_MINIMIZEBOX  = 0x00020000;
    public const int WS_MAXIMIZEBOX  = 0x00010000;
    public const int WS_CAPTION      = 0x00C00000;

    // class styles
    public const int CS_DROPSHADOW = 0x00020000;

    // ShowWindow commands
    public const int SW_HIDE            = 0;
    public const int SW_SHOWNOACTIVATE  = 4;
    public const int SW_SHOW            = 5;
    public const int SW_SHOWNA          = 8;

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static extern bool ReleaseCapture();

    // Mouse capture, held on the settings form during finder-tool capture so
    // stray button events don't leak to the window being pointed at.
    [DllImport("user32.dll")]
    public static extern IntPtr SetCapture(IntPtr hWnd);

    [DllImport("user32.dll", CharSet = CharSet.Auto)]
    public static extern IntPtr SendMessage(IntPtr hWnd, uint Msg, IntPtr wParam, IntPtr lParam);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static extern bool ShowWindow(IntPtr hWnd, int nCmdShow);

    // Reposition the follow-cursor finder tooltip in physical screen pixels
    // (bypasses WinForms DPI translation on Location).
    public static readonly IntPtr HWND_TOPMOST = new(-1);
    public const uint SWP_NOSIZE     = 0x0001;
    public const uint SWP_NOACTIVATE = 0x0010;
    public const uint SWP_SHOWWINDOW = 0x0040;

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static extern bool SetWindowPos(IntPtr hWnd, IntPtr hWndInsertAfter, int X, int Y, int cx, int cy, uint uFlags);

    /// <summary>Get monitor rectangle that fully or mostly contains the given point.</summary>
    [DllImport("user32.dll")]
    public static extern IntPtr MonitorFromPoint(POINT pt, uint dwFlags);

    public const uint MONITOR_DEFAULTTONEAREST = 2;

    [StructLayout(LayoutKind.Sequential)]
    public struct RECT { public int Left, Top, Right, Bottom; }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    public struct MONITORINFO
    {
        public uint cbSize;
        public RECT rcMonitor;
        public RECT rcWork;
        public uint dwFlags;
    }

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static extern bool GetMonitorInfo(IntPtr hMonitor, ref MONITORINFO lpmi);

    // ----- Game Mode auto-pause detection -----
    // Everything here is external, read-only, and user-mode: window geometry
    // and style for the hovered window, the shell's exclusive-fullscreen state,
    // and SCM driver-service status for anti-cheat kernel drivers. No handle is
    // ever opened into a game process beyond the limited-info name query above.

    public const int GWL_STYLE = -16;

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static extern bool GetWindowRect(IntPtr hWnd, out RECT lpRect);

    // SHQueryUserNotificationState: the same signal Focus Assist uses to mute
    // notifications during games. QUNS_RUNNING_D3D_FULL_SCREEN (3) means an
    // exclusive-fullscreen Direct3D app is running somewhere in the session.
    public const int QUNS_RUNNING_D3D_FULL_SCREEN = 3;

    [DllImport("shell32.dll")]
    public static extern int SHQueryUserNotificationState(out int pquns);

    // Service Control Manager, status query only. Detects anti-cheat kernel
    // drivers that have no user-mode process (XIGNCODE's xhunter1, GameGuard's
    // npggNT). SC_MANAGER_CONNECT + SERVICE_QUERY_STATUS work without admin.
    public const uint SC_MANAGER_CONNECT = 0x0001;
    public const uint SERVICE_QUERY_STATUS = 0x0004;
    public const uint SERVICE_RUNNING = 0x00000004;

    [StructLayout(LayoutKind.Sequential)]
    public struct SERVICE_STATUS
    {
        public uint dwServiceType;
        public uint dwCurrentState;
        public uint dwControlsAccepted;
        public uint dwWin32ExitCode;
        public uint dwServiceSpecificExitCode;
        public uint dwCheckPoint;
        public uint dwWaitHint;
    }

    [DllImport("advapi32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    public static extern IntPtr OpenSCManager(string? machineName, string? databaseName, uint dwAccess);

    [DllImport("advapi32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    public static extern IntPtr OpenService(IntPtr hSCManager, string lpServiceName, uint dwDesiredAccess);

    [DllImport("advapi32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static extern bool QueryServiceStatus(IntPtr hService, out SERVICE_STATUS lpServiceStatus);

    [DllImport("advapi32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static extern bool CloseServiceHandle(IntPtr hSCObject);

    /// <summary>True if the named Windows service (including driver services)
    /// is currently RUNNING. Safe for standard users; false on any failure.</summary>
    public static bool IsServiceRunning(string serviceName)
    {
        IntPtr scm = OpenSCManager(null, null, SC_MANAGER_CONNECT);
        if (scm == IntPtr.Zero) return false;
        try
        {
            IntPtr svc = OpenService(scm, serviceName, SERVICE_QUERY_STATUS);
            if (svc == IntPtr.Zero) return false;
            try
            {
                return QueryServiceStatus(svc, out SERVICE_STATUS status)
                       && status.dwCurrentState == SERVICE_RUNNING;
            }
            finally { CloseServiceHandle(svc); }
        }
        finally { CloseServiceHandle(scm); }
    }

    // ----- display settings (refresh rate detection) -----
    public const int ENUM_CURRENT_SETTINGS = -1;

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    public struct DEVMODE
    {
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 32)] public string dmDeviceName;
        public ushort dmSpecVersion;
        public ushort dmDriverVersion;
        public ushort dmSize;
        public ushort dmDriverExtra;
        public uint dmFields;
        public int dmPositionX;
        public int dmPositionY;
        public uint dmDisplayOrientation;
        public uint dmDisplayFixedOutput;
        public short dmColor;
        public short dmDuplex;
        public short dmYResolution;
        public short dmTTOption;
        public short dmCollate;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 32)] public string dmFormName;
        public ushort dmLogPixels;
        public uint dmBitsPerPel;
        public uint dmPelsWidth;
        public uint dmPelsHeight;
        public uint dmDisplayFlags;
        public uint dmDisplayFrequency;
        public uint dmICMMethod;
        public uint dmICMIntent;
        public uint dmMediaType;
        public uint dmDitherType;
        public uint dmReserved1;
        public uint dmReserved2;
        public uint dmPanningWidth;
        public uint dmPanningHeight;
    }

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static extern bool EnumDisplaySettings(string? deviceName, int modeNum, ref DEVMODE devMode);

    // ----- multimedia timer resolution -----
    // Default Windows timer resolution is ~15.6ms, which makes Thread.Sleep(N)
    // overshoot by up to that amount. Calling timeBeginPeriod(1) raises system
    // timer resolution to 1ms so the pipeline thread can hit its target tick
    // rate without burning CPU on long SpinWait loops. Pair with timeEndPeriod
    // on shutdown to release the global resolution boost.

    [DllImport("winmm.dll", EntryPoint = "timeBeginPeriod")]
    public static extern uint TimeBeginPeriod(uint uPeriod);

    [DllImport("winmm.dll", EntryPoint = "timeEndPeriod")]
    public static extern uint TimeEndPeriod(uint uPeriod);

    // ----- helpers -----

    /// <summary>Extract signed 16-bit high word from MSLLHOOKSTRUCT.mouseData (wheel delta).</summary>
    public static short HiWord(uint value) => (short)((value >> 16) & 0xFFFF);
}
