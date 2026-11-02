using System.ComponentModel;
using System.Drawing.Drawing2D;

namespace FrameGlass;

/// <summary>
/// A soft, flat look with no third-party control library: one palette, rounded corners, and flat
/// buttons that respond on hover and press. Nothing here animates - this window is opened and
/// used hundreds of times a day, where motion reads as lag.
/// </summary>
static class Theme
{
    // Dark throughout, and uniformly so: a window that is half dark and half light reads as broken
    // rather than designed. Cards sit one step off the canvas so panels still read as panels, and
    // every surface here is dark - there is no second light theme to fall out of sync.
    public static readonly Color Canvas = Color.FromArgb(0x20, 0x20, 0x20);   // window background (Windows 11 dark)
    public static readonly Color Chrome = Color.FromArgb(0x20, 0x20, 0x20);  // tab strip, same as canvas
    public static readonly Color Card = Color.FromArgb(0x2B, 0x2B, 0x2B);    // panels, tables
    // Input fields (text boxes, combos, spinners) sit one step lighter than a card, so a field reads as a
    // place to type rather than as part of the panel behind it. Used for the field and its native edit child.
    public static readonly Color Input = Color.FromArgb(0x3A, 0x3A, 0x3A);
    public static readonly Color Line = Color.FromArgb(0x3D, 0x3D, 0x3D);    // borders, gridlines
    public static readonly Color Ink = Color.FromArgb(0xFF, 0xFF, 0xFF);     // primary text
    public static readonly Color Muted = Color.FromArgb(0xAB, 0xAB, 0xAB);   // secondary text
    /// <summary>The Windows accent colour, read once at startup, so the app matches the rest of the OS.</summary>
    public static readonly Color Accent = SystemAccent();
    public static readonly Color AccentSoft = Mix(Accent, Canvas, 0.72f);
    // Lighter than Input (#3A3A3A), so a hovered combo list row stays visible against the field.
    public static readonly Color Hover = Color.FromArgb(0x45, 0x45, 0x45);

    public const int Radius = 8;

    // Segoe UI Variable is the Fluent type: one family covers text, display and caption, and it
    // interpolates weights. It is a system font from Windows 11, so fall back to plain Segoe UI
    // rather than silently rendering in a default face.
    public static readonly string UiFont = Installed("Segoe UI Variable Text") ? "Segoe UI Variable Text"
                              : Installed("Segoe UI") ? "Segoe UI"
                              : SystemFonts.DefaultFont.Name;

    /// <summary>Body text size, in points. The only size this window uses.</summary>
    public const float TypeBody = 9.5f;
    /// <summary>Small secondary text: hints, captions, column labels.</summary>
    public const float TypeCaption = 9f;
    /// <summary>Section headings inside a tab or card.</summary>
    public const float TypeSubtitle = 15f;
    /// <summary>Window or page title. The largest size in the type scale.</summary>
    public const float TypeTitle = 21f;

    static bool Installed(string family) =>
        new FontFamily(family).Name == family;

    /// <summary>
    /// The Windows accent colour, from HKCU\...\Themes\Personalize. The value is a DWORD in
    /// AABBGGRR order - reversed relative to Color's ARGB - so it has to be rebuilt byte by byte.
    /// Falls back to the blue this app shipped with if the key is missing or the read is denied,
    /// because a settings window must not fail to draw over an accent colour.
    /// </summary>
    static Color SystemAccent()
    {
        var fallback = Color.FromArgb(0x5C, 0xA8, 0xFF);
        try
        {
            using var key = Microsoft.Win32.Registry.CurrentUser.OpenSubKey(
                @"Software\Microsoft\Windows\CurrentVersion\Themes\Personalize");
            if (key?.GetValue("AccentColor") is not int abgr) return fallback;
            // Byte order in the registry DWORD is 0xAABBGGRR: red is the low byte, blue the third.
            // Color.FromArgb(int) expects 0xAARRGGBB, so pass the channels explicitly instead.
            int r = abgr & 0xFF, g = (abgr >> 8) & 0xFF, b = (abgr >> 16) & 0xFF;
            return Color.FromArgb(255, r, g, b);
        }
        catch { return fallback; }
    }

    // Spacing, on a 4/8 rhythm. Named rather than typed inline: the numbers were scattered across
    // every tab, which is why they ended up at 15 and 27 as well as 8.
    public const int SpaceXs = 4, SpaceSm = 8, SpaceMd = 12, SpaceLg = 16, SpaceXl = 24;

    /// <summary>
    /// Mica and rounded corners on Windows 11, and a dark title bar on Windows 10 2004+, all
    /// best-effort: the DWM call takes a plain int and an older build simply ignores it, so the
    /// window must look right with every one of these failing. The client area carries its own
    /// solid fill for that reason.
    /// </summary>
    public static void ApplyWindowChrome(Form f)
    {
        if (f.IsDisposed || !f.IsHandleCreated) return;
        // 20 = DWMWA_USE_IMMERSIVE_DARK_MODE, 1 = on. Needs Windows 10 2004 (build 19041) or newer.
        // This is the one that matters most visually: without it a dark app wears the OS light
        // caption and minimise/maximise buttons.
        DwmSetWindowAttribute(f.Handle, 20, 1, 4);
        // 33 = DWMWA_WINDOW_CORNER_PREFERENCE, 2 = DWMWCP_ROUND. Needs Windows 11.
        DwmSetWindowAttribute(f.Handle, 33, 2, 4);
        // 38 = DWMWA_SYSTEMBACKDROP_TYPE, 2 = DWMSBT_MAINWINDOW (Mica). Needs Windows 11 build 22621
        // or newer. 1 would be Acrylic, which blurs far more aggressively and turns text fuzzy.
        DwmSetWindowAttribute(f.Handle, 38, 2, 4);
        f.BackColor = Canvas;
    }

    [System.Runtime.InteropServices.DllImport("dwmapi.dll")]
    static extern int DwmSetWindowAttribute(IntPtr hwnd, int attr, int value, int size);

    /// <summary>
    /// A ComboBox that keeps the dark fill all the way to the edge. FlatStyle.Flat is ignored for
    /// DropDownList in several states, so the stock control kept drawing a pale 3D frame around
    /// the field - the most visible piece of default Windows styling left in the window.
    /// </summary>
    public sealed class ThemedCombo : ComboBox
    {
        public ThemedCombo()
        {
            // Deliberately NOT UserPaint. A ComboBox is two native controls - an edit field and a
            // list - and setting UserPaint on it makes the framework, the field and the list
            // disagree about who paints what, which leaves stale pixels in the field.
            //
            // FlatStyle.Flat plus the dark colours is the supported way to restyle a ComboBox, and
            // the process is already in dark colour mode, so the native arrow comes out dark too.
            FlatStyle = FlatStyle.Flat;
            DrawMode = DrawMode.OwnerDrawFixed;
            ItemHeight = S(26);   // the control derives its height from this; 18 left it 10px shorter
                                  // than every TextBox and spinner next to it
        }

        float _dpiScale = 1f;

        /// <summary>
        /// Scale factor for this control's pixel sizes, set by the owning form from its DPI. Changing it
        /// resizes the item height, which is why it is a property and not a constant.
        /// </summary>
        [DefaultValue(1f)]
        public float DpiScale
        {
            get => _dpiScale;
            set { _dpiScale = value; ItemHeight = S(26); Invalidate(); }
        }

