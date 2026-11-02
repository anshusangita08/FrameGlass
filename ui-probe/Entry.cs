using System.Drawing.Imaging;
using System.Reflection;
using System.Text;
using System.Windows.Forms;
using FrameGlass;

namespace UiProbe;

static class Entry
{
    static readonly StringBuilder Out = new();
    static void W(string s = "") => Out.AppendLine(s);

    [STAThread]
    static void Main(string[] args)
    {
        // An unhandled exception on the UI thread pops a modal JIT dialog, which blocks the probe
        // forever and loses the report. Print it instead: a crash has to be visible in the output,
        // not in a dialog somebody has to dismiss.
        Application.SetUnhandledExceptionMode(UnhandledExceptionMode.CatchException);
        // The probe drives the real SettingsForm, so it needs the same process-wide dark system
        // rendering the shipping exe asks for, or the captures show a different window than users get.
        try { Application.SetColorMode(SystemColorMode.Dark); } catch { /* not on this build */ }
        Application.ThreadException += (_, e) =>
        {
            Console.WriteLine("!! UI THREAD EXCEPTION");
            Console.WriteLine(e.Exception.ToString());
        };

        // The shipping exe carries a requireAdministrator manifest, so --selftest cannot be run
        // from an unelevated shell. The probe has no such manifest, so it can drive the very same
        // Composer.SelfTest() and prove the checks pass.
        if (args.Contains("--selftest"))
        {
            int fail = FrameGlass.Composer.SelfTest();
            Console.WriteLine(fail == 0 ? "SELFTEST OK" : $"SELFTEST FAILED ({fail})");
            Environment.ExitCode = fail;
            return;
        }

        // The sidebar probes build and capture the real form, so they need the same per-monitor DPI
        // awareness the shipping exe runs with. Without it, screen coordinates and the form disagree.
        if (args.Contains("--shots") || args.Contains("--shots-real") || args.Contains("--keys") || args.Contains("--names"))
        {
            Native.SetProcessDpiAwarenessContext(new IntPtr(-4));   // PER_MONITOR_AWARE_V2, as Program does
            ApplicationConfiguration.Initialize();
        }
        if (args.Contains("--shots-real"))
        {
            // --shots-real <dir>. Real monitors and real DPI, unlike --shots which emulates scale.
            int at = Array.IndexOf(args, "--shots-real");
            string dir = at + 1 < args.Length && !args[at + 1].StartsWith("--")
                ? args[at + 1]
                : Path.Combine("..", "tmp", "s1-real");
            Environment.ExitCode = RealDpi.Run(Path.GetFullPath(dir));
            return;
        }
        if (args.Contains("--names")) { Environment.ExitCode = Navigation.Names(); return; }
        if (args.Contains("--keys")) { Environment.ExitCode = Navigation.Keyboard(); return; }
        if (args.Contains("--shots"))
        {
            // --shots <dir>. Without a directory, the repo's git-ignored tmp/win11-shots (run from ui-probe).
            int at = Array.IndexOf(args, "--shots");
            string dir = at + 1 < args.Length && !args[at + 1].StartsWith("--")
                ? args[at + 1]
                : Path.Combine("..", "tmp", "win11-shots");
            Environment.ExitCode = Navigation.Shots(Path.GetFullPath(dir));
            return;
        }
        if (args.Contains("--bisect")) { Bisect.Run(); return; }
        if (args.Contains("--scale")) { Bisect.Scale(); return; }
        if (args.Contains("--sustained")) { Bisect.Sustained(); return; }
        if (args.Contains("--selfcheck")) { Bisect.SelfCheck(); return; }
        if (args.Contains("--perf")) { Perf.Run(); return; }
        if (args.Contains("--fixes")) { Fixes.Run(); return; }
        if (args.Contains("--icons")) { RunIcons(); return; }
        if (args.Contains("--hero")) { Hero.Run(); return; }
        if (args.Contains("--drag")) { Drag.Run(); return; }
        if (args.Contains("--tab"))
        {
            int i = args.SkipWhile(a => a != "--tab").Skip(1).Select(a => int.TryParse(a, out var n) ? n : 1).FirstOrDefault();
            Layout.Run(i);
            return;
        }
        if (args.Contains("--leak")) { LeakTest.Run(); return; }

        var probeDir = AppContext.BaseDirectory;
        var shotDir = Path.Combine(probeDir, "shots");
        Directory.CreateDirectory(shotDir);

        // Deterministic fixture. Deliberately includes network + disk rows and an unusual
        // font, so "does the UI exercise everything" cannot depend on the operator's saved
        // config. Also proves the app tolerates a hand-written config.
        var cfg = BuildFixture();

        Native.SetProcessDpiAwarenessContext(new IntPtr(-4));   // PER_MONITOR_AWARE_V2, as Program does
        ApplicationConfiguration.Initialize();

        W($"probe dir : {probeDir}");
        W($"screen     : {Screen.PrimaryScreen?.Bounds}");
        W();

        SettingsForm? form = null;
        try
        {
            form = new SettingsForm(
                cfg,
                () => FrameStats.Empty,
                () => new Reading(),
                _ => { });

            form.StartPosition = FormStartPosition.Manual;
            form.Location = new Point(40, 40);
            // Topmost, or CopyFromScreen captures whatever else is on screen at those coordinates.
            form.TopMost = true;
            form.Show();
            Pump(6);

            W($"form size  : {form.Size}  (client {form.ClientSize})");
            W($"form font  : {form.Font.Name} {form.Font.Size}pt");
            W($"form bg    : {Hex(form.BackColor)}   fg {Hex(form.ForeColor)}");
            W();

            var nav = Navigation.Nav(form);
            if (nav is null) { W("!! no NavSidebar found"); }
            else
            {
                W($"sidebar: {nav.AccessibilityObject.GetChildCount()} items, selected={nav.SelectedIndex}");
                W();
            }

            // ---- per-page capture + audit ----
            // Pages are reached through the sidebar, as a user reaches them. The "tab" file prefix is
            // kept because the pixel check below globs for it.
            if (nav is not null)
            {
                for (int i = 0; i < Navigation.PageNames.Length; i++)
                {
                    nav.SelectedIndex = i;
                    Application.DoEvents();
                    Pump(4);

                    var name = Navigation.PageNames[i];
                    var file = Path.Combine(shotDir, $"tab{i}-{Sanitize(name)}.png");
                    Capture(form, file);
                    W($"captured [{i}] {name} -> {Path.GetFileName(file)}");
                }
            }

            W();
            W("================ CONTROL TREE AUDIT ================");
            Audit(form);

            W();
            // The sidebar replaced the tab strip, so its checks replace the strip checks: four named
            // items, and each selection shows exactly one page. Printed under its own SIDEBAR heading.
            Navigation.SidebarReport(form, s => W(s));

            W();
            W("================ LIGHT-COLOUR LEAKS ================");
            // Only a *background* that is light is a leak. A light ForeColor is the intended design:
            // Theme.Ink is #E6ECF4, light text on a dark fill. Flagging every light colour reported
            // the app's own palette as a defect, which made the section useless.
            //
            // BackColor is also the wrong place to look for a system-drawn control, because a
            // ComboBox with a dark BackColor still paints its own list and arrow with system
            // colours. Those are caught by the pixel check below, which looks at what was actually
            // rendered rather than what a property claims.
            var dark = Color.FromArgb(0x1B, 0x1F, 0x26);
            int leaks = 0;
            foreach (var c in Walk(form))
            {
                if (c.BackColor.A != 255) continue;
                double lum = Lum(c.BackColor);
                if (lum > 200 && Math.Abs(lum - Lum(dark)) > 60)
                {
                    leaks++;
                    W($"  BackColor={Hex(c.BackColor)} on {c.GetType().Name} '{Trim(c.Text, 24)}' bounds={c.Bounds}");
                }
            }
            W(leaks == 0 ? "  none: no light background anywhere in the tree" : $"  {leaks} light background(s)");

            W();
            W("---- rendered pixels: light areas inside the window ----");
            // The property walk above can only see what the app set. This sees what Windows drew,
            // which is the only way to catch a system control rendering light inside a dark window.
            var win = form.RectangleToScreen(form.ClientRectangle);
            var vs = SystemInformation.VirtualScreen;
            W($"  window rect in image: X={win.X - vs.Left} Y={win.Y - vs.Top} " +
              $"W={win.Width} H={win.Height}   (image {vs.Width}x{vs.Height}, virtual origin {vs.X},{vs.Y})");
            foreach (var file in Directory.GetFiles(shotDir, "tab*.png").OrderBy(x => x))
            {
                var hot = LightPixels(file, win);
                W($"  {Path.GetFileName(file),-22} {hot.Count,5} px ({100.0 * hot.Count / Math.Max(1, hot.Total),5:0.00}%)  " +
                  (hot.Count == 0 ? "clean" : $"at {hot.Where}  brightest {hot.Brightest}"));
            }

            W();
            W("================ SCROLLABILITY, EVERY TAB ================");
            // "Fully scrollable" is a claim, so it gets checked per tab rather than trusted: a
            // Fill-docked body inside an AutoScroll host is clipped and never scrolls, which is how
            // the About tab used to lose its bottom third with no scrollbar to show for it.
            if (nav is not null)
            {
                for (int i = 0; i < Navigation.PageNames.Length; i++)
                {
                    var page = Navigation.PageAt(form, i)!;
                    nav.SelectedIndex = i;
                    Application.DoEvents(); Pump(3);

                    var host = Walk(page).OfType<ScrollableControl>().FirstOrDefault(s => s.AutoScroll);
                    var pageRect = page.RectangleToScreen(page.ClientRectangle);

                    // Anything whose preferred height exceeds the visible page, and is not itself
                    // scrollable, is content the user cannot reach.
                    var cut = new List<string>();
                    foreach (var c in Walk(page))
                    {
                        if (c is ScrollableControl { AutoScroll: true }) continue;
                        if (c.Dock is DockStyle.Top or DockStyle.Bottom) continue;   // the scrolled body
                        var r = c.Parent?.RectangleToScreen(c.Bounds) ?? Rectangle.Empty;
                        if (r.IsEmpty || r.Height == 0) continue;
                        if (r.Bottom > pageRect.Bottom + 2 || r.Top < pageRect.Top - 2)
                            cut.Add($"{c.GetType().Name} '{Trim(c.Text, 22)}' y={r.Top}..{r.Bottom}");
                    }

                    string scroll = host is null ? "no auto-scroll host"
                        : $"host={host.GetType().Name} max={host.VerticalScroll.Maximum} v={host.VerticalScroll.Value}"
                          + (host.VerticalScroll.Maximum > host.VerticalScroll.Value ? " scrollable" : " fits");
                    W($"  [{Navigation.PageNames[i]}] {scroll}  offscreen={cut.Count}");
                    foreach (var s in cut.Take(4)) W($"        OFFSCREEN {s}");
                }
            }

            W();
            W("================ OVERLAY PAGE: DESIGN CONTROLS ================");
            // Design was merged into the "Overlay" page; the swatches and fades live there now.
            var overlay = Navigation.PageAt(form, 0);
            if (nav is null || overlay is null) { W("  no Overlay page"); }
            else
            {
                nav.SelectedIndex = 0;
                Application.DoEvents(); Pump(3);
                var box = Walk(overlay).OfType<CheckBox>()
                            .FirstOrDefault(c => c.Text.Contains("panel behind"));
                // Theme.Slider is the custom slider; TrackBar is no longer used by the app.
                var bars = Walk(overlay).OfType<Theme.Slider>().ToList();
                W($"  'Draw the panel' Checked={box?.Checked}");
                foreach (var b in bars)
                    W($"  Slider Enabled={b.Enabled} value={b.Value} fore={Hex(b.ForeColor)} (bg fade should track the checkbox)");

                // Font/preset combo back colours side by side
                var combos = Walk(overlay).OfType<ComboBox>().ToList();
                foreach (var cb in combos)
                    W($"  ComboBox '{Trim(cb.Text, 18)}' back={Hex(cb.BackColor)} fore={Hex(cb.ForeColor)} flat={cb.FlatStyle}");

                // swatch buttons: does each one actually show its config colour?
                foreach (var b in Walk(overlay).OfType<Button>().Where(x => x.Tag as string == "swatch"))
                    W($"  swatch '{b.Text}' back={Hex(b.BackColor)} fore={Hex(b.ForeColor)}");
            }
        }
        catch (Exception e)
        {
            W("!! probe threw: " + e);
        }
        finally
        {
            try { form?.Close(); form?.Dispose(); } catch { }
        }

        var report = Path.Combine(probeDir, "probe-report.txt");
        File.WriteAllText(report, Out.ToString());
        Console.WriteLine(Out.ToString());
    }

