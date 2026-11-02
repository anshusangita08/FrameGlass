using System.Drawing.Imaging;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Windows.Forms;
using FrameGlass;

namespace UiProbe;

/// <summary>
/// Probes for the sidebar navigation that replaced the tab strip (S3, S7).
///
/// Each entry point is its own flag so a failure names its own check:
///   --shots DIR   PNGs of all four pages at 100, 150, 200, 250 and 300% (scrolled to top), at minimum window size,
///                 and one Overlay shot with keyboard focus on a toggle.
///   --keys        Down/End/Home change the page, Space flips a ToggleSwitch, and the Tab order of
///                 every page is printed in visual order with out-of-order items flagged.
///   --names       every interactive control's AccessibleName against the label on its own grid row.
///
/// Scale. The form takes its DPI factor from the monitor it is built on, and this machine has one
/// fixed factor. 100% and 150% are reached by replaying the form's own DPI-change path: the same
/// Scale call and field updates that SettingsForm's DpiChanged handler makes. That is reflection into
/// private members, so it is an emulation of a DPI change, not a real 150% monitor. The owner's visual
/// check on real hardware is still the proof for DPI.
/// </summary>
static class Navigation
{
    /// <summary>Page order is sidebar order. Each page panel is named by its title (SettingsForm.Page).</summary>
    public static readonly string[] PageNames = { "Overlay", "Rows", "Row details", "General", "About" };

    const int WM_KEYDOWN = 0x0100, WM_KEYUP = 0x0101;
    const int VK_SPACE = 0x20, VK_END = 0x23, VK_HOME = 0x24, VK_UP = 0x26, VK_DOWN = 0x28;
    const BindingFlags AnyInstance = BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public;

    [DllImport("user32.dll")] static extern bool PostMessage(IntPtr hWnd, int msg, IntPtr wParam, IntPtr lParam);
    [DllImport("user32.dll")] static extern IntPtr WindowFromPoint(Point p);
    [DllImport("user32.dll")] static extern bool GetComboBoxInfo(IntPtr hwndCombo, ref ComboBoxInfo info);

    /// <summary>The COMBOBOXINFO struct: the closed field's text rectangle and its arrow button rectangle.</summary>
    [System.Runtime.InteropServices.StructLayout(System.Runtime.InteropServices.LayoutKind.Sequential)]
    struct ComboBoxInfo
    {
        public int cbSize;
        public NativeRect rcItem;
        public NativeRect rcButton;
        public int stateButton;
        public IntPtr hwndCombo, hwndEdit, hwndList;
    }

    /// <summary>The Win32 RECT layout (left, top, right, bottom), which differs from Rectangle.</summary>
    [System.Runtime.InteropServices.StructLayout(System.Runtime.InteropServices.LayoutKind.Sequential)]
    struct NativeRect { public int L, T, R, B; }