        /// <summary>A pixel size scaled by <see cref="DpiScale"/>.</summary>
        int S(int v) => (int)Math.Round(v * DpiScale);

        protected override void OnDrawItem(DrawItemEventArgs e)
        {
            // e.Index == -1 is the closed field. There is no edit box behind it, so the text has to
            // be drawn here or the combo reads as an empty box.
            //
            // Do not call OnPaint from here. It re-enters the paint path with a Graphics borrowed from
            // DrawItem, which this control double-buffers, so the buffer and the surface disagree and the
            // field keeps a block of stale pixels (the black ghost rectangle beside a dropdown). Drawing
            // into e.Graphics directly is what DrawItem is for.
            if (e.Index < 0)
            {
                using var bg = new SolidBrush(BackColor);
                e.Graphics.FillRectangle(bg, e.Bounds);
                using var text = new SolidBrush(ForeColor);
                e.Graphics.DrawString(Text, Font, text, e.Bounds.X + S(4),
                    e.Bounds.Y + (e.Bounds.Height - Font.Height) / 2);
                // The flat frame draws no focus cue, so the closed field gets an Accent edge while focused.
                if (Focused)
                    using (var ring = new Pen(Accent, Math.Max(1f, S(2))))
                        e.Graphics.DrawRectangle(ring, e.Bounds.X, e.Bounds.Y,
                            Math.Max(1, e.Bounds.Width - 1), Math.Max(1, e.Bounds.Height - 1));
                return;
            }
            bool on = (e.State & DrawItemState.Selected) != 0;
            using (var b = new SolidBrush(on ? Hover : BackColor)) e.Graphics.FillRectangle(b, e.Bounds);
            using (var t = new SolidBrush(ForeColor))
                e.Graphics.DrawString(GetItemText(Items[e.Index]), Font, t, e.Bounds.X + S(4),
                    e.Bounds.Y + (e.Bounds.Height - Font.Height) / 2);
        }

        protected override void OnGotFocus(EventArgs e)
        {
            base.OnGotFocus(e);
            // The flat frame has no focus cue of its own, and this window is driven from the
            // keyboard as much as the mouse, so the field is redrawn in the accent when focused.
            Invalidate();
        }

        protected override void OnLostFocus(EventArgs e) { base.OnLostFocus(e); ClearEditSelection(); Invalidate(); }

        // Measured: a resize (DPI change) makes the native edit re-select its text. EM_GETSEL read 0..8 at 150%
        // and 8..8 at 100%. Clear again after the resize when the combo is not focused.
        protected override void OnSizeChanged(EventArgs e) { base.OnSizeChanged(e); CentreAfterResize(); if (!Focused) ClearEditSelection(); }

        /// <summary>
        /// An editable combo draws its text in a native edit box, and that box keeps the stock blue selection
        /// highlight while the combo is not focused. Clearing the selection removes the highlight. Callers are
        /// focus loss (OnLostFocus) and a resize while unfocused (OnSizeChanged, which is how a DPI change lands).
        /// A DropDownList combo has no edit box, so it is left alone.
        /// </summary>
        public void ClearEditSelection()
        {
            if (DropDownStyle != ComboBoxStyle.DropDown || !IsHandleCreated) return;
            CollapseEditSelection();
            // The native edit can re-select its text after this call returns (Text assignment, handle
            // re-creation, page show). Repeat the clear once the message queue is idle so the last
            // word is ours. Skipped when the combo has focus so the user's selection is kept.
            BeginInvoke(new Action(() =>
            {
                if (!Focused && IsHandleCreated && !IsDisposed) CollapseEditSelection();
            }));
        }

        // SelectionLength reads 0 even while the edit paints a full selection, so the managed property
        // is not a reliable handle on it. Send EM_SETSEL straight to the native edit child (the first child
        // window of a DropDown combo) and park the caret at the end with nothing selected.
        void CollapseEditSelection()
        {
            IntPtr edit = GetWindow(Handle, GW_CHILD);
            if (edit == IntPtr.Zero) return;
            int end = Text.Length;
            SendMessage(edit, EM_SETSEL, (IntPtr)end, (IntPtr)end);
        }

        const uint GW_CHILD = 5;
        const int EM_SETSEL = 0x00B1;

        [System.Runtime.InteropServices.DllImport("user32.dll")]
        static extern IntPtr GetWindow(IntPtr hWnd, uint uCmd);

        [System.Runtime.InteropServices.DllImport("user32.dll", CharSet = System.Runtime.InteropServices.CharSet.Unicode)]
        static extern IntPtr SendMessage(IntPtr hWnd, int msg, IntPtr wParam, IntPtr lParam);

        protected override void OnHandleCreated(EventArgs e) { base.OnHandleCreated(e); EditBox.Centre(this, fill: false); CentreEditText(); }

        // A DPI change grows the field, and the native edit keeps its top edge, so re-centre it after the resize.
        void CentreAfterResize()
        {
            EditBox.Centre(this, fill: false);
            if (IsHandleCreated) BeginInvoke(new Action(() => { if (!IsDisposed) EditBox.Centre(this, fill: false); }));
        }

        // The native edit is re-laid out by the combo on every resize and font change, so the text height is
        // reapplied on each of them rather than once at creation.
        protected override void OnResize(EventArgs e) { base.OnResize(e); CentreEditText(); }

        protected override void OnFontChanged(EventArgs e) { base.OnFontChanged(e); CentreEditText(); }

        /// <summary>
        /// An editable combo's native edit is as tall as the frame minus a 3px inset top and bottom, and a single-line
        /// edit draws its text at its own top, so the text sat above centre. The edit is shrunk to the font's line
        /// height and centred in the frame; its X and width are left as the framework set them. A DropDownList has
        /// no edit child and is left alone.
        /// </summary>
        void CentreEditText()
        {
            if (DropDownStyle != ComboBoxStyle.DropDown || !IsHandleCreated) return;
            // Measured with GDI, the same text path the edit uses, so the height matches what the edit paints.
            int lineHeight = TextRenderer.MeasureText("Ag", Font, new Size(int.MaxValue, int.MaxValue), TextFormatFlags.NoPadding).Height;
            EditBox.CentreWithHeight(this, lineHeight);
            // Measured: SetWindowPos on the edit returns true and holds until this handler returns, then the native
            // combo re-lays its edit back to the frame's inner height (26 px at a 32 px frame). Reapply once the
            // message queue is idle, as CentreAfterResize does, so the last word is ours.
            if (IsHandleCreated)
                BeginInvoke(new Action(() =>
                {
                    if (!IsDisposed && IsHandleCreated && DropDownStyle == ComboBoxStyle.DropDown)
                        EditBox.CentreWithHeight(this, lineHeight);
                }));
        }
    }

    /// <summary>
    /// Vertical placement of the native edit inside an editable ComboBox or NumericUpDown. At high DPI Windows
    /// leaves that edit at the top of a taller frame, so the text sat above the middle and the frame's bottom
    /// edge looked stepped. Only the edit's Y and height change; its X and width are the framework's.
    /// </summary>
    static class EditBox
    {
        [System.Runtime.InteropServices.StructLayout(System.Runtime.InteropServices.LayoutKind.Sequential)]
        struct RECT { public int Left, Top, Right, Bottom; }