    /// <summary>
    /// Fluent icon font check (--icons). Proves every glyph constant in FrameGlass.Icons is in the
    /// embedded subset and renders to real ink, then writes shots/icons.png as a visual record.
    ///
    /// Each glyph is rendered at 20 px and must produce a 20x20 bitmap with at least one pixel of
    /// alpha > 0. Ink touching the outer border is allowed: Icons.Draw fits the ink to the full
    /// rect by design. Dimensions are checked so a changed render size cannot pass unnoticed.
    ///
    /// The strip shows every glyph at 16, 24 and 32 px (one row per size) so a blurry or
    /// wrong-size glyph is visible in the image, not only in the pixel count.
    ///
    /// ExitCode = number of failing glyphs, matching the --fixes convention.
    /// </summary>
    static void RunIcons()
    {
        if (!Icons.Available)
        {
            Console.WriteLine("ICONS FAIL: font not available");
            Environment.ExitCode = 1;
            return;
        }

        int failures = 0;
        foreach (char c in Icons.All)
        {
            using var bmp = Icons.Render(c, 20, Color.White);
            bool ok = bmp is not null && bmp.Width == 20 && bmp.Height == 20 && HasInk(bmp);
            if (!ok) failures++;
            Console.WriteLine($"icon U+{(int)c:X4} {(ok ? "ok" : "FAIL")}");
        }

        // Visual record: one row per size, each glyph in a fixed-pitch cell so sizes line up.
        // Cell pitch is 32 px (the largest glyph) plus 8 px gap; 8 px padding on every side.
        int[] sizes = [16, 24, 32];
        const int Pad = 8, Cell = 32, Gap = 8;
        int width = Pad * 2 + Icons.All.Length * (Cell + Gap) - Gap;
        int height = Pad * 2 + sizes.Length * Cell + (sizes.Length - 1) * Gap;

        var shotDir = Path.Combine(AppContext.BaseDirectory, "shots");
        Directory.CreateDirectory(shotDir);
        using (var strip = new Bitmap(width, height))
        using (var g = Graphics.FromImage(strip))
        {
            g.Clear(Color.FromArgb(32, 32, 32));
            for (int row = 0; row < sizes.Length; row++)
            {
                int size = sizes[row];
                int bandY = Pad + row * (Cell + Gap);
                for (int i = 0; i < Icons.All.Length; i++)
                {
                    using var glyph = Icons.Render(Icons.All[i], size, Color.White);
                    if (glyph is null) continue;   // already counted as a failure above
                    // Centre each glyph vertically in its band; 1:1 draw, no scaling.
                    int x = Pad + i * (Cell + Gap);
                    int y = bandY + (Cell - size) / 2;
                    g.DrawImage(glyph, x, y, size, size);
                }
            }
            strip.Save(Path.Combine(shotDir, "icons.png"), ImageFormat.Png);
        }

        Console.WriteLine(failures == 0
            ? $"ICONS OK ({Icons.All.Length})"
            : $"ICONS FAIL ({failures} of {Icons.All.Length})");
        Environment.ExitCode = failures;
    }

