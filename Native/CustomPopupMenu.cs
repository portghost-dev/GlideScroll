using System.Drawing.Drawing2D;
using System.Drawing.Text;

namespace GlideScroll.Native;

/// <summary>
/// Frameless GDI+ popup menu with rounded corners, drop shadow, hover highlight,
/// and submenu support, visually close to LM Studio / Win11 modern menus.
/// Uses a regular Form (not Win32 menu) so we own every pixel of the rendering;
/// the OS still handles top-most behavior and click-outside dismissal via the
/// Form's Deactivate event. Submenus are shown with SW_SHOWNOACTIVATE so the
/// parent retains focus and the chain only dismisses on a true click-outside.
///
/// API mirrors the previous NativePopupMenu so swapping is mechanical:
///   var m = new CustomPopupMenu();
///   m.AddItem("Settings...", () =&gt; ..., bold: true);
///   m.AddSeparator();
///   m.AddSubmenu("Disable for app", sub =&gt; { ... });
///   m.Show(x, y);
/// </summary>
public sealed class CustomPopupMenu : IDisposable
{
    // ----- visual constants (Win11-flavored dark) -----
    private static readonly Color BgColor      = Color.FromArgb(43, 43, 43);   // ~#2B2B2B
    private static readonly Color BorderColor  = Color.FromArgb(64, 64, 64);
    private static readonly Color TextColor    = Color.FromArgb(240, 240, 240);
    private static readonly Color TextDisabled = Color.FromArgb(120, 120, 120);
    private static readonly Color HoverBg      = Color.FromArgb(60, 60, 60);
    private static readonly Color SeparatorCl  = Color.FromArgb(64, 64, 64);

    private const int CornerRadius = 8;
    private const int OuterPadX    = 4;     // horizontal padding inside the popup
    private const int OuterPadY    = 4;     // top/bottom padding
    private const int ItemHeight   = 30;
    private const int SeparatorH   = 9;
    private const int ItemPadX     = 12;
    private const int ChevronW     = 14;
    private const int MinWidth     = 180;
    private const int MaxWidth     = 360;
    private const int SubmenuHoverDelayMs = 220;

    // ----- model -----
    private enum ItemKind { Standard, Separator, Submenu }

    private sealed class MenuItem
    {
        public ItemKind Kind;
        public string Text = "";
        public bool Enabled = true;
        public bool Bold;
        public Action? OnClick;
        public CustomPopupMenu? Submenu;
    }

    private readonly List<MenuItem> _items = new();
    private MenuPopupForm? _form;
    private CustomPopupMenu? _activeSubmenu;
    private CustomPopupMenu? _parent;
    private bool _disposed;

    // ----- public API -----
    public void AddItem(string text, Action onClick, bool enabled = true, bool bold = false)
    {
        _items.Add(new MenuItem
        {
            Kind = ItemKind.Standard,
            Text = text,
            Enabled = enabled,
            Bold = bold,
            OnClick = onClick
        });
    }

    public void AddSeparator()
    {
        _items.Add(new MenuItem { Kind = ItemKind.Separator });
    }

    public void AddSubmenu(string text, Action<CustomPopupMenu> build, bool enabled = true)
    {
        var child = new CustomPopupMenu { _parent = this };
        build(child);
        _items.Add(new MenuItem
        {
            Kind = ItemKind.Submenu,
            Text = text,
            Enabled = enabled,
            Submenu = child
        });
    }

    /// <summary>Show the menu at screen coordinates. Modal-feeling but non-blocking;
    /// uses Application.DoEvents loop until dismissed.</summary>
    public void Show(int x, int y)
    {
        ShowInternal(x, y, isSubmenu: false);
    }