        [System.Runtime.InteropServices.StructLayout(System.Runtime.InteropServices.LayoutKind.Sequential)]
        struct POINT { public int X, Y; }

        const uint GW_CHILD = 5, GW_HWNDNEXT = 2;
        const uint SWP_NOZORDER = 0x0004, SWP_NOACTIVATE = 0x0010;

        [System.Runtime.InteropServices.DllImport("user32.dll")]
        static extern IntPtr GetWindow(IntPtr hWnd, uint uCmd);

        [System.Runtime.InteropServices.DllImport("user32.dll", CharSet = System.Runtime.InteropServices.CharSet.Unicode)]
        static extern int GetClassName(IntPtr hWnd, System.Text.StringBuilder name, int max);

        [System.Runtime.InteropServices.DllImport("user32.dll")]
        static extern bool GetWindowRect(IntPtr hWnd, out RECT rect);

        [System.Runtime.InteropServices.DllImport("user32.dll")]
        static extern bool ScreenToClient(IntPtr hWnd, ref POINT point);

        [System.Runtime.InteropServices.DllImport("user32.dll")]
        static extern bool SetWindowPos(IntPtr hWnd, IntPtr after, int x, int y, int cx, int cy, uint flags);

        /// <summary>
        /// Centres the edit child of <paramref name="host"/> vertically. With <paramref name="fill"/> the edit spans
        /// the frame (a 2px inset top and bottom) and the single-line text centres inside it; otherwise the edit
        /// keeps its own height and is centred in the frame.
        /// </summary>
        public static void Centre(Control host, bool fill)
        {
            if (!host.IsHandleCreated || host.IsDisposed) return;
            IntPtr edit = FindEdit(host.Handle);
            if (edit == IntPtr.Zero || !GetWindowRect(edit, out var r)) return;
            var origin = new POINT { X = r.Left, Y = r.Top };
            ScreenToClient(host.Handle, ref origin);
            int w = r.Right - r.Left;
            int h = r.Bottom - r.Top;
            int frame = host.ClientSize.Height;
            int y;
            if (fill) { y = 2; h = Math.Max(1, frame - 4); }
            else y = Math.Max(0, (frame - h) / 2);
            if (origin.Y == y && (r.Bottom - r.Top) == h) return;   // already there; avoid a layout loop
            SetWindowPos(edit, IntPtr.Zero, origin.X, y, w, h, SWP_NOZORDER | SWP_NOACTIVATE);
        }

        /// <summary>
        /// Sets the edit child of <paramref name="host"/> to <paramref name="height"/> and centres it vertically in
        /// the frame. Only Y and height change; X and width are kept. Used where the edit should be the text's line
        /// height rather than the frame's inner height, so a single line of text lands in the middle of the frame.
        /// </summary>
        public static void CentreWithHeight(Control host, int height)
        {
            if (!host.IsHandleCreated || host.IsDisposed) return;
            IntPtr edit = FindEdit(host.Handle);
            if (edit == IntPtr.Zero || !GetWindowRect(edit, out var r)) return;
            var origin = new POINT { X = r.Left, Y = r.Top };
            ScreenToClient(host.Handle, ref origin);
            int w = r.Right - r.Left;
            int frame = host.ClientSize.Height;
            int h = Math.Clamp(height, 1, Math.Max(1, frame));
            int y = Math.Max(0, (frame - h) / 2);
            if (origin.Y == y && (r.Bottom - r.Top) == h) return;   // already there; avoid a layout loop
            SetWindowPos(edit, IntPtr.Zero, origin.X, y, w, h, SWP_NOZORDER | SWP_NOACTIVATE);
        }

        /// <summary>The child window whose class is "Edit"; a NumericUpDown also has an up-down child.</summary>
        static IntPtr FindEdit(IntPtr parent)
        {
            var name = new System.Text.StringBuilder(32);
            for (IntPtr c = GetWindow(parent, GW_CHILD); c != IntPtr.Zero; c = GetWindow(c, GW_HWNDNEXT))
            {
                name.Clear();
                GetClassName(c, name, name.Capacity);
                if (string.Equals(name.ToString(), "Edit", StringComparison.OrdinalIgnoreCase)) return c;
            }
            return IntPtr.Zero;
        }
    }

    /// <summary>
    /// A NumericUpDown whose spinner buttons are drawn rather than inherited. The stock UpDownButtons
    /// ignore the parent's BackColor, so every spinner kept a pale block on its right edge.
    /// </summary>
    public sealed class ThemedSpinner : NumericUpDown
    {
        public ThemedSpinner()
        {
            SetStyle(ControlStyles.UserPaint | ControlStyles.AllPaintingInWmPaint
                   | ControlStyles.OptimizedDoubleBuffer | ControlStyles.ResizeRedraw, true);
        }

        float _dpiScale = 1f;

        /// <summary>Scale factor for this control's pixel sizes, set by the owning form from its DPI.</summary>
        [DefaultValue(1f)]
        public float DpiScale
        {
            get => _dpiScale;
            set { _dpiScale = value; Invalidate(); }
        }

        /// <summary>A pixel size scaled by <see cref="DpiScale"/>.</summary>
        int S(int v) => (int)Math.Round(v * DpiScale);

        // Same centring as ThemedCombo: the edit keeps its own height and sits in the middle of the frame.
        protected override void OnHandleCreated(EventArgs e) { base.OnHandleCreated(e); EditBox.Centre(this, fill: false); SyncEditColour(); }

        // The field's fill is BackColor. The native edit is a child TextBox, which the framework does not always
        // repaint with the parent's colour, so it is set here explicitly whenever the field's colour changes.
        protected override void OnBackColorChanged(EventArgs e) { base.OnBackColorChanged(e); SyncEditColour(); }

        void SyncEditColour()
        {
            foreach (Control c in Controls)
                if (c is TextBox) c.BackColor = BackColor;
        }

        // The native edit is placed by the framework on each resize, so it is centred again after the resize.
        protected override void OnSizeChanged(EventArgs e)
        {
            base.OnSizeChanged(e);
            EditBox.Centre(this, fill: false);
            if (IsHandleCreated) BeginInvoke(new Action(() => { if (!IsDisposed) EditBox.Centre(this, fill: false); }));
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            base.OnPaint(e);
            // The frame paints its bottom band after the base paint, so the edge under the centred edit reads as
            // one straight line rather than a step where the native edit stopped.
            using (var band = new SolidBrush(BackColor)) e.Graphics.FillRectangle(band, 0, Height - 2, Width, 2);
            int spinW = S(18);
            var spin = new Rectangle(Width - spinW - 1, 1, spinW, Height - 2);
            // The spin strip is part of the field, so it takes the field's BackColor. A strip in the surface colour
            // would show as a notch cut out of the lighter Input field.
            using (var b = new SolidBrush(Enabled ? BackColor : BackColor.Faded(0.6f)))
                e.Graphics.FillRectangle(b, spin);

            // Split in two: up on top, down below, with a hairline between them.
            using (var mid = new Pen(Line, 1f))
                e.Graphics.DrawLine(mid, spin.Left, spin.Top + spin.Height / 2f,
                                        spin.Right, spin.Top + spin.Height / 2f);
            using (var box = new Pen(Line, 1f)) e.Graphics.DrawRectangle(box, spin);

            Arrow(e.Graphics, spin.Left, spin.Top, spin.Width, spin.Height / 2, up: true);
            Arrow(e.Graphics, spin.Left, spin.Top + spin.Height / 2, spin.Width, spin.Height / 2, up: false);
        }