    /// <summary>True when at least one pixel is not fully transparent. Uses GetPixel, which is fine at 20 px.</summary>
    static bool HasInk(Bitmap bmp)
    {
        for (int y = 0; y < bmp.Height; y++)
            for (int x = 0; x < bmp.Width; x++)
                if (bmp.GetPixel(x, y).A > 0) return true;
        return false;
    }

    internal static void Pump(int rounds)
    {
        for (int i = 0; i < rounds; i++) { Application.DoEvents(); Thread.Sleep(60); }
    }

    /// <summary>A known-shape config: every group present, odd-but-valid values, custom rows.</summary>
    internal static FrameGlass.Config BuildFixture()
    {
        var c = new FrameGlass.Config
        {
            Lines = new[]
            {
                "game", "app", "res", "fps", "fps1low", "frametime", "graph",
                "cpu", "cputemp", "cpuload", "cpupower",
                "gpu", "gputemp", "gpuload",
                "ram", "vram", "ping", "netup", "diskread", "time", "upscale",
            },
            HiddenGroups = Array.Empty<string>(),
            CustomRows = new List<string> { "my {fps} row" },
            RowColors = new Dictionary<string, string> { ["fps"] = "#40FF40" },
            Formats = new Dictionary<string, string> { ["cpu"] = "{cpus} <R> {cputemp}" },
        };
        return c;
    }