    private void ShowInternal(int x, int y, bool isSubmenu)
    {
        _form?.Close();
        var form = new MenuPopupForm(this);
        _form = form;

        // Set Owner BEFORE Build, Build creates the handle (via CreateGraphics),
        // and we want the handle to be created with the owner relationship in
        // place via CreateParams.Parent. Setting Owner after handle creation goes
        // through SetWindowLong(GWL_HWNDPARENT), which is less reliable for
        // changing activation behavior on an already-realized window.
        if (isSubmenu)
        {
            var rootForm = GetRootForm();
            if (rootForm is not null && !rootForm.IsDisposed && rootForm.IsHandleCreated)
            {
                form.Owner = rootForm;
            }
        }

        form.Build(_items);
        form.PositionAt(x, y);

        if (isSubmenu)
        {
            form.ShowNoActivate();
        }
        else
        {
            form.Show();
            form.Activate();
            while (!form.IsDisposed && form.Visible)
            {
                Application.DoEvents();
                Thread.Sleep(8);
            }
        }
    }

    private MenuPopupForm? GetRootForm()
    {
        var current = this;
        while (current._parent is not null) current = current._parent;
        return current._form;
    }

    // Walk the active-submenu chain (root → deepest open submenu) and return
    // true if the given foreground hwnd is one of those forms. Used by the
    // root's deferred OnDeactivate check so transient focus shifts within our
    // own popup chain don't dismiss the menu.
    private bool IsForegroundOwnedByChain(IntPtr hwnd)
    {
        if (hwnd == IntPtr.Zero) return false;
        var node = this;
        while (node is not null)
        {
            if (node._form is { IsHandleCreated: true, IsDisposed: false } f
                && f.Handle == hwnd)
            {
                return true;
            }
            node = node._activeSubmenu;
        }
        return false;
    }

    private void OnItemHovered(MenuItem item, Rectangle screenRect)
    {
        // Close any open submenu that doesn't belong to the new hovered item.
        if (_activeSubmenu is not null && (item.Kind != ItemKind.Submenu || _activeSubmenu != item.Submenu))
        {
            _activeSubmenu.CloseChain();
            _activeSubmenu = null;
        }

        if (item.Kind == ItemKind.Submenu && item.Enabled && _activeSubmenu != item.Submenu)
        {
            _form?.ScheduleSubmenuOpen(item, SubmenuHoverDelayMs, () =>
            {
                if (_disposed || item.Submenu is null) return;
                _activeSubmenu = item.Submenu;
                int sx = screenRect.Right - 4;
                int sy = screenRect.Top - OuterPadY;
                AdjustForScreen(ref sx, ref sy, item.Submenu);
                item.Submenu.ShowInternal(sx, sy, isSubmenu: true);
            });
        }
    }

    private void OnItemClicked(MenuItem item)
    {
        if (!item.Enabled) return;
        if (item.Kind == ItemKind.Submenu)
        {
            // Click on a submenu item opens it immediately (cancel hover delay).
            if (_activeSubmenu != item.Submenu)
            {
                _activeSubmenu?.CloseChain();
                _activeSubmenu = item.Submenu;
                if (item.Submenu is not null && _form is not null)
                {
                    var rect = _form.LastHoverScreenRect;
                    int sx = rect.Right - 4;
                    int sy = rect.Top - OuterPadY;
                    AdjustForScreen(ref sx, ref sy, item.Submenu);
                    item.Submenu.ShowInternal(sx, sy, isSubmenu: true);
                }
            }
            return;
        }
        // Standard item: close the whole chain, then dispatch.
        var action = item.OnClick;
        CloseChainFromRoot();
        try { action?.Invoke(); } catch { /* swallow */ }
    }

    private void AdjustForScreen(ref int x, ref int y, CustomPopupMenu submenu)
    {
        var size = submenu.MeasureSize();
        var screen = Screen.FromPoint(new Point(x, y)).WorkingArea;
        if (x + size.Width > screen.Right)
        {
            // Flip to the left of the parent.
            if (_form is not null)
                x = _form.Bounds.Left - size.Width + 4;
        }
        if (y + size.Height > screen.Bottom) y = screen.Bottom - size.Height;
        if (x < screen.Left) x = screen.Left;
        if (y < screen.Top) y = screen.Top;
    }