        void Arrow(Graphics g, int x, int y, int w, int h, bool up)
        {
            if (!Enabled) return;
            int cx = x + w / 2, cy = y + h / 2;

            // Fluent chevron when the icon font loaded; otherwise the hand-drawn polyline below.
            // The glyph em square equals the smaller side of this half-cell, centred on the same point
            // the polyline uses. Fluent chevron ink fills only about half the em box, so this size matches
            // the ~8 px polyline. The cell is the spin strip width (scaled by DpiScale) and half the
            // control's Height, which WinForms also DPI-scales, so min() picks the side that still fits.
            // Colour matches the polyline: ForeColor, with no separate disabled
            // or hover variant (a disabled spinner returns early above).
            if (Icons.Available)
            {
                float s = Math.Min(w, h);
                Icons.Draw(g, up ? Icons.ChevronUp : Icons.ChevronDown,
                    new RectangleF(cx - s / 2f, cy - s / 2f, s, s), ForeColor);
                return;
            }

            var pen = new Pen(ForeColor, Math.Max(1f, 1.6f * DpiScale)) { StartCap = LineCap.Round, EndCap = LineCap.Round };
            int ax = S(4), ay = S(3), ad = S(1);
            using (pen)
                if (up) g.DrawLines(pen, [new Point(cx - ax, cy + ad), new Point(cx, cy - ay), new Point(cx + ax, cy + ad)]);
                else g.DrawLines(pen, [new Point(cx - ax, cy - ad), new Point(cx, cy + ay), new Point(cx + ax, cy - ad)]);
        }

        protected override void OnMouseDown(MouseEventArgs me)
        {
            // Route clicks in the drawn strip to the real spinner, so the custom paint did not
            // cost the control its behaviour.
            if (me.X >= Width - S(18))
            {
                bool up = me.Y < Height / 2f;
                if (up && Value < Maximum) Value++;
                else if (!up && Value > Minimum) Value--;
                return;
            }
            base.OnMouseDown(me);
        }
    }

    /// <summary>Text that stays readable on the given background.</summary>
    public static Color On(Color c) => c.GetBrightness() > 0.6 ? Ink : Color.White;

    /// <summary>A rounded rectangle path, used for window corners and card backgrounds.</summary>
    public static GraphicsPath Round(RectangleF r, int radius)
    {
        float d = radius * 2f;
        if (r.Width <= d || r.Height <= d) { var flat = new GraphicsPath(); flat.AddRectangle(r); return flat; }
        var p = new GraphicsPath();
        p.AddArc(r.X, r.Y, d, d, 180, 90);
        p.AddArc(r.Right - d, r.Y, d, d, 270, 90);
        p.AddArc(r.Right - d, r.Bottom - d, d, d, 0, 90);
        p.AddArc(r.X, r.Bottom - d, d, d, 90, 90);
        p.CloseFigure();
        return p;
    }

    public static void Fill(Graphics g, RectangleF r, Color fill, int radius = Radius, Color? border = null)
    {
        using var path = Round(r, radius);
        using var b = new SolidBrush(fill);
        g.FillPath(b, path);
        if (border is { } line)
        {
            using var pen = new Pen(line);
            g.DrawPath(pen, path);
        }
    }

    /// <summary>Rounds a control's corners by clipping it to the path.</summary>
    public static void Round2(Control c, int radius = Radius)
    {
        if (c.Width <= radius * 2 || c.Height <= radius * 2) return;
        using var path = Round(new RectangleF(0, 0, c.Width, c.Height), radius);
        c.Region?.Dispose();
        c.Region = new Region(path);
    }

    public static void Flat(Button b, bool filled = false)
    {
        b.FlatStyle = FlatStyle.Flat;
        b.FlatAppearance.BorderSize = filled ? 0 : 1;
        b.FlatAppearance.BorderColor = Line;
        b.BackColor = filled ? Accent : Card;
        b.ForeColor = filled ? On(Accent) : Ink;
        b.FlatAppearance.MouseOverBackColor = filled ? AccentHover : Hover;
        b.FlatAppearance.MouseDownBackColor = filled ? AccentDown : AccentSoft;
        // FlatAppearance has no hover/pressed ForeColor, so the text keeps the resting colour.
        // That is fine here because every button pairs Ink or On(Accent) with a fill of very
        // different lightness, and both states stay above 4.5:1.
        b.UseVisualStyleBackColor = false;
        b.Cursor = Cursors.Hand;
        // Keyboard focus cue: the flat style draws no focus rectangle, so the edge changes colour instead.
        b.GotFocus += (_, _) => FocusEdge(b, filled, true);
        b.LostFocus += (_, _) => FocusEdge(b, filled, false);
        // A screen reader otherwise announces the glyph ("up arrow") with no idea what it moves.
        if (string.IsNullOrEmpty(b.AccessibleName) && b.Text.Length > 0) b.AccessibleName = b.Text;
    }

    /// <summary>
    /// The focus edge of a flat button: 2px, Ink on a filled button (an Accent edge would vanish into the
    /// Accent fill) and Accent on a plain one. Losing focus restores the resting edge.
    /// </summary>
    static void FocusEdge(Button b, bool filled, bool focused)
    {
        b.FlatAppearance.BorderSize = focused ? 2 : (filled ? 0 : 1);
        b.FlatAppearance.BorderColor = focused ? (filled ? Ink : Accent) : Line;
        b.Invalidate();
    }

    static readonly Color AccentHover = Mix(Accent, Color.White, 0.14f);
    static readonly Color AccentDown = Mix(Accent, Color.Black, 0.18f);

    /// <summary>Blends <paramref name="b"/> over <paramref name="a"/> by <paramref name="t"/> (0 = a, 1 = b), opaque.</summary>
    public static Color Mix(Color a, Color b, float t) => Color.FromArgb(
        (int)(a.R + (b.R - a.R) * t),
        (int)(a.G + (b.G - a.G) * t),
        (int)(a.B + (b.B - a.B) * t));

    /// <summary>The same colour at reduced opacity - for a disabled control's fill.</summary>
    static Color Faded(this Color c, float a) => Color.FromArgb((int)(255 * a), c.R, c.G, c.B);

    /// <summary>
    /// The colour of the surface a control sits on: the nearest opaque BackColor above it. Custom-painted
    /// controls fill their gaps and channels with this rather than with the Canvas constant, so a control
    /// inside a card is not framed by a Canvas-coloured patch. Falls back to Canvas if no ancestor is opaque.
    /// </summary>
    public static Color SurfaceBehind(Control c)
    {
        for (Control? p = c.Parent; p is not null; p = p.Parent)
            if (p.BackColor.A == 255) return p.BackColor;
        return Canvas;
    }