    // ------------------------------------------------------------------ capture

    static void Capture(Control form, string path)
    {
        try
        {
            form.Refresh();
            Application.DoEvents();
            // CopyFromScreen, not DrawToBitmap: a WM_PRINT capture does not include the DWM-composited
            // frame, so it rendered the title bar and window backdrop as light even after the app
            // had correctly asked for a dark one. This reads the real composited screen.
            //
            // Full virtual screen, not the window: a window-only crop cannot show what a tab looks
            // like against the rest of the desktop, and it hides anything the window is clipping.
            var vs = SystemInformation.VirtualScreen;
            int w = Math.Max(1, vs.Width), h = Math.Max(1, vs.Height);
            using var bmp = new Bitmap(w, h);
            using (var g = Graphics.FromImage(bmp))
                g.CopyFromScreen(vs.Left, vs.Top, 0, 0, bmp.Size);
            bmp.Save(path, ImageFormat.Png);
        }
        catch (Exception e) { W($"   !! capture failed {Path.GetFileName(path)}: {e.Message}"); }
    }

    // ------------------------------------------------------------------ audit

    static void Audit(Control root)
    {
        var all = Walk(root).ToList();
        W($"total controls: {all.Count}");
        W();

        // Unlabeled / unnamed interactive controls
        W("---- interactive controls with no label AND no tooltip AND no adjacent label ----");
        int unlabeled = 0;
        foreach (var c in all)
        {
            if (!IsInteractive(c)) continue;
            if (HasOwnLabel(c)) continue;
            if (!string.IsNullOrWhiteSpace(Tip(c))) continue;
            if (NearbyLabel(c) is not null) continue;
            unlabeled++;
            W($"  {Describe(c)}");
        }
        W(unlabeled == 0 ? "  (none)" : $"  count: {unlabeled}");
        W();

        // Tooltip coverage (README claims "every option has a hover tooltip")
        W("---- interactive controls with NO tooltip ----");
        int noTip = 0;
        foreach (var c in all)
        {
            if (!IsInteractive(c)) continue;
            if (!string.IsNullOrWhiteSpace(Tip(c))) continue;
            { noTip++; W($"  {Describe(c)}"); }
        }
        W(noTip == 0 ? "  (none)" : $"  count: {noTip}");
        W();

        // Off-grid padding/margin (4px or 6px steps instead of 8)
        W("---- Padding/Margin not on the 8px grid (informational) ----");
        foreach (var c in all)
        {
            foreach (var (kind, p) in new[] { ("Pad", c.Padding), ("Mar", c.Margin) })
            {
                if (p == Padding.Empty) continue;
                if (OffGrid(p)) W($"  {Describe(c)} {kind}={p}");
            }
        }
        W();

        // Zero-size / never-laid-out controls (dead controls)
        W("---- zero-size controls (never laid out) ----");
        int zero = 0;
        foreach (var c in all)
        {
            if (c is Form) continue;
            if (c.Width <= 0 || c.Height <= 0) { zero++; W($"  {Describe(c)}"); }
        }
        W(zero == 0 ? "  (none)" : $"  count: {zero}");
        W();

        // Clipped children (drawn outside parent bounds -> user cannot see/interact)
        W("---- children outside parent bounds (clipped) ----");
        int clipped = 0;
        foreach (var c in all)
        {
            if (c.Parent is null) continue;
            var pb = c.Parent.ClientRectangle;
            var cb = c.Bounds;
            if (cb.Right > pb.Width + 2 || cb.Bottom > pb.Height + 2 || cb.Left < -2 || cb.Top < -2)
            {
                if (c is ScrollableControl sc && sc.AutoScroll) continue;
                clipped++;
                W($"  {Describe(c)} bounds={cb} parent={c.Parent.GetType().Name} client={pb.Size}");
            }
        }
        W(clipped == 0 ? "  (none)" : $"  count: {clipped}");
        W();

        // Font usage
        W("---- distinct fonts in the tree ----");
        foreach (var g in all.Select(c => c.Font?.Name ?? "(null)").Distinct().OrderBy(x => x))
            W($"  {g}");
        W();

        // Colours actually used - shows whether the dark theme holds or default WinForms shows through
        W("---- distinct BackColors in the tree ----");
        foreach (var g in all.Select(c => Hex(c.BackColor)).Distinct().OrderBy(x => x))
            W($"  {g}");
        W();
        W("---- distinct ForeColors in the tree ----");
        foreach (var g in all.Select(c => Hex(c.ForeColor)).Distinct().OrderBy(x => x))
            W($"  {g}");
        W();

        // DataGridView rows
        foreach (var dg in all.OfType<DataGridView>())
        {
            W($"---- DataGridView '{dg.Name}': {dg.Columns.Count} cols, {dg.Rows.Count} rows ----");
            foreach (DataGridViewColumn col in dg.Columns)
                W($"   col '{col.HeaderText}' width={col.Width} vis={col.Visible}");
        }

        // Buttons with no accessible name
        W();
        W("---- buttons / controls with empty AccessibleName ----");
        int noAcc = 0;
        foreach (var c in all)
        {
            if (!IsInteractive(c)) continue;
            if (string.IsNullOrWhiteSpace(c.AccessibleName) && !HasOwnLabel(c)) { noAcc++; W($"  {Describe(c)}"); }
        }
        W(noAcc == 0 ? "  (none)" : $"  count: {noAcc}");
    }