    private Size MeasureSize()
    {
        // Pre-measure for screen-edge adjustments before the form is built.
        using var f = new Font("Segoe UI", 9f);
        using var bmp = new Bitmap(1, 1);
        using var g = Graphics.FromImage(bmp);
        int textWidth = 0;
        int height = OuterPadY * 2;
        bool anySubmenu = _items.Any(i => i.Kind == ItemKind.Submenu);
        foreach (var it in _items)
        {
            if (it.Kind == ItemKind.Separator) { height += SeparatorH; continue; }
            int w = (int)Math.Ceiling(g.MeasureString(it.Text, f).Width);
            if (w > textWidth) textWidth = w;
            height += ItemHeight;
        }
        int width = textWidth + ItemPadX * 2 + (anySubmenu ? ChevronW : 0);
        if (width < MinWidth) width = MinWidth;
        if (width > MaxWidth) width = MaxWidth;
        return new Size(width, height);
    }

    private void CloseChain()
    {
        if (_activeSubmenu is not null)
        {
            _activeSubmenu.CloseChain();
            _activeSubmenu = null;
        }
        _form?.Close();
        _form = null;
    }

    private void CloseChainFromRoot()
    {
        var root = this;
        while (root._parent is not null) root = root._parent;
        root.CloseChain();
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        foreach (var it in _items) it.Submenu?.Dispose();
        _items.Clear();
        _form?.Dispose();
        _form = null;
    }

    // -----------------------------------------------------------------
    // The actual popup window
    // -----------------------------------------------------------------
    private sealed class MenuPopupForm : Form
    {
        private readonly CustomPopupMenu _owner;
        private List<(MenuItem Item, Rectangle Bounds)> _hits = new();
        private MenuItem? _hoveredItem;
        private System.Windows.Forms.Timer? _hoverTimer;

        public Rectangle LastHoverScreenRect { get; private set; }

        public MenuPopupForm(CustomPopupMenu owner)
        {
            _owner = owner;
            FormBorderStyle = FormBorderStyle.None;
            ShowInTaskbar = false;
            StartPosition = FormStartPosition.Manual;
            TopMost = true;
            DoubleBuffered = true;
            BackColor = BgColor;
            Font = new Font("Segoe UI", 9f);
            KeyPreview = true;
        }

        protected override CreateParams CreateParams
        {
            get
            {
                var cp = base.CreateParams;
                // Drop shadow under the popup, same trick Win10/11 menus use.
                cp.ClassStyle |= Win32.CS_DROPSHADOW;
                // WS_EX_TOOLWINDOW so it doesn't show in alt-tab.
                cp.ExStyle |= 0x00000080;
                return cp;
            }
        }

        // Submenus must not steal activation from the root popup, if they do,
        // the root form's OnDeactivate fires, CloseChain runs, and the submenu's
        // own form gets disposed mid-show. Returning true here is the supported
        // WinForms way to opt a top-level form out of activation on Show().
        protected override bool ShowWithoutActivation => _owner._parent is not null;

        public void Build(List<MenuItem> items)
        {
            // Measure
            using var g = CreateGraphics();
            int textWidth = 0;
            int height = OuterPadY * 2;
            bool anySubmenu = items.Any(i => i.Kind == ItemKind.Submenu);
            foreach (var it in items)
            {
                if (it.Kind == ItemKind.Separator) { height += SeparatorH; continue; }
                using var f = new Font(Font, it.Bold ? FontStyle.Bold : FontStyle.Regular);
                int w = (int)Math.Ceiling(g.MeasureString(it.Text, f).Width);
                if (w > textWidth) textWidth = w;
                height += ItemHeight;
            }
            int width = textWidth + ItemPadX * 2 + (anySubmenu ? ChevronW : 0);
            if (width < MinWidth) width = MinWidth;
            if (width > MaxWidth) width = MaxWidth;
            ClientSize = new Size(width, height);

            // Build hit-test rects
            _hits = new List<(MenuItem, Rectangle)>();
            int y = OuterPadY;
            foreach (var it in items)
            {
                int h = it.Kind == ItemKind.Separator ? SeparatorH : ItemHeight;
                _hits.Add((it, new Rectangle(OuterPadX, y, width - OuterPadX * 2, h)));
                y += h;
            }

            // Apply rounded region
            ApplyRoundedRegion();
        }