    /// <summary>
    /// A capsule: a rectangle whose short ends are full semicircles - the Fluent shape of a switch track
    /// and thumb-track. <see cref="Round"/> cannot draw one, because it falls back to a square when the
    /// radius would equal half the height. The caller owns the returned path and must dispose it.
    /// </summary>
    public static GraphicsPath Capsule(RectangleF r)
    {
        var p = new GraphicsPath();
        float d = Math.Min(r.Width, r.Height);
        if (d <= 0) { p.AddRectangle(r); return p; }
        p.AddArc(r.X, r.Y, d, d, 90, 180);
        p.AddArc(r.Right - d, r.Y, d, d, 270, 180);
        p.CloseFigure();
        return p;
    }

    /// <summary>
    /// A focus ring for custom-painted surfaces. WinForms draws no focus affordance on a flat
    /// control, so without this a keyboard user cannot tell where they are. Fluent's rule: a 2px
    /// accent outline with a dark gap inside it, drawn just outside the control's bounds.
    ///
    /// Drawn on a Panel rather than a bare Control: a Control with UserPaint rejects a transparent
    /// BackColor, and a Panel supports transparency, so the ring does not paint a box over the
    /// button it surrounds.
    /// </summary>
    public sealed class FocusRing : Panel
    {
        public FocusRing()
        {
            SetStyle(ControlStyles.UserPaint | ControlStyles.AllPaintingInWmPaint
                   | ControlStyles.OptimizedDoubleBuffer | ControlStyles.ResizeRedraw, true);
            SetStyle(ControlStyles.Selectable, false);   // decorates; never in the tab order
            BackColor = Color.Transparent;
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            var r = new Rectangle(1, 1, Width - 3, Height - 3);
            if (r.Width <= 2 || r.Height <= 2) return;
            e.Graphics.SmoothingMode = SmoothingMode.AntiAlias;
            // The gap is the colour of the surface the ring sits on, so it does not show as a patch.
            using (var gap = new Pen(SurfaceBehind(this), 3f)) e.Graphics.DrawRectangle(gap, r);
            using (var ring = new Pen(Accent, 2f)) e.Graphics.DrawRectangle(ring, r);
        }
    }

    /// <summary>
    /// The settings window's left navigation, in the style of Windows 11 Settings: one row per page,
    /// each with a glyph and a label, and a short accent pill on the selected row. It is the window's
    /// only page switcher.
    ///
    /// Custom-painted, not a ListBox: a ListBox cannot draw the rounded hover fill or the accent pill,
    /// and it brings the OS selection colour with it. Every colour comes from the Theme tokens, and
    /// every size is multiplied by <see cref="DpiScale"/>, the form's k, because WinForms does not
    /// scale custom-painted metrics for us. The caller sets the width (200 * k) and DpiScale, and
    /// updates both on DpiChanged.
    ///
    /// Keyboard: one tab stop. Up, Down, Home and End move the selection. The focus ring is drawn on
    /// the selected row only while focus came from the keyboard, so a mouse click leaves no ring.
    ///
    /// Accessibility: the control is a List, and each row is a ListItem with its label as the name
    /// and Selected on the current row. Invoking a row's default action selects it.
    /// </summary>
    public sealed class NavSidebar : Control
    {
        readonly List<(string Text, char Glyph)> _items = new();
        int _selected = -1;
        int _hover = -1;
        bool _pointerFocus;   // true only while a mouse click is what gives this control focus
        bool _showRing;       // focus ring on the selected row: keyboard focus only

        float _k = 1f;

        /// <summary>Raised when the selection changes, by click, keyboard or code. The first AddItem does not raise it.</summary>
        public event EventHandler? SelectedIndexChanged;

        public NavSidebar()
        {
            SetStyle(ControlStyles.UserPaint | ControlStyles.AllPaintingInWmPaint
                   | ControlStyles.OptimizedDoubleBuffer | ControlStyles.ResizeRedraw
                   | ControlStyles.Selectable, true);
            BackColor = Chrome;
            TabStop = true;
            Cursor = Cursors.Hand;
            AccessibleRole = AccessibleRole.List;
            AccessibleName = "Settings pages";
        }

        /// <summary>
        /// The DPI factor (the form's k: 1 at 96 DPI, 1.5 at 144). Every row, glyph and pill size below
        /// is multiplied by it. Setting it repaints; the caller is responsible for the control's Width.
        /// </summary>
        [DefaultValue(1f)]
        public float DpiScale
        {
            get => _k;
            set
            {
                if (value <= 0 || Math.Abs(value - _k) < 0.0001f) return;
                _k = value;
                Invalidate();
            }
        }

        /// <summary>Adds a row at the bottom. The first row added becomes the selection, silently.</summary>
        public void AddItem(string text, char glyph)
        {
            bool first = _items.Count == 0;
            _items.Add((text, glyph));
            if (first) _selected = 0;
            Invalidate();
        }

        /// <summary>
        /// The selected row, or -1 when there are no rows. Assigning a value outside the row range is
        /// clamped into it, so Up at the top stays on the first row rather than going to -1.
        /// </summary>
        [DefaultValue(-1)]
        public int SelectedIndex
        {
            get => _selected;
            set
            {
                int v = _items.Count == 0 ? -1 : Math.Clamp(value, 0, _items.Count - 1);
                if (v == _selected) return;
                _selected = v;
                Invalidate();
                SelectedIndexChanged?.Invoke(this, EventArgs.Empty);
            }
        }

        // Metrics, all multiplied by k. Named so the paint, hit-test and accessibility code agree.
        float Pad => Theme.SpaceXs * _k;          // side and top margin of the row column
        float RowHeight => 40f * _k;                  // one row
        float Inset => Theme.SpaceMd * _k;            // left padding inside a row, before the glyph
        float GlyphSize => 16f * _k;                  // glyph square
        float Gap => Theme.SpaceMd * _k;              // glyph to label
        int Corner => (int)Math.Round(Theme.Radius * _k);   // row corner radius, the Theme.Radius scale
        int PillCorner => Math.Max(1, (int)Math.Round(1.5f * _k));

        /// <summary>A row's rectangle in client coordinates.</summary>
        RectangleF ItemRect(int i) => new(Pad, Pad + i * RowHeight, Width - 2 * Pad, RowHeight);