    static bool OffGrid(Padding p) =>
        !On8(p.Left) || !On8(p.Top) || !On8(p.Right) || !On8(p.Bottom);

    static bool On8(int v) => v == 0 || v % 8 == 0;

    static bool IsInteractive(Control c) =>
        c is Button or CheckBox or RadioButton or ComboBox or TextBox or NumericUpDown
          or Theme.Slider or ListView or ListBox or DataGridView or PictureBox or LinkLabel
          or TreeView or DateTimePicker or MaskedTextBox;

    static bool HasOwnLabel(Control c) => c switch
    {
        Button { Text.Length: > 0 } => true,
        CheckBox { Text.Length: > 0 } => true,
        RadioButton { Text.Length: > 0 } => true,
        LinkLabel { Text.Length: > 0 } => true,
        _ => false,
    };

    /// <summary>Is there a Label immediately to the left, i.e. a visible field label?</summary>
    static Label? NearbyLabel(Control c)
    {
        var parent = c.Parent;
        if (parent is null) return null;
        foreach (Control sib in parent.Controls)
        {
            if (ReferenceEquals(sib, c) || sib is not Label l) continue;
            if (string.IsNullOrWhiteSpace(l.Text)) continue;
            if (Math.Abs(l.Top - c.Top) > c.Height) continue;
            if (l.Right <= c.Left && c.Left - l.Right < 260) return l;
        }
        return null;
    }