        public void PositionAt(int x, int y)
        {
            // Clamp into the working area of the screen the cursor is on.
            var screen = Screen.FromPoint(new Point(x, y)).WorkingArea;
            if (x + Width > screen.Right) x = screen.Right - Width;
            if (y + Height > screen.Bottom) y = screen.Bottom - Height;
            if (x < screen.Left) x = screen.Left;
            if (y < screen.Top) y = screen.Top;
            Location = new Point(x, y);
        }

        public void ShowNoActivate()
        {
            if (IsDisposed) return;
            // Force handle creation if Build didn't already (it did, via CreateGraphics),
            // then show via SW_SHOWNA so the OS doesn't activate us. Avoid touching
            // Form.Visible, the .NET show pipeline can re-trigger activation even with
            // ShowWithoutActivation=true, especially for TopMost top-level forms.
            if (!IsHandleCreated) CreateControl();
            Win32.ShowWindow(Handle, Win32.SW_SHOWNA);
        }

        private void ApplyRoundedRegion()
        {
            using var path = RoundedRectPath(new Rectangle(0, 0, Width, Height), CornerRadius);
            Region?.Dispose();
            Region = new Region(path);
        }

        private static GraphicsPath RoundedRectPath(Rectangle r, int radius)
        {
            int d = radius * 2;
            var p = new GraphicsPath();
            p.AddArc(r.X, r.Y, d, d, 180, 90);
            p.AddArc(r.Right - d, r.Y, d, d, 270, 90);
            p.AddArc(r.Right - d, r.Bottom - d, d, d, 0, 90);
            p.AddArc(r.X, r.Bottom - d, d, d, 90, 90);
            p.CloseFigure();
            return p;
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            var g = e.Graphics;
            g.SmoothingMode = SmoothingMode.AntiAlias;
            g.TextRenderingHint = TextRenderingHint.ClearTypeGridFit;

            // Background fill (region already clips to rounded shape)
            using (var bg = new SolidBrush(BgColor)) g.FillRectangle(bg, ClientRectangle);

            // Border
            using (var border = new Pen(BorderColor, 1f))
            using (var path = RoundedRectPath(new Rectangle(0, 0, Width - 1, Height - 1), CornerRadius))
                g.DrawPath(border, path);

            // Items
            foreach (var (it, rect) in _hits)
            {
                if (it.Kind == ItemKind.Separator)
                {
                    int sy = rect.Top + rect.Height / 2;
                    using var sep = new Pen(SeparatorCl, 1f);
                    g.DrawLine(sep, rect.Left + 8, sy, rect.Right - 8, sy);
                    continue;
                }

                bool hovered = it == _hoveredItem && it.Enabled;
                if (hovered)
                {
                    var hr = Rectangle.Inflate(rect, -2, 0);
                    using var hb = new SolidBrush(HoverBg);
                    using var hp = RoundedRectPath(hr, 4);
                    g.FillPath(hb, hp);
                }

                using var f = new Font(Font, it.Bold ? FontStyle.Bold : FontStyle.Regular);
                using var tb = new SolidBrush(it.Enabled ? TextColor : TextDisabled);
                var textRect = new RectangleF(
                    rect.Left + ItemPadX,
                    rect.Top,
                    rect.Width - ItemPadX * 2 - (it.Kind == ItemKind.Submenu ? ChevronW : 0),
                    rect.Height);
                var fmt = new StringFormat
                {
                    LineAlignment = StringAlignment.Center,
                    Alignment = StringAlignment.Near,
                    Trimming = StringTrimming.EllipsisCharacter,
                    FormatFlags = StringFormatFlags.NoWrap
                };
                g.DrawString(it.Text, f, tb, textRect, fmt);

                if (it.Kind == ItemKind.Submenu)
                {
                    // Right-pointing chevron
                    using var cp = new Pen(it.Enabled ? TextColor : TextDisabled, 1.4f)
                    { StartCap = LineCap.Round, EndCap = LineCap.Round };
                    int cx = rect.Right - ItemPadX - 6;
                    int cy = rect.Top + rect.Height / 2;
                    g.DrawLine(cp, cx - 3, cy - 4, cx + 1, cy);
                    g.DrawLine(cp, cx + 1, cy, cx - 3, cy + 4);
                }
            }
        }