        /// <summary>The row under a client point, or -1 for the gaps and the margins.</summary>
        int ItemAt(Point p)
        {
            if (p.X < Pad || p.X > Width - Pad) return -1;
            int i = (int)Math.Floor((p.Y - Pad) / RowHeight);
            return i >= 0 && i < _items.Count ? i : -1;
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            var g = e.Graphics;
            g.SmoothingMode = SmoothingMode.AntiAlias;
            using (var bg = new SolidBrush(Chrome))
                g.FillRectangle(bg, ClientRectangle);

            // One font and one format for every row, disposed at the end of the paint. Nothing is cached
            // on the control, so there is no GDI object to free in Dispose.
            // Scaled by _k in points: this text is drawn through a Graphics at 96 DPI, so a bare 9.5pt stayed at
            // the 100% size on a 175% monitor while every WinForms label grew. The scale is applied here instead.
            using var font = new Font(Theme.UiFont, Theme.TypeBody * _k);
            using var text = new SolidBrush(Ink);
            using var fmt = new StringFormat
            {
                LineAlignment = StringAlignment.Center,
                FormatFlags = StringFormatFlags.NoWrap,
                Trimming = StringTrimming.EllipsisCharacter,
            };

            for (int i = 0; i < _items.Count; i++)
            {
                var r = ItemRect(i);
                bool selected = i == _selected;
                // Selected and hovered rows share one fill, Hover. The pill is what marks the selection.
                if (selected || i == _hover) Fill(g, r, Hover, Corner);

                if (selected)
                {
                    var pill = new RectangleF(r.Left, r.Top + (r.Height - 16f * _k) / 2f, 3f * _k, 16f * _k);
                    Fill(g, pill, Accent, PillCorner);
                }

                // Without the icon font the label starts at the same inset, so the rows still line up.
                float x = r.Left + Inset;
                if (Icons.Available)
                {
                    Icons.Draw(g, _items[i].Glyph, new RectangleF(x, r.Top + (r.Height - GlyphSize) / 2f, GlyphSize, GlyphSize), Ink);
                    x += GlyphSize + Gap;
                }

                float textWidth = r.Right - Inset - x;
                if (textWidth > 0)
                    g.DrawString(_items[i].Text, font, text, new RectangleF(x, r.Top, textWidth, r.Height), fmt);
            }

            // Focus ring: the same treatment as FocusRing (a dark gap, then a 2px accent line), drawn
            // just inside the selected row so the clipped edge of the control does not cut it.
            if (_showRing && Focused && _selected >= 0)
            {
                var ringRect = RectangleF.Inflate(ItemRect(_selected), -_k, -_k);
                using var path = Round(ringRect, Corner);
                using (var gap = new Pen(Chrome, 3f * _k)) g.DrawPath(gap, path);
                using var ring = new Pen(Accent, 2f * _k);
                g.DrawPath(ring, path);
            }
        }

        protected override void OnMouseDown(MouseEventArgs e)
        {
            base.OnMouseDown(e);
            if (e.Button != MouseButtons.Left) return;
            int i = ItemAt(e.Location);
            if (i < 0) return;
            // A click hides the ring. The flag tells OnGotFocus this focus came from the mouse.
            _showRing = false;
            _pointerFocus = true;
            try { Focus(); }
            finally { _pointerFocus = false; }
            SelectedIndex = i;
        }

        protected override void OnMouseMove(MouseEventArgs e)
        {
            base.OnMouseMove(e);
            int h = ItemAt(e.Location);
            if (h == _hover) return;
            _hover = h;
            Invalidate();
        }

        protected override void OnMouseLeave(EventArgs e)
        {
            base.OnMouseLeave(e);
            if (_hover == -1) return;
            _hover = -1;
            Invalidate();
        }

        // Without this the arrow keys go to the form's tab order, which moves focus off the sidebar
        // instead of moving the selection. Home and End are included for the same reason.
        protected override bool IsInputKey(Keys key) => key is Keys.Up or Keys.Down or Keys.Home or Keys.End
                                                       || base.IsInputKey(key);

        protected override void OnKeyDown(KeyEventArgs e)
        {
            int target = e.KeyCode switch
            {
                Keys.Up => _selected - 1,
                Keys.Down => _selected + 1,
                Keys.Home => 0,
                Keys.End => _items.Count - 1,
                _ => int.MinValue,
            };
            if (target == int.MinValue || _items.Count == 0) { base.OnKeyDown(e); return; }
            _showRing = true;              // keys are keyboard focus, so the ring shows
            SelectedIndex = target;        // clamped; a no-op at either end, so repaint anyway
            Invalidate();
            e.Handled = true;
            e.SuppressKeyPress = true;
        }

        protected override void OnGotFocus(EventArgs e)
        {
            base.OnGotFocus(e);
            _showRing = !_pointerFocus;
            Invalidate();
        }

        protected override void OnLostFocus(EventArgs e)
        {
            base.OnLostFocus(e);
            _showRing = false;
            Invalidate();
        }

        protected override AccessibleObject CreateAccessibilityInstance() => new ListAccessible(this);

        /// <summary>The List that holds the rows. Its children are created on demand from the row list.</summary>
        sealed class ListAccessible : ControlAccessibleObject
        {
            readonly NavSidebar _owner;

            public ListAccessible(NavSidebar owner) : base(owner) => _owner = owner;

            public override int GetChildCount() => _owner._items.Count;

            public override AccessibleObject? GetChild(int index) =>
                index >= 0 && index < _owner._items.Count ? new ItemAccessible(_owner, index) : null;
        }

        /// <summary>One row for a screen reader: its label, its position, and whether it is the selection.</summary>
        sealed class ItemAccessible : AccessibleObject
        {
            readonly NavSidebar _owner;
            readonly int _index;

            public ItemAccessible(NavSidebar owner, int index)
            {
                _owner = owner;
                _index = index;
            }

            public override string? Name => _owner._items[_index].Text;

            public override AccessibleRole Role => AccessibleRole.ListItem;

            public override AccessibleObject Parent => _owner.AccessibilityObject;

            public override Rectangle Bounds => _owner.RectangleToScreen(Rectangle.Round(_owner.ItemRect(_index)));

            public override AccessibleStates State
            {
                get
                {
                    var s = AccessibleStates.Selectable | AccessibleStates.Focusable;
                    if (_owner._selected == _index)
                    {
                        s |= AccessibleStates.Selected;
                        if (_owner.Focused) s |= AccessibleStates.Focused;
                    }
                    return s;
                }
            }

            public override void DoDefaultAction() => _owner.SelectedIndex = _index;
        }
    }

    /// <summary>
    /// A slider painted to Fluent's proportions: 4px round track, 20px round thumb, accent fill to
    /// the left of the knob.
    ///
    /// A plain Control, not a TrackBar subclass: the native control's drag and notification behaviour
    /// is not reliable enough to build on, so the value, the events and the painting are all owned here.
    /// </summary>
    public sealed class Slider : Control
    {
        int _min, _max = 100, _value;

        /// <summary>Raised whenever Value changes, by a drag, a click, the keyboard or code.</summary>
        public event EventHandler? ValueChanged;

        public Slider()
        {
            SetStyle(ControlStyles.UserPaint | ControlStyles.AllPaintingInWmPaint
                   | ControlStyles.OptimizedDoubleBuffer | ControlStyles.ResizeRedraw
                   | ControlStyles.Selectable, true);
            Height = 32;
            Cursor = Cursors.Hand;
            TabStop = true;
        }

        // DefaultValue is what the WinForms designer analyser (WFO1000) asks of a public property on
        // a Control. These are not designer-serialised here, but the attribute is the documented way
        // to say so and the build treats the warning as an error.
        [DefaultValue(0)]
        public int Minimum
        {
            get => _min;
            set { _min = value; _max = Math.Max(_max, _min + 1); Value = _value; Invalidate(); }
        }

        [DefaultValue(100)]
        public int Maximum
        {
            get => _max;
            set { _max = Math.Max(value, _min + 1); Value = _value; Invalidate(); }
        }