    static string Describe(Control c)
    {
        var txt = c.Text is { Length: > 0 } t ? $" text='{Trim(t, 40)}'" : "";
        var fld = c is not (Button or CheckBox or RadioButton or Label) ? $" field={FieldOf(c)}" : "";
        var tip = Tip(c);
        var tips = string.IsNullOrWhiteSpace(tip) ? "" : $" tip='{Trim(tip, 44)}'";
        return $"{c.GetType().Name,-14} name='{c.Name}'{fld}{txt}{tips} bounds={c.Bounds}";
    }

    static string FieldOf(Control c)
    {
        // the form's private fields, so a control can be tied back to its Config property
        var t = c;
        var fi = t.GetType().GetField(
            c.Name.TrimStart('_'), System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic);
        return fi is null ? "" : fi.FieldType.Name;
    }

    // ToolTipService is not public API on every WinForms version, so resolve it at runtime
    // rather than taking a compile-time reference to it.
    static readonly Type? TipService = typeof(ToolTip).Assembly.GetType("System.Windows.Forms.ToolTipService");

    static string Tip(Control c) =>
        TipService?.GetMethod("GetToolTip", BindingFlags.Public | BindingFlags.Static)
            ?.Invoke(null, new object[] { c }) as string ?? "";

    static string Trim(string s, int n) => s.Length <= n ? s : s[..n] + "...";

