using System.Drawing;
using GlideScroll.Native;

namespace GlideScroll.UI;

/// <summary>
/// Small always-on-top, no-activate label that follows the mouse cursor during
/// a finder-tool capture, showing what will be added ("Will add: Notepad, 
/// exe"). It has to be a native window rather than an HTML element because it
/// must render over other applications' windows, wherever the user drags. It's
/// offset below-right of the cursor and the capture ignores GlideScroll's own
/// process, so it never interferes with the class/exe resolution.
/// </summary>
internal sealed class CaptureTooltip : Form
{
    private readonly Label _label;

    public CaptureTooltip()
    {
        FormBorderStyle = FormBorderStyle.None;
        ShowInTaskbar = false;
        TopMost = true;
        AutoSize = true;
        AutoSizeMode = AutoSizeMode.GrowAndShrink;
        StartPosition = FormStartPosition.Manual;
        // Matches the app's default --surface / --text so it feels in-theme.
        BackColor = Color.FromArgb(37, 37, 38);
        Padding = new Padding(9, 6, 9, 6);
        _label = new Label
        {
            AutoSize = true,
            ForeColor = Color.FromArgb(230, 230, 230),
            Font = new Font("Segoe UI", 9f),
        };
        Controls.Add(_label);
    }

    // Never take focus from the window being pointed at.
    protected override bool ShowWithoutActivation => true;

    protected override CreateParams CreateParams
    {
        get
        {
            var cp = base.CreateParams;
            cp.ExStyle |= Win32.WS_EX_TOOLWINDOW | Win32.WS_EX_NOACTIVATE;
            return cp;
        }
    }

    /// <summary>Update text and move the tooltip to just below-right of the
    /// cursor (physical screen coordinates), showing it without activation.</summary>
    public void ShowAt(string text, int screenX, int screenY)
    {
        _label.Text = text;
        if (!Visible) Win32.ShowWindow(Handle, Win32.SW_SHOWNA);
        Win32.SetWindowPos(Handle, Win32.HWND_TOPMOST, screenX + 16, screenY + 22, 0, 0,
            Win32.SWP_NOSIZE | Win32.SWP_NOACTIVATE | Win32.SWP_SHOWWINDOW);
    }

    public void HideTip()
    {
        if (Visible) Win32.ShowWindow(Handle, Win32.SW_HIDE);
    }
}