    /// <summary>
    /// Reads the native closed-field geometry of a combo, in its client coordinates: rcItem is the text
    /// area and rcButton the arrow. Measured, not assumed, so the width check does not depend on guesses.
    /// </summary>
    static (Rectangle item, Rectangle button) ClosedFieldRects(ComboBox c)
    {
        var info = new ComboBoxInfo { cbSize = System.Runtime.InteropServices.Marshal.SizeOf<ComboBoxInfo>() };
        if (!GetComboBoxInfo(c.Handle, ref info)) return (Rectangle.Empty, Rectangle.Empty);
        static Rectangle R(NativeRect n) => Rectangle.FromLTRB(n.L, n.T, n.R, n.B);
        return (R(info.rcItem), R(info.rcButton));
    }
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] static extern int GetWindowText(IntPtr hWnd, System.Text.StringBuilder text, int max);
    [DllImport("user32.dll")] static extern IntPtr GetAncestor(IntPtr hwnd, uint flags);
    const uint GA_ROOT = 2;

    static string WindowText(IntPtr h)
    {
        if (h == IntPtr.Zero) return "(none)";
        var sb = new System.Text.StringBuilder(256);
        GetWindowText(h, sb, sb.Capacity);
        return $"'{sb}' hwnd={h}";
    }

    /// <summary>
    /// The top-level window that owns <paramref name="h"/>. WindowFromPoint returns the deepest child
    /// under the point (a spinner, a panel), so a shot's guard must compare roots, not raw handles.
    /// </summary>
    static IntPtr RootOf(IntPtr h) => h == IntPtr.Zero ? h : GetAncestor(h, GA_ROOT);

    // ------------------------------------------------------------------ shared helpers

    public static IEnumerable<Control> Walk(Control c)
    {
        yield return c;
        foreach (Control k in c.Controls)
            foreach (var d in Walk(k))
                yield return d;
    }

    public static Theme.NavSidebar? Nav(Control form) => Walk(form).OfType<Theme.NavSidebar>().FirstOrDefault();

    public static Panel? PageAt(Control form, int i)
    {
        if (i < 0 || i >= PageNames.Length) return null;
        return Walk(form).OfType<Panel>().FirstOrDefault(p => p.Name == PageNames[i]);
    }

    /// <summary>Names of the pages whose Visible flag is set. Exactly one should be set at a time.</summary>
    internal static string[] VisibleNames(Control form) =>
        Enumerable.Range(0, PageNames.Length)
            .Where(i => PageAt(form, i) is { Visible: true })
            .Select(i => PageNames[i]).ToArray();

    /// <summary>Selects a page through the sidebar's own property, then lets the form lay out and paint.</summary>
    internal static void Select(Theme.NavSidebar nav, int i)
    {
        nav.SelectedIndex = i;
        Entry.Pump(3);
    }

    /// <summary>
    /// Builds the settings form on the fixture config, shown at 100% DPI-equivalent, then scaled to
    /// <paramref name="target"/> (1.0 to 3.0, see Shots). Caller disposes.
    /// </summary>
    static SettingsForm Build(float target)
    {
        var form = new SettingsForm(Entry.BuildFixture(), () => FrameStats.Empty, () => new Reading(), _ => { });
        form.StartPosition = FormStartPosition.Manual;
        form.Location = new Point(40, 40);
        // Topmost, so CopyFromScreen reads this window and not whatever is above it.
        form.TopMost = true;
        form.Show();
        Entry.Pump(6);
        ScaleTo(form, target);
        Entry.Pump(4);
        return form;
    }

    /// <summary>
    /// Moves the form to a DPI factor the way its DpiChanged handler does: set the factor, call the
    /// form's Scale, then re-push the factor into the custom-painted controls and the window size.
    /// The steps mirror SettingsForm's handler line for line; if that handler changes, this must too.
    /// </summary>
    public static void ScaleTo(SettingsForm form, float target)
    {
        float k0 = Get<float>(form, "_k");
        if (Math.Abs(k0 - target) < 0.001f) return;
        float by = target / k0;
        Set(form, "_k", target);                      // the handler sets _k before it calls Scale
        form.Scale(new SizeF(by, by));                // the form's own Scale path
        var nav = Nav(form) ?? throw new InvalidOperationException("no NavSidebar");
        nav.DpiScale = target;
        nav.Width = (int)(200 * target);              // S(200)
        Invoke(form, "ApplyDpiScale", form);
        Invoke(form, "RebuildMarks");
        Invoke(form, "LayOutPages");                  // the handler's page layout pass, after the new heights
        // The same helpers the form's constructor and DpiChanged use, so the probe sees the working-area caps.
        form.ClientSize = (Size)Invoke(form, "WindowSize")!;
        form.MinimumSize = (Size)Invoke(form, "MinimumWindowSize")!;
    }

    static T Get<T>(object o, string name) =>
        (T)(o.GetType().GetField(name, AnyInstance) ?? throw new MissingFieldException(name)).GetValue(o)!;

    static void Set(object o, string name, object value) =>
        (o.GetType().GetField(name, AnyInstance) ?? throw new MissingFieldException(name)).SetValue(o, value);

    static object? Invoke(object o, string name, params object[] args) =>
        (o.GetType().GetMethod(name, AnyInstance) ?? throw new MissingMethodException(name)).Invoke(o, args);

    internal static bool CaptureWindow(Form form, string path)
    {
        try
        {
            form.Refresh();
            Application.DoEvents();
            // The window's own rectangle, read from the composited screen. Only the window is wanted:
            // the full virtual screen would put the desktop and other windows into every shot.
            var b = form.Bounds;
            // A shot is only evidence when the form is what the screen shows there. If another window
            // (the lock screen, a dialog) is on top, the PNG is of that window, so it is refused.
            // WindowFromPoint returns the deepest child under the point, so the comparison is made on the
            // root window of that child. The root must be the form itself; any other root is refused.
            var hit = WindowFromPoint(new Point(b.Left + b.Width / 2, b.Top + b.Height / 2));
            var root = RootOf(hit);
            if (root != form.Handle)
            {
                Console.WriteLine($"  !! not saved {Path.GetFileName(path)}: top window at form centre is " +
                                  $"{WindowText(root)} (child hit {WindowText(hit)}), not the form (lock screen or another window)");
                return false;
            }
            using var bmp = new Bitmap(b.Width, b.Height);
            using (var g = Graphics.FromImage(bmp))
                g.CopyFromScreen(b.Location, Point.Empty, b.Size);
            bmp.Save(path, ImageFormat.Png);
            return true;
        }
        catch (Exception e)
        {
            Console.WriteLine($"  !! capture failed {Path.GetFileName(path)}: {e.Message}");
            return false;
        }
    }

    /// <summary>
    /// Posts a key to a control's window and lets the message loop dispatch it. That is the same path a
    /// real keystroke takes once it reaches the control, so OnKeyDown, IsInputKey and the toggle's own
    /// key handling all run. Posting avoids depending on which window the OS has in the foreground.
    /// </summary>
    static void Press(Control target, int vk)
    {
        PostMessage(target.Handle, WM_KEYDOWN, new IntPtr(vk), IntPtr.Zero);
        Entry.Pump(2);
        PostMessage(target.Handle, WM_KEYUP, new IntPtr(vk), IntPtr.Zero);
        Entry.Pump(3);
    }

    // ------------------------------------------------------------------ default run section

    /// <summary>
    /// Default-run section, replacing the tab-strip checks. Four items, named in page order; each item
    /// selects exactly one page, and that page is the one with the matching name.
    /// </summary>
    public static int SidebarReport(Control form, Action<string> say)
    {
        var nav = Nav(form);
        say("================ SIDEBAR ================");
        if (nav is null) { say("  !! no Theme.NavSidebar found"); return 1; }

        int fail = 0;
        int n = nav.AccessibilityObject.GetChildCount();
        var navRect = nav.RectangleToScreen(nav.ClientRectangle);
        say($"  items: {n}  sidebar bounds={nav.Bounds}  DpiScale={nav.DpiScale:0.00}");
        if (n != PageNames.Length) { fail++; say($"  !! expected {PageNames.Length} items"); }
        for (int i = 0; i < n; i++)
        {
            var item = nav.AccessibilityObject.GetChild(i);
            string name = item?.Name ?? "(none)";
            bool inside = item is not null && navRect.Contains(item.Bounds);
            bool nameOk = i < PageNames.Length && name == PageNames[i];
            if (!nameOk || !inside) fail++;
            say($"  [{i}] '{name}' bounds={item?.Bounds}{(inside ? "" : "  !! outside sidebar")}{(nameOk ? "" : $"  !! expected '{(i < PageNames.Length ? PageNames[i] : "?")}'")}");
        }

        for (int i = 0; i < PageNames.Length; i++)
        {
            Select(nav, i);
            var visible = VisibleNames(form);
            bool ok = visible.Length == 1 && visible[0] == PageNames[i] && nav.SelectedIndex == i;
            if (!ok) fail++;
            say($"  select {i} -> visible pages [{string.Join(", ", visible)}]  {(ok ? "ok" : "FAIL")}");
        }
        say(fail == 0 ? "  sidebar: ok (4 items, one page visible per selection)" : $"  sidebar: {fail} failure(s)");
        return fail;
    }

    // ------------------------------------------------------------------ --shots

    /// <summary>
    /// Saves the PNG set under <paramref name="dir"/>. Returns the number of failed captures or checks.
    /// Each scale gets a fresh form, so rounding from one scale step cannot carry into the next.
    /// </summary>
    public static int Shots(string dir)
    {
        Directory.CreateDirectory(dir);
        int fail = 0;
        // 1.0 and 1.5 are the original scales; 2.0, 2.5 and 3.0 cover 200%, 250% and 300% monitors (S1).
        foreach (var target in new[] { 1.0f, 1.5f, 2.0f, 2.5f, 3.0f })
        {
            int pct = (int)Math.Round(target * 100);
            fail += ShotsAt(dir, target, pct, minimum: false);
            fail += ShotsAt(dir, target, pct, minimum: true);
        }
        Console.WriteLine($"output dir: {dir}");
        Console.WriteLine(fail == 0 ? "SHOTS OK" : $"SHOTS FAIL ({fail})");
        return fail;
    }

    static int ShotsAt(string dir, float target, int pct, bool minimum)
    {
        int fail = 0;
        string kind = minimum ? "min" : "full";
        using var form = Build(target);
        var nav = Nav(form) ?? throw new InvalidOperationException("no NavSidebar");
        if (minimum)
        {
            // The minimum size is the form's own MinimumSize, which ScaleTo has just updated.
            form.Size = form.MinimumSize;
            Entry.Pump(4);
        }
        Console.WriteLine($"[{pct}% {kind}] form client={form.ClientSize} min={form.MinimumSize}");

        for (int i = 0; i < PageNames.Length; i++)
        {
            Select(nav, i);
            ScrollToTop(PageAt(form, i)!);
            Entry.Pump(2);
            var visible = VisibleNames(form);
            if (visible.Length != 1 || visible[0] != PageNames[i])
            {
                fail++;
                Console.WriteLine($"  !! page {i} is not the only visible page: [{string.Join(", ", visible)}]");
            }
            string file = Path.Combine(dir, $"p{pct}-{kind}-{i}-{PageNames[i]}.png");
            if (CaptureWindow(form, file)) Console.WriteLine($"  {Path.GetFileName(file)}");
            else fail++;
        }

        if (!minimum)
        {
            // The Hot colour combo sits in the Design card, below the fold of the Overlay page. Scroll
            // its row into view and save one extra shot, so its closed field can be checked for clipping.
            Select(nav, 0);
            var hotCombo = Walk(PageAt(form, 0)!).OfType<ComboBox>()
                .FirstOrDefault(c => c.Items.Cast<object>().Any(i => i.ToString()!.StartsWith("orange", StringComparison.Ordinal)));
            if (hotCombo is null) { fail++; Console.WriteLine("  !! no Hot colour combo on the Overlay page"); }
            else
            {
                ScrollIntoView(hotCombo);
                var (item, button) = ClosedFieldRects(hotCombo);
                Console.WriteLine($"  hot combo width={hotCombo.Width} text area x={item.Left}..{item.Right} " +
                                  $"arrow x={button.Left}..{button.Right} item.Right<=arrow.Left: {item.Right <= button.Left}");
                string file = Path.Combine(dir, $"p{pct}-overlay-hot-colour.png");
                if (CaptureWindow(form, file)) Console.WriteLine($"  {Path.GetFileName(file)}");
                else fail++;
            }
        }

        if (!minimum)
        {
            // Keyboard focus on the first toggle of the Overlay page. Focus is moved by code, which is the
            // same state Tab produces; the shot has to show a focus ring on the toggle.
            Select(nav, 0);
            form.Activate();
            var sw = FirstTabStop<Theme.ToggleSwitch>(PageAt(form, 0)!);
            if (sw is null) { fail++; Console.WriteLine("  !! no ToggleSwitch on the Overlay page"); }
            else
            {
                sw.Focus();
                Entry.Pump(4);
                Console.WriteLine($"  focus on '{sw.Text}': Focused={sw.Focused}");
                if (!sw.Focused) fail++;
                string file = Path.Combine(dir, $"p{pct}-overlay-focus-toggle.png");
                if (CaptureWindow(form, file)) Console.WriteLine($"  {Path.GetFileName(file)}");
                else fail++;
            }
        }
        return fail;
    }

    /// <summary>
    /// Scrolls the nearest auto-scroll ancestor so that <paramref name="c"/> is inside its viewport. Uses the
    /// framework's own ScrollControlIntoView (protected, so called by reflection). Setting AutoScrollPosition or
    /// VerticalScroll.Value directly was measured to leave the position at 0 in this form, so they are not used.
    /// </summary>
    static void ScrollIntoView(Control c)
    {
        Control? up = c.Parent;
        while (up is not null && !(up is ScrollableControl { AutoScroll: true })) up = up.Parent;
        if (up is not ScrollableControl host) return;
        typeof(ScrollableControl).GetMethod("ScrollControlIntoView", AnyInstance)!.Invoke(host, new object[] { c });
        Entry.Pump(3);
    }

    internal static void ScrollToTop(Control page)
    {
        foreach (var s in Walk(page).OfType<ScrollableControl>().Where(s => s.AutoScroll))
        {
            s.VerticalScroll.Value = 0;
            s.AutoScrollPosition = Point.Empty;
        }
    }

    static T? FirstTabStop<T>(Control root) where T : Control =>
        Walk(root).OfType<T>().FirstOrDefault(c => c.TabStop && c.Visible);

    // ------------------------------------------------------------------ --keys

    /// <summary>Keyboard check: sidebar arrows, Space on a ToggleSwitch, and Tab order per page.</summary>
    public static int Keyboard()
    {
        int fail = 0;
        using var form = Build(1.0f);
        var nav = Nav(form) ?? throw new InvalidOperationException("no NavSidebar");
        form.Activate();
        Entry.Pump(3);

        Console.WriteLine("================ KEYBOARD ================");
        Select(nav, 0);
        nav.Focus();
        Entry.Pump(3);
        Console.WriteLine($"  sidebar Focused={nav.Focused}  form ContainsFocus={form.ContainsFocus}");

        // 1. Arrow keys on the sidebar change the page, one page per press, and stop at the ends.
        (string label, int vk, int expect)[] arrows =
        {
            ("Down", VK_DOWN, 1), ("Down", VK_DOWN, 2), ("End", VK_END, 3),
            ("Down at last page", VK_DOWN, 3), ("Home", VK_HOME, 0), ("Up at first page", VK_UP, 0),
        };
        foreach (var (label, vk, expect) in arrows)
        {
            Press(nav, vk);
            var visible = VisibleNames(form);
            bool ok = nav.SelectedIndex == expect && visible.Length == 1 && visible[0] == PageNames[expect];
            if (!ok) fail++;
            Console.WriteLine($"  {label,-17} selected={nav.SelectedIndex} visible=[{string.Join(", ", visible)}]  {(ok ? "ok" : "FAIL")}");
        }

        // 2. Space flips a ToggleSwitch on the Overlay page, and a second Space puts it back.
        Select(nav, 0);
        var sw = FirstTabStop<Theme.ToggleSwitch>(PageAt(form, 0)!);
        if (sw is null) { fail++; Console.WriteLine("  !! no ToggleSwitch on the Overlay page"); }
        else
        {
            sw.Focus();
            Entry.Pump(3);
            bool before = sw.Checked;
            Press(sw, VK_SPACE);
            bool afterOne = sw.Checked;
            Press(sw, VK_SPACE);
            bool afterTwo = sw.Checked;
            bool ok = sw.Focused && afterOne != before && afterTwo == before;
            if (!ok) fail++;
            Console.WriteLine($"  Space on '{sw.Text}': Focused={sw.Focused} {before} -> {afterOne} -> {afterTwo}  {(ok ? "ok" : "FAIL")}");
        }

        // 3. Tab order per page, compared with the visual order. An item is flagged when it starts above
        // the previous item, or on the same row but to its left.
        Console.WriteLine();
        Console.WriteLine("  TAB ORDER (page controls, in the order Tab visits them)");
        int flagged = 0;
        for (int i = 0; i < PageNames.Length; i++)
        {
            Select(nav, i);
            var seq = TabSequence(form, PageAt(form, i)!);
            Console.WriteLine($"  [{PageNames[i]}] {seq.Count} tab stops");
            for (int k = 0; k < seq.Count; k++)
            {
                var c = seq[k];
                var at = Origin(c, form);
                bool out_ = false;
                if (k > 0)
                {
                    var prev = seq[k - 1];
                    var pAt = Origin(prev, form);
                    bool above = at.Y < pAt.Y - prev.Height / 2;
                    bool sameRowLeft = Math.Abs(at.Y - pAt.Y) <= prev.Height / 2 && at.X < pAt.X;
                    out_ = above || sameRowLeft;
                }
                if (out_) flagged++;
                Console.WriteLine($"    {k,2}. {c.GetType().Name,-14} {Caption(c),-30} y={at.Y,4} x={at.X,4}{(out_ ? "  << OUT OF VISUAL ORDER" : "")}");
            }
        }
        Console.WriteLine(flagged == 0
            ? "  tab order: every page is in visual order"
            : $"  tab order: {flagged} item(s) out of visual order");
        fail += flagged;

        Console.WriteLine();
        Console.WriteLine(fail == 0 ? "KEYBOARD OK" : $"KEYBOARD FAIL ({fail})");
        return fail;
    }

    /// <summary>
    /// The controls inside <paramref name="page"/> in the order repeated Tab presses reach them, starting
    /// from the top of the form. Uses the form's own GetNextControl, which is what Tab calls.
    /// </summary>
    static List<Control> TabSequence(Control form, Control page)
    {
        var seq = new List<Control>();
        Control? first = null, cur = null;
        for (int guard = 0; guard < 2000; guard++)
        {
            cur = ((ContainerControl)form).GetNextControl(cur, true);
            if (cur is null || cur == first) break;
            first ??= cur;
            // Only TabStop controls are Tab stops. GetNextControl also returns labels and layout panels,
            // which the Tab key never lands on, so counting them would flag wrappers that are not focus targets.
            if (cur.TabStop && cur.Enabled && Within(cur, page)) seq.Add(cur);
        }
        return seq;
    }

    static bool Within(Control c, Control page)
    {
        for (Control? p = c; p is not null; p = p.Parent)
            if (p == page) return true;
        return false;
    }

    /// <summary>Top-left of a control in the form's client coordinates, so controls on different wrappers compare.</summary>
    static Point Origin(Control c, Control form) => form.PointToClient(c.PointToScreen(Point.Empty));

    static string Caption(Control c)
    {
        string text = !string.IsNullOrEmpty(c.AccessibleName) ? c.AccessibleName : c.Text;
        text = text.Replace("\n", " ");
        return text.Length <= 28 ? text : text[..28] + "...";
    }

    // ------------------------------------------------------------------ --names

    /// <summary>
    /// Dumps every interactive control's AccessibleName on every page and checks each one against the
    /// label in column 0 of its own grid row. The grid cell is the oracle: it is what a sighted user
    /// reads, and it is independent of the geometry the naming code used to rely on.
    ///
    /// Status per control:
    ///   ok        name equals the row label
    ///   BAD       name is empty, or is another row's label (the "Pin to window" bug)
    ///   explicit  name is a descriptive name set in code and is not a row label
    ///   -         not inside a labelled grid row
    /// </summary>
    public static int Names()
    {
        using var form = Build(1.0f);
        var nav = Nav(form) ?? throw new InvalidOperationException("no NavSidebar");
        int bad = 0, total = 0;
        Console.WriteLine("NAME DUMP: accessible name against the label on the control's own row");
        for (int i = 0; i < PageNames.Length; i++)
        {
            Select(nav, i);
            var page = PageAt(form, i)!;
            var rowLabels = Walk(page).OfType<TableLayoutPanel>()
                .SelectMany(t => t.Controls.OfType<Label>()
                    .Where(l => l.Text.Length > 0 && t.GetPositionFromControl(l).Column == 0))
                .Select(l => l.Text).ToHashSet();

            // About has no interactive controls, only labels. Its labels are checked too, so the card's
            // names are proven rather than assumed.
            foreach (var c in Walk(page).Where(c => IsNamedKind(c) || (PageNames[i] == "About" && c is Label)))
            {
                total++;
                string name = c.AccessibleName ?? "";
                string? row = OwnRowLabel(c);
                string status;
                if (row is null) status = "-";
                else if (name == row) status = "ok";
                else if (name.Length == 0 || (rowLabels.Contains(name) && name != row)) { status = "BAD"; bad++; }
                else status = "explicit";
                Console.WriteLine($"  {PageNames[i],-8} {c.GetType().Name,-14} name='{Trim(name, 34),-34}' row='{Trim(row ?? "-", 22)}' {status}");
            }
        }
        Console.WriteLine($"names: {total} controls, {bad} BAD");
        Console.WriteLine(bad == 0 ? "NAMES OK" : $"NAMES FAIL ({bad})");
        return bad;
    }

    static bool IsNamedKind(Control c) =>
        c is Button or CheckBox or RadioButton or ComboBox or TextBox or NumericUpDown
          or ListView or Theme.Slider or Theme.NavSidebar;

    /// <summary>The Label in column 0 of the nearest grid row that holds this control in a value column.</summary>
    static string? OwnRowLabel(Control c)
    {
        Control child = c;
        for (Control? p = c.Parent; p is not null && p is not Theme.CardPanel; child = p, p = p.Parent)
        {
            if (p is not TableLayoutPanel grid) continue;
            var pos = grid.GetPositionFromControl(child);
            if (pos.Column >= 1 && grid.GetControlFromPosition(0, pos.Row) is Label { Text.Length: > 0 } l)
                return l.Text;
            // Otherwise keep climbing: the control may sit in a wrapper table (a slider pair, a Track
            // row) whose own cells are not labels, and the row label is in the enclosing grid.
        }
        return null;
    }

    static string Trim(string s, int n) => s.Length <= n ? s : s[..n] + "...";
}