        [DefaultValue(0)]
        public int Value
        {
            get => _value;
            set
            {
                int v = Math.Clamp(value, _min, _max);
                if (v == _value) return;
                _value = v;
                Invalidate();
                ValueChanged?.Invoke(this, EventArgs.Empty);
            }
        }

        float _dpiScale = 1f;

        /// <summary>
        /// Scale factor for this control's pixel sizes (height, thumb, track, focus ring), set by the owning
        /// form from its DPI. Changing it resizes the control, as NavSidebar does.
        /// </summary>
        [DefaultValue(1f)]
        public float DpiScale
        {
            get => _dpiScale;
            set { _dpiScale = value; Height = S(32); Invalidate(); }
        }

        /// <summary>A pixel size scaled by <see cref="DpiScale"/>.</summary>
        int S(int v) => (int)Math.Round(v * DpiScale);

        /// <summary>Thumb diameter in pixels at the current DPI.</summary>
        int Thumb => S(20);

        /// <summary>Knob centre for a value. Kept in one place so hit-testing and painting agree.</summary>
        int KnobX(int v) => (int)Math.Round(
            (double)(v - _min) / Math.Max(1, _max - _min) * (Width - Thumb)) + Thumb / 2;

        /// <summary>The value a click at x means: a jump to the position, which is what a slider is.</summary>
        int ValueAt(int x)
        {
            int span = Math.Max(1, Width - Thumb);
            return _min + (int)Math.Round((double)Math.Clamp(x - Thumb / 2, 0, span) / span * (_max - _min));
        }

        protected override void OnMouseDown(MouseEventArgs e)
        {
            if (e.Button == MouseButtons.Left)
            {
                Focus();
                Capture = true;
                Value = ValueAt(e.X);      // a click anywhere on the track moves the knob there
            }
            base.OnMouseDown(e);
        }

        protected override void OnMouseMove(MouseEventArgs e)
        {
            if (Capture) Value = ValueAt(e.X);   // dragging tracks the pointer, uncapped
            base.OnMouseMove(e);
        }

        protected override void OnMouseUp(MouseEventArgs e)
        {
            Capture = false;
            base.OnMouseUp(e);
        }

        // Without this the arrow keys are treated as navigation and the form's tab order eats them,
        // so a slider you can reach with Tab but cannot move.
        protected override bool IsInputKey(Keys key) => key switch
        {
            Keys.Left or Keys.Right or Keys.Up or Keys.Down
                or Keys.Home or Keys.End or Keys.PageUp or Keys.PageDown => true,
            _ => base.IsInputKey(key),
        };

        protected override void OnKeyDown(KeyEventArgs e)
        {
            int step = Math.Max(1, (_max - _min) / 20);
            switch (e.KeyCode)
            {
                case Keys.Left or Keys.Down: Value -= step; break;
                case Keys.Right or Keys.Up: Value += step; break;
                case Keys.PageDown: Value -= (_max - _min) / 5; break;
                case Keys.PageUp: Value += (_max - _min) / 5; break;
                case Keys.Home: Value = _min; break;
                case Keys.End: Value = _max; break;
                default: base.OnKeyDown(e); return;
            }
            e.Handled = true;
        }

        protected override void OnGotFocus(EventArgs e) { base.OnGotFocus(e); Invalidate(); }
        protected override void OnLostFocus(EventArgs e) { base.OnLostFocus(e); Invalidate(); }

        protected override void OnPaint(PaintEventArgs e)
        {
            var g = e.Graphics;
            // The whole control is ours, so there is no clip imposed from outside and no need to
            // defend against one: a full clear here is the entire story. The trail this replaces
            // came from the native control narrowing the paint to where its own thumb had moved.
            using (var bg = new SolidBrush(BackColor))
                g.FillRectangle(bg, ClientRectangle);
            g.SmoothingMode = SmoothingMode.AntiAlias;

            int y = Height / 2;
            int knobX = KnobX(_value);
            int trackH = S(4);

            // Round() allocates a GraphicsPath, which holds a native GDI handle. Both paths below
            // have to be disposed: leaking two per repaint is what the 20x open/close test caught.
            using (var track = Round(new Rectangle(0, y - trackH / 2, Width, trackH), trackH / 2))
            using (var bg = new SolidBrush(Line))
                g.FillPath(bg, track);

            if (Enabled)
            {
                using var fill = Round(new Rectangle(0, y - trackH / 2, Math.Max(0, knobX), trackH), trackH / 2);
                using var lit = new SolidBrush(Accent);
                g.FillPath(lit, fill);
            }

            // Focus has to be drawn here: the OS ring would land on a surface we have painted over.
            // The gap is the control's own background, the pixels the ring sits on, not a fixed colour.
            if (Focused)
            {
                int r = S(15);
                var fr = new Rectangle(knobX - r, y - r, 2 * r, 2 * r);
                using (var gap = new Pen(BackColor, S(3))) g.DrawEllipse(gap, fr);
                using var ring = new Pen(Accent, S(2));
                g.DrawEllipse(ring, fr);
            }

            using (var body = new SolidBrush(Enabled ? Ink : Muted))
                g.FillEllipse(body, knobX - Thumb / 2, y - Thumb / 2, Thumb, Thumb);
        }
    }

    /// <summary>
    /// A Windows 11 on/off switch: a 40x20 pill with a thumb, and the label to its right. It is a CheckBox
    /// underneath, so Checked, CheckedChanged, the Space key, mouse clicks and the CheckButton accessibility
    /// role all behave as the checkbox it replaces did; only the drawing differs. The pill is painted over
    /// the surface behind the control (its parent's BackColor), so it sits cleanly inside a card.
    /// On: Accent fill with a dark thumb. Off: a 1px Muted outline with a Muted thumb.
    /// The focus ring is shown for keyboard focus only: a mouse press hides it until a key is pressed.
    /// Sizes are multiplied by <see cref="DpiScale"/>, which the owning form sets from its DPI.
    /// </summary>
    public sealed class ToggleSwitch : CheckBox
    {
        public ToggleSwitch()
        {
            SetStyle(ControlStyles.UserPaint | ControlStyles.AllPaintingInWmPaint
                   | ControlStyles.OptimizedDoubleBuffer | ControlStyles.ResizeRedraw
                   | ControlStyles.Selectable, true);
            Appearance = Appearance.Normal;
            AutoSize = true;
        }

        float _dpiScale = 1f;

        /// <summary>Scale factor for the pill, thumb, label gap and focus ring. Resizes an AutoSize switch.</summary>
        [DefaultValue(1f)]
        public float DpiScale
        {
            get => _dpiScale;
            set
            {
                _dpiScale = value;
                if (AutoSize) Size = GetPreferredSize(Size.Empty);
                Invalidate();
            }
        }

        /// <summary>A pixel size scaled by <see cref="DpiScale"/>.</summary>
        int S(int v) => (int)Math.Round(v * DpiScale);

        int TrackW => S(40);
        int TrackH => S(20);
        int LabelGap => S(12);
        // Room around the pill for the focus ring, which is drawn 3px outside the track.
        int Pad => S(4);

        // True after a mouse press, so the focus ring stays hidden for mouse users. A key press clears it.
        bool _pointerFocus;

