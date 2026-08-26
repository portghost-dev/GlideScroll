using GlideScroll.Native;

namespace GlideScroll.Pipeline;

/// <summary>
/// State machine for the middle mouse button. Disambiguates a regular
/// middle-click (passes through unchanged) from a press-and-drag
/// (engages pan mode and suppresses the original click).
///
/// States:
///  Idle         - no middle activity
///  PressedPending - middle is down, waiting to see if motion happens
///  Panning      - confirmed pan, eating mouse moves and locking cursor
///  Coasting     - middle released with FlickMode on, momentum decaying
/// </summary>
public sealed class PanStateMachine
{
    public enum State { Idle, PressedPending, Panning, Coasting }

    private State _state = State.Idle;
    private Win32.POINT _lockedCursor;
    private long _pressedAtMs;
    private int _accumulatedMotion = 0;
    private readonly System.Diagnostics.Stopwatch _stopwatch = System.Diagnostics.Stopwatch.StartNew();

    private readonly PanController _pan;

    /// <summary>Pixels of motion required while pressed before we commit to pan mode.</summary>
    public int ActivationThreshold { get; set; } = 5;

    /// <summary>If pressed without moving for this long, treat as definite click and pass through (ms).</summary>
    public int ClickHoldTimeoutMs { get; set; } = 250;

    /// <summary>When true (default), the cursor visually stays pinned in place during
    /// active pan while the user's hand moves freely. When false, the cursor moves
    /// with the hand, useful for users who want to see where their pointer is.
    /// (Disambiguation phase always locks briefly to keep the click-vs-pan decision
    /// visually stable; only Panning state respects this toggle.)</summary>
    public bool LockCursor { get; set; } = true;

    public State CurrentState => _state;

    /// <summary>True whenever a middle-button interaction is in flight (pressed,
    /// panning, or coasting), i.e. not Idle. The pipeline uses this to keep
    /// feeding the state machine even if the foreground app or Pan.Enabled
    /// changes mid-pan, so it can never strand in Panning with the cursor pinned.</summary>
    public bool IsActive => _state != State.Idle;

    public PanStateMachine(PanController pan)
    {
        _pan = pan;
    }

    /// <summary>Process a middle-button-down. Returns whether to suppress the OS event.</summary>
    public HookResult OnMiddleDown(int x, int y)
    {
        // If we were coasting, any new input cancels it. Then we restart
        // the disambiguation as a fresh press.
        if (_state == State.Coasting)
            _pan.Cancel();

        _state = State.PressedPending;
        _lockedCursor = new Win32.POINT { X = x, Y = y };
        _pressedAtMs = _stopwatch.ElapsedMilliseconds;
        _accumulatedMotion = 0;

        // Suppress the original middle-down so apps don't see it yet. If
        // we end up deciding it was a click, we'll synthesize a real one.
        return HookResult.Suppress;
    }

    /// <summary>Process a middle-button-up. Returns whether to suppress the OS event.</summary>
    public HookResult OnMiddleUp(int x, int y)
    {
        switch (_state)
        {
            case State.PressedPending:
                // It was a click, not a pan. Replay a real middle-click
                // pair so the foreground app gets it normally.
                _state = State.Idle;
                Native.InputSynth.SendMiddleClick();
                return HookResult.Suppress; // suppress this real up; we just sent our own pair

            case State.Panning:
                // Engage coast or stop, depending on PanController.FlickMode.
                _pan.OnRelease();
                _state = _pan.FlickMode ? State.Coasting : State.Idle;
                return HookResult.Suppress;

            case State.Coasting:
                // Shouldn't normally happen (button is up during coast),
                // but be defensive.
                _state = State.Idle;
                return HookResult.PassThrough;

            default:
                return HookResult.PassThrough;
        }
    }

    /// <summary>Process a mouse-move event. Returns whether to suppress.</summary>
    public HookResult OnMove(int x, int y)
    {
        switch (_state)
        {
            case State.PressedPending:
            {
                int dx = x - _lockedCursor.X;
                int dy = y - _lockedCursor.Y;
                _accumulatedMotion += Math.Abs(dx) + Math.Abs(dy);

                if (_accumulatedMotion >= ActivationThreshold)
                {
                    _state = State.Panning;
                    // Feed the accumulated displacement to the pan controller
                    // so motion that happened during disambiguation isn't lost.
                    _pan.OnPanMotion(dx, dy);
                    if (LockCursor)
                    {
                        // Lock at original press position, cursor stays put.
                        Win32.SetCursorPos(_lockedCursor.X, _lockedCursor.Y);
                    }
                    else
                    {
                        // LockCursor off, let cursor jump from press point to
                        // the user's current hand position (a few pixels at
                        // most, since we just crossed the activation threshold).
                        // Anchor follows so subsequent dx is incremental.
                        Win32.SetCursorPos(x, y);
                        _lockedCursor.X = x;
                        _lockedCursor.Y = y;
                    }
                    return HookResult.Suppress;
                }

                // Below threshold: don't move the cursor, but also don't
                // forward the motion (so the click can still resolve cleanly).
                Win32.SetCursorPos(_lockedCursor.X, _lockedCursor.Y);
                return HookResult.Suppress;
            }

            case State.Panning:
            {
                int dx = x - _lockedCursor.X;
                int dy = y - _lockedCursor.Y;

                if (dx != 0 || dy != 0)
                {
                    _pan.OnPanMotion(dx, dy);
                    if (LockCursor)
                    {
                        // Re-lock cursor in place, the user's hand is moving
                        // but visually the pointer stays put.
                        Win32.SetCursorPos(_lockedCursor.X, _lockedCursor.Y);
                    }
                    else
                    {
                        // Cursor moves with the hand. We MUST call SetCursorPos
                        // explicitly: returning Suppress from a low-level mouse
                        // hook also freezes the cursor on modern Windows (the
                        // OS uses the hook return as part of cursor-update
                        // gating, not just message delivery). So we manually
                        // place the cursor at the user-reported position, and
                        // advance our anchor so the next dx is incremental.
                        Win32.SetCursorPos(x, y);
                        _lockedCursor.X = x;
                        _lockedCursor.Y = y;
                    }
                }
                return HookResult.Suppress;
            }

            default:
                return HookResult.PassThrough;
        }
    }

    /// <summary>Other input arrived (different button, wheel, etc.), cancel any coast.</summary>
    public void CancelCoast()
    {
        if (_state == State.Coasting)
        {
            _pan.Cancel();
            _state = State.Idle;
        }
    }

    /// <summary>Periodic check from the timer thread for click-hold timeout.</summary>
    public void TickCheckTimeout()
    {
        if (_state == State.PressedPending)
        {
            long age = _stopwatch.ElapsedMilliseconds - _pressedAtMs;
            if (age > ClickHoldTimeoutMs)
            {
                // Held too long without motion. Don't synthesize a click;
                // user is probably just resting their finger. Stay pending
                // until release. Most apps don't care about long middle holds.
                // (Intentional no-op for now; this hook is a place to
                // change behaviour if we discover an app that needs it.)
            }
        }
    }
}