    static double Lum(Color c) => 0.299 * c.R + 0.587 * c.G + 0.114 * c.B;

    /// <summary>
    /// Count near-white pixels inside a window region of a capture, and say where they are.
    ///
    /// The capture is the whole virtual screen on purpose, so the wallpaper and any other window are
    /// in the image. Measuring the whole file reported the desktop, not the app: every tab came out
    /// at a near-identical 0.12% because that was the light wallpaper, not the UI. Only the window's
    /// own rectangle answers the question being asked.
    ///
    /// A count alone is not actionable, so the clusters are reported by position - a leak in the
    /// metrics table is a different bug from antialiased text on a slider, and the number cannot tell
    /// them apart.
    /// </summary>
    static (int Count, int Total, string Brightest, string Where) LightPixels(string path, Rectangle window)
    {
        using var bmp = new Bitmap(path);
        var vs = SystemInformation.VirtualScreen;
        // window is in screen coordinates; the bitmap starts at the virtual screen's origin.
        var r = Rectangle.Intersect(
            new Rectangle(window.X - vs.Left, window.Y - vs.Top, window.Width, window.Height),
            new Rectangle(0, 0, bmp.Width, bmp.Height));
        if (r.Width <= 0 || r.Height <= 0) return (0, 0, "- (window off-screen)", "-");

        int n = 0;
        string brightest = "-";
        double best = 0;
        // 8x8 buckets over the window, so a cluster's position is meaningful.
        const int Bx = 8, By = 8;
        var grid = new int[Bx, By];
        int bw = Math.Max(1, r.Width / Bx), bh = Math.Max(1, r.Height / By);

        for (int y = r.Y; y < r.Bottom; y += 2)
            for (int x = r.X; x < r.Right; x += 2)
            {
                var px = bmp.GetPixel(x, y);
                double l = Lum(px);
                if (l < 235) continue;         // light enough to read as a light-mode panel
                n++;
                grid[Math.Min(Bx - 1, (x - r.X) / bw), Math.Min(By - 1, (y - r.Y) / bh)]++;
                if (l > best) { best = l; brightest = Hex(px); }
            }

        // Name the busiest three buckets, in window-relative percentages so they can be found.
        var hot = new List<(int X, int Y, int C)>();
        for (int gy = 0; gy < By; gy++)
            for (int gx = 0; gx < Bx; gx++)
                if (grid[gx, gy] > 0) hot.Add((gx, gy, grid[gx, gy]));
        string where = hot.Count == 0
            ? "-"
            : string.Join(", ", hot.OrderByDescending(h => h.C).Take(3).Select(h =>
                $"{100.0 * h.X / Bx:0}%/{100.0 * h.Y / By:0}% ({h.C}px)"));
        return (n, r.Width * r.Height, brightest, where);
    }

    static string Hex(Color c) => c.A == 255 ? $"#{c.R:X2}{c.G:X2}{c.B:X2}" : $"#{c.R:X2}{c.G:X2}{c.B:X2} a={c.A}";

    static string Sanitize(string s)
    {
        foreach (var ch in Path.GetInvalidFileNameChars()) s = s.Replace(ch, '_');
        return s;
    }

    static IEnumerable<Control> Walk(Control c)
    {
        yield return c;
        foreach (Control k in c.Controls)
            foreach (var d in Walk(k))
                yield return d;
    }

}