        /// <summary>
        /// Height is the taller of the pill and the text; width is pill, gap, text, and the padding for the ring.
        /// Overridden because the stock CheckBox sizes itself for a 13px box, not for this pill.
        /// </summary>
        public override Size GetPreferredSize(Size proposed)
        {
            var text = Text.Length > 0
                ? TextRenderer.MeasureText(Text, Font, new Size(int.MaxValue, int.MaxValue), TextFormatFlags.NoPadding)
                : Size.Empty;
            int labelW = text.Width > 0 ? LabelGap + text.Width : 0;
            return new Size(TrackW + labelW + 2 * Pad, Math.Max(TrackH, text.Height) + 2 * Pad);
        }

        // Disabled controls are drawn at half strength, the same convention the other controls use.
        Color Tone(Color c) => Enabled ? c : c.Faded(0.5f);

        protected override void OnPaint(PaintEventArgs e)
        {
            var g = e.Graphics;
            g.SmoothingMode = SmoothingMode.AntiAlias;
            // Fill with the surface behind us, so the switch takes its background from its card, not from Canvas.
            using (var bg = new SolidBrush(SurfaceBehind(this))) g.FillRectangle(bg, ClientRectangle);

            bool on = Checked;
            var track = new RectangleF(Pad, (Height - TrackH) / 2f, TrackW, TrackH);

            using (var pill = Capsule(track))
            {
                if (on)
                {
                    using var fill = new SolidBrush(Tone(Accent));
                    g.FillPath(fill, pill);
                }
                else
                {
                    // Outline inset by half its width, so the stroke stays inside the pill's bounds.
                    float w = Math.Max(1f, S(1));
                    using var outline = Capsule(RectangleF.Inflate(track, -w / 2f, -w / 2f));
                    using var edge = new Pen(Tone(Muted), w);
                    g.DrawPath(edge, outline);
                }
            }

            // The thumb sits at the right end when on (14px) and the left end when off (10px), each
            // inset from the track edge by the same margin, so it moves cleanly between the two.
            int onD = S(14), offD = S(10);
            int thumbD = on ? onD : offD;
            float thumbX = on
                ? track.Right - (TrackH - onD) / 2f - onD
                : track.X + (TrackH - offD) / 2f;
            float cy = track.Y + track.Height / 2f;
            using (var thumb = new SolidBrush(on ? Tone(Canvas) : Tone(Muted)))
                g.FillEllipse(thumb, thumbX, cy - thumbD / 2f, thumbD, thumbD);

            // The label is drawn by GDI, not GDI+, so it matches the rest of the window's text.
            int labelX = (int)track.Right + LabelGap;
            var labelRect = new Rectangle(labelX, 0, Math.Max(0, Width - Pad - labelX), Height);
            TextRenderer.DrawText(g, Text, Font, labelRect, Tone(Ink),
                TextFormatFlags.Left | TextFormatFlags.VerticalCenter | TextFormatFlags.SingleLine
                | TextFormatFlags.NoPadding | TextFormatFlags.NoPrefix);

            // Keyboard focus ring: a 2px Ink capsule drawn 3px outside the pill.
            if (Focused && !_pointerFocus)
            {
                float rw = Math.Max(1f, S(2));
                using var ring = new Pen(Ink, rw);
                using var ringPath = Capsule(RectangleF.Inflate(track, S(3), S(3)));
                g.DrawPath(ring, ringPath);
            }
        }

        protected override void OnMouseDown(MouseEventArgs e)
        {
            // A mouse press also focuses the control; hide the ring so only keyboard users see it.
            _pointerFocus = true;
            Invalidate();
            base.OnMouseDown(e);
        }

        protected override void OnKeyDown(KeyEventArgs e)
        {
            // Keyboard use from here on: show the ring.
            if (_pointerFocus) { _pointerFocus = false; Invalidate(); }
            base.OnKeyDown(e);
        }

        protected override void OnGotFocus(EventArgs e) { base.OnGotFocus(e); Invalidate(); }

        protected override void OnLostFocus(EventArgs e)
        {
            _pointerFocus = false;
            base.OnLostFocus(e);
            Invalidate();
        }

        protected override void OnCheckedChanged(EventArgs e) { base.OnCheckedChanged(e); Invalidate(); }
        protected override void OnEnabledChanged(EventArgs e) { base.OnEnabledChanged(e); Invalidate(); }
        protected override void OnParentBackColorChanged(EventArgs e) { base.OnParentBackColorChanged(e); Invalidate(); }

        protected override void OnTextChanged(EventArgs e)
        {
            base.OnTextChanged(e);
            if (AutoSize) Size = GetPreferredSize(Size.Empty);
            Invalidate();
        }
    }

    /// <summary>Paints a soft card: white fill, hairline border, rounded corners.</summary>
    public sealed class CardPanel : Panel
    {
        public CardPanel()
        {
            DoubleBuffered = true;
            BackColor = Card;
            Padding = new Padding(8);
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            base.OnPaint(e);
            e.Graphics.SmoothingMode = SmoothingMode.AntiAlias;
            Fill(e.Graphics, new RectangleF(0.5f, 0.5f, Width - 1f, Height - 1f), Card, Radius, Line);
        }

        protected override void OnResize(EventArgs e)
        {
            base.OnResize(e);
            Round2(this, Radius);
        }
    }

    /// <summary>
    /// Renderer for the tray context menu. The stock renderer paints in Windows system menu colours,
    /// which do not match this window's dark palette. Every colour here comes from the palette above.
    /// The menu background is Canvas and hover is Hover, a clear step apart, so the highlighted row is
    /// visible without adding a new colour. Text is forced to Ink (Muted when disabled), because the
    /// stock renderer takes text colour from the system menu colour, not from the menu's ForeColor.
    /// </summary>
    public sealed class TrayMenuRenderer : ToolStripProfessionalRenderer
    {
        public TrayMenuRenderer() : base(new Palette()) { }

        /// <summary>
        /// Maps the theme palette onto the stock menu regions. UseSystemColors is off so the table
        /// never falls back to the OS menu colours, which would undo the mapping.
        /// </summary>
        sealed class Palette : ProfessionalColorTable
        {
            public Palette() => UseSystemColors = false;

            public override Color ToolStripDropDownBackground => Canvas;
            public override Color ImageMarginGradientBegin => Canvas;
            public override Color ImageMarginGradientMiddle => Canvas;
            public override Color ImageMarginGradientEnd => Canvas;
            public override Color MenuItemSelected => Hover;
            public override Color MenuItemSelectedGradientBegin => Hover;
            public override Color MenuItemSelectedGradientEnd => Hover;
            // Pressed uses the same fill as hover. Without this the stock pressed state keeps its
            // system blue, which would be the only blue in an otherwise grey menu.
            public override Color MenuItemPressedGradientBegin => Hover;
            public override Color MenuItemPressedGradientEnd => Hover;
            public override Color MenuItemBorder => Line;
            public override Color MenuBorder => Line;
            public override Color SeparatorDark => Line;
            public override Color SeparatorLight => Line;
        }

        /// <summary>Sets the text colour before the stock renderer draws it.</summary>
        protected override void OnRenderItemText(ToolStripItemTextRenderEventArgs e)
        {
            e.TextColor = e.Item.Enabled ? Ink : Muted;
            base.OnRenderItemText(e);
        }
    }

}