        protected override void OnMouseMove(MouseEventArgs e)
        {
            base.OnMouseMove(e);
            MenuItem? hit = null;
            Rectangle hitRect = Rectangle.Empty;
            foreach (var (it, rect) in _hits)
            {
                if (it.Kind == ItemKind.Separator) continue;
                if (rect.Contains(e.Location)) { hit = it; hitRect = rect; break; }
            }
            if (hit != _hoveredItem)
            {
                _hoveredItem = hit;
                Invalidate();
                if (hit is not null)
                {
                    var screenRect = RectangleToScreen(hitRect);
                    LastHoverScreenRect = screenRect;
                    _owner.OnItemHovered(hit, screenRect);
                }
                else
                {
                    CancelPendingSubmenu();
                }
            }
        }

        protected override void OnMouseLeave(EventArgs e)
        {
            base.OnMouseLeave(e);
            // Don't clear _hoveredItem if a submenu is open, keep highlight
            // on the parent item that owns the open submenu.
            if (_owner._activeSubmenu is null)
            {
                _hoveredItem = null;
                Invalidate();
            }
            CancelPendingSubmenu();
        }

        protected override void OnMouseUp(MouseEventArgs e)
        {
            base.OnMouseUp(e);
            if (e.Button != MouseButtons.Left) return;
            foreach (var (it, rect) in _hits)
            {
                if (it.Kind != ItemKind.Separator && rect.Contains(e.Location))
                {
                    _owner.OnItemClicked(it);
                    return;
                }
            }
        }

        protected override void OnKeyDown(KeyEventArgs e)
        {
            base.OnKeyDown(e);
            if (e.KeyCode == Keys.Escape)
            {
                _owner.CloseChain();
                e.Handled = true;
            }
        }

        protected override void OnDeactivate(EventArgs e)
        {
            base.OnDeactivate(e);
            // Only the root popup (no parent) drives chain dismissal on Deactivate.
            if (_owner._parent is not null) return;

            // Defer the close decision to the next message tick. When a submenu
            // is opened, the OS may briefly transfer focus through the chain
            // before settling, if we close immediately on the transient
            // deactivation we'll dismiss the menu the user is trying to use.
            // After the message loop settles, check whether the foreground
            // window is one of our own owned chain forms; if so, ignore.
            BeginInvoke(new Action(() =>
            {
                if (IsDisposed) return;
                var fg = Win32.GetForegroundWindow();
                if (fg == Handle) return;                    // we got focus back
                if (_owner.IsForegroundOwnedByChain(fg)) return; // a submenu has focus
                _owner.CloseChain();
            }));
        }

        protected override void WndProc(ref Message m)
        {
            // Submenus opt out of activation entirely, clicks on them don't
            // make them the active window, so the parent's Deactivate doesn't fire.
            if (m.Msg == Win32.WM_MOUSEACTIVATE && _owner._parent is not null)
            {
                m.Result = (IntPtr)Win32.MA_NOACTIVATE;
                return;
            }
            base.WndProc(ref m);
        }

        public void ScheduleSubmenuOpen(MenuItem item, int delayMs, Action open)
        {
            CancelPendingSubmenu();
            _hoverTimer = new System.Windows.Forms.Timer { Interval = delayMs };
            _hoverTimer.Tick += (_, _) =>
            {
                CancelPendingSubmenu();
                if (_hoveredItem == item) open();
            };
            _hoverTimer.Start();
        }

        private void CancelPendingSubmenu()
        {
            _hoverTimer?.Stop();
            _hoverTimer?.Dispose();
            _hoverTimer = null;
        }

        protected override void OnFormClosing(FormClosingEventArgs e)
        {
            CancelPendingSubmenu();
            base.OnFormClosing(e);
        }
    }
}
