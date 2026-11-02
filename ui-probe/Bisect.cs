using System.Drawing.Drawing2D;
using System.Runtime.InteropServices;
using System.Windows.Forms;

namespace UiProbe;

/// <summary>
/// Isolates which control type carries the GDI slope, by building one form per type and running
/// the same open/close cycle. Guessing at the source produced three wrong answers in a row; this
/// narrows the search to a control type instead of a line of code.
/// </summary>
static class Bisect
{
    [DllImport("user32.dll")] static extern uint GetGuiResources(IntPtr process, int flags);

    static uint Gdi() => GetGuiResources(System.Diagnostics.Process.GetCurrentProcess().Handle, 0);

    public static void Run()
    {
        (string name, Func<Control> make)[] cases =
        {
            ("baseline: empty Panel",   () => new Panel { Dock = DockStyle.Fill }),
            ("CardPanel",               () => new FrameGlass.Theme.CardPanel { Dock = DockStyle.Fill }),
            ("Theme.Slider",            () => new FrameGlass.Theme.Slider { Dock = DockStyle.Fill, Width = 200 }),
            ("Theme.ThemedCombo",      () => new FrameGlass.Theme.ThemedCombo { Dock = DockStyle.Fill, Width = 200 }),
            ("Theme.ThemedSpinner",    () => new FrameGlass.Theme.ThemedSpinner { Dock = DockStyle.Fill, Width = 120 }),
            ("Theme.FocusRing",         () => new FrameGlass.Theme.FocusRing { Dock = DockStyle.Fill }),
            ("Button (Theme.Flat)",     () => { var b = new Button { Text = "x", Dock = DockStyle.Fill }; FrameGlass.Theme.Flat(b); return b; }),
            ("ListView",                () => new ListView { Dock = DockStyle.Fill, View = View.Details }),
            ("DataGridView",            () => new DataGridView { Dock = DockStyle.Fill }),
            ("ToolTip component",       () => ToolTipCase()),
            ("ImageList + 2 cloned glyphs", () => ImageListCase()),
            ("child panel w/ undisposed Bitmap", () => BitmapChildCase()),
            ("Form + DWM dark title bar", () => null),
            ("Form + DWM Mica backdrop",  () => null),
        };

        Console.WriteLine($"{"control type",-30} {"GDI delta / 20",-18} verdict");
        Console.WriteLine(new string('-', 66));

        foreach (var (name, make) in cases)
        {
            _chromeMode = name.Contains("dark title bar") ? 1 : name.Contains("Mica") ? 2 : 0;
            long d = Slope(make);
            Console.WriteLine($"{name,-30} {d,+18} {(d > 5 ? "LEAKS" : "flat")}");
        }
    }

    static long Slope(Func<Control> make)
    {
        for (int i = 0; i < 3; i++) Cycle(make);
        GC.Collect(); GC.WaitForPendingFinalizers(); GC.Collect();
        uint g0 = Gdi();

        for (int i = 0; i < 20; i++) Cycle(make);
        GC.Collect(); GC.WaitForPendingFinalizers(); GC.Collect();
        return (long)Gdi() - g0;
    }

    /// <summary>
    /// Two leaks found, both at 1 object per form. A per-form constant is only 1 if the form holds
    /// one instance, so the next question is whether the slope scales with instance count (a real
    /// per-instance leak) or stays flat (a one-time cost, which would be a non-bug).
    /// </summary>
    public static void Scale()
    {
        Console.WriteLine("Does the slope scale with instance count? 1 object/form = a real per-instance leak.");
        Console.WriteLine();
        Console.WriteLine($"{"shape",-46} {"GDI delta / 20",-18} per form");
        Console.WriteLine(new string('-', 78));

        (string name, Func<Control> make)[] cases =
        {
            ("1 ThemedSpinner",            () => new FrameGlass.Theme.ThemedSpinner { Dock = DockStyle.Fill }),
            ("2 ThemedSpinner",            () => TwoSpin(2)),
            ("4 ThemedSpinner",            () => TwoSpin(4)),
            ("8 ThemedSpinner",            () => TwoSpin(8)),
            ("1 plain NumericUpDown",      () => new NumericUpDown { Dock = DockStyle.Fill }),
            ("1 ToolTip",                  () => ToolTipCase()),
            ("2 ToolTip",                  () => TipCase(2)),
            ("1 slider, no ToolTip",       () => new FrameGlass.Theme.Slider { Dock = DockStyle.Fill, Width = 200 }),
        };

        foreach (var (name, make) in cases)
        {
            long d = Slope(make);
            Console.WriteLine($"{name,-46} {d,+18} {d / 20.0,+7:0.00}");
        }

        Console.WriteLine();
        Console.WriteLine("Root cause inside NumericUpDown - control, or the button strip?");
        Console.WriteLine();
        Console.WriteLine($"{"shape",-46} {"GDI delta / 20",-18} per form");
        Console.WriteLine(new string('-', 78));

        (string name, Func<Control> make)[] inner =
        {
            ("NumericUpDown, stock",                  () => new NumericUpDown { Dock = DockStyle.Fill }),
            ("NumericUpDown, Increment = 0 (no step)",() => NoButtons()),
            ("UpDownBase (no value handling)",        () => UpDownBaseCase()),
            ("bare Panel, no spinner at all",        () => new Panel { Dock = DockStyle.Fill }),
        };

        foreach (var (name, make) in inner)
        {
            long d = Slope(make);
            Console.WriteLine($"{name,-46} {d,+18} {d / 20.0,+7:0.00}");
        }

        // The real form has six spinners but only leaks one per open. Hypothesis: the leak is per
        // *created handle*, and only the selected tab's controls get one, because a TabPage that
        // has never been shown does not realise its children.
        Console.WriteLine();
        Console.WriteLine("Accounting check: is it per handle, or per instance?");
        Console.WriteLine();
        Console.WriteLine($"{"shape",-46} {"GDI delta / 20",-18} per form");
        Console.WriteLine(new string('-', 78));

        (string name, Func<Control> make)[] tabs =
        {
            ("6 spinners, 1 visible tab",              () => Tabbed(6, 1)),
            ("6 spinners, 2 visible tabs",             () => Tabbed(6, 2)),
            ("6 spinners, all 6 tabs shown",           () => Tabbed(6, 6)),
            ("6 spinners on one flat page (no tabs)",  () => Flat(6)),
        };

        foreach (var (name, make) in tabs)
        {
            long d = Slope(make);
            Console.WriteLine($"{name,-46} {d,+18} {d / 20.0,+7:0.00}");
        }

        // The real form builds six spinners but measures 1/form. Count what is actually alive at
        // dispose time, rather than inferring it from the source.
        Console.WriteLine();
        Console.WriteLine("The real form: how many spinners exist, and how many leak?");
        Count();

        Console.WriteLine();
        Lifecycle();
    }

    /// <summary>
    /// The isolated NumericUpDown leaks 1.00 each, but the real form holds six of them and only
    /// leaks 0.85 in total. Those two facts cannot both describe the same leak, so the isolated case
    /// is not the real form's leak and the search has to go back to the whole form.
    ///
    /// Subtraction is the only honest way to narrow it: rebuild the form with one feature removed,
    /// measure, and keep the removal that moves the number. Each row is the full form except for
    /// the one thing named, so a row that drops to 0 identifies the cause.
    /// </summary>
    static void Count()
    {
        (string name, Action<Control>? sabotage)[] cases =
        {
            ("full form, nothing disabled",     null),
            ("timer stopped",                    f => { if (Comp(f, "_live") is System.Windows.Forms.Timer t) t.Stop(); }),
            ("ToolTip disposed early",            f => { if (Comp(f, "_tip") is ToolTip t) t.Dispose(); }),
            ("ImageList disposed early",          f => { if (Comp(f, "_marks") is ImageList l) l.Dispose(); }),
            ("preview panel emptied",             f => { if (Find(f, "_preview") is Panel p) p.Controls.Clear(); }),
        };

        Console.WriteLine($"{"form shape",-40} {"GDI / 20",-10} per form");
        Console.WriteLine(new string('-', 62));

        foreach (var (name, sabotage) in cases)
        {
            long d = SlopeForm(sabotage);
            Console.WriteLine($"{name,-40} {d,+10} {d / 20.0,+8:0.00}");
        }
    }

    /// <summary>Read a private field off the form. Probe-only: it needs to see what the form hides.</summary>
    static object? Comp(object o, string field) =>
        o.GetType().GetField(field,
            System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)
         ?.GetValue(o);

    static Control? Find(Control root, string field) => Comp(root, field) as Control;

    /// <summary>
    /// Four different sabotages each took the number to zero, which cannot all be the cause. The
    /// shared factor is that the form was still shown. So the question is not which object, it is
    /// which part of Show/close/dispose the GDI object is born in.
    /// </summary>
    public static void Lifecycle()
    {
        (string name, Mode mode)[] cases =
        {
            ("show, then Close() + Dispose()", Mode.Normal),
            ("never Show()",                   Mode.NeverShow),
            ("Show(), Dispose(), no Close()",  Mode.DisposeOnly),
            ("Show(), Close(), no Dispose()",  Mode.CloseOnly),
        };

        Console.WriteLine($"{"lifecycle",-40} {"GDI / 20",-10} per form");
        Console.WriteLine(new string('-', 62));

        foreach (var (name, mode) in cases)
        {
            long d = SlopeLifecycle(mode);
            Console.WriteLine($"{name,-40} {d,+10} {d / 20.0,+8:0.00}");
        }
    }

    /// <summary>
    /// A leak grows forever. A one-time cost - a cache, a lazily built font table, a shared
    /// resource that gets reused - stops growing and plateaus. Both look identical at 20 cycles, so
    /// run enough cycles to tell them apart. This is the test that decides which one this is.
    /// </summary>
    /// <summary>
    /// A test that passes is only worth something if it can still fail. The warm-up change could
    /// easily have hidden a real leak by settling past it, so plant a deliberate per-open leak and
    /// confirm the harness reports it.
    /// </summary>
    public static void SelfCheck()
    {
        Console.WriteLine("Does the harness still catch a real leak? Plant one and look.");
        Console.WriteLine();

        (string name, Action cycle)[] cases =
        {
            ("no leak planted (must be flat)",  () => { }),
            ("1 GDI brush leaked per open",      () => PlantBitmap()),
            ("3 GDI brushes leaked per open",     () => { PlantBitmap(); PlantBitmap(); PlantBitmap(); }),
        };

        foreach (var (name, cycle) in cases)
        {
            long d = SlopeBare(cycle);
            Console.WriteLine($"{name,-38} {d,+8} over 20  {(d > 10 ? "DETECTED" : "flat")}");
        }

        Console.WriteLine();
        Console.WriteLine("A harness that cannot fail is not a test. If both planted cases read flat,");
        Console.WriteLine("the warm-up change has broken the gate and --leak is not a gate any more.");
    }

    /// <summary>
    /// A Bitmap is a GDI+ object, and GDI+ objects are not in the GDI handle table that
    /// GetGuiResources counts. Planting one therefore proves nothing - which is exactly how a
    /// self-check can be green while measuring the wrong thing. Plant a real GDI brush instead:
    /// CreateSolidBrush is a genuine GDI handle, one per call, alive until DeleteObject.
    /// </summary>
    [DllImport("gdi32.dll")] static extern IntPtr CreateSolidBrush(int color);
    [DllImport("gdi32.dll")] static extern bool DeleteObject(IntPtr h);

    static readonly List<IntPtr> _planted = [];

    static void PlantBitmap() => _planted.Add(CreateSolidBrush(unchecked((int)0xFF0000FF)));

    static long SlopeBare(Action cycle)
    {
        for (int i = 0; i < 3; i++) cycle();
        GC.Collect(); GC.WaitForPendingFinalizers(); GC.Collect();
        uint g0 = Gdi();
        for (int i = 0; i < 20; i++) cycle();
        GC.Collect(); GC.WaitForPendingFinalizers(); GC.Collect();
        return (long)Gdi() - g0;
    }

    public static void Sustained(int cycles = 300)
    {
        Console.WriteLine($"Does it grow forever, or plateau? {cycles} open/close cycles of the real form.");
        Console.WriteLine();

        for (int i = 0; i < 3; i++) OpenForm(null);
        GC.Collect(); GC.WaitForPendingFinalizers(); GC.Collect();
        uint g0 = Gdi();
        Console.WriteLine($"  start  GDI={g0}");

        for (int i = 1; i <= cycles; i++)
        {
            OpenForm(null);
            if (i % 25 == 0)
            {
                GC.Collect(); GC.WaitForPendingFinalizers(); GC.Collect();
                Console.WriteLine($"  {i,4} cycles  GDI={Gdi(),-6} delta {Gdi() - g0,+6}  " +
                                  $"({(Gdi() - g0) / (double)i:0.000} per cycle)");
            }
        }
        GC.Collect(); GC.WaitForPendingFinalizers(); GC.Collect();
        uint d = Gdi() - g0;
        Console.WriteLine();
        Console.WriteLine(d < 50
            ? $"VERDICT: +{d} over {cycles} cycles and flat early. Bounded, not a leak."
            : $"VERDICT: +{d} over {cycles} cycles and still climbing. A real leak.");
    }

    enum Mode { Normal, NeverShow, DisposeOnly, CloseOnly }

    static long SlopeLifecycle(Mode mode)
    {
        for (int i = 0; i < 3; i++) OpenLifecycle(mode);
        GC.Collect(); GC.WaitForPendingFinalizers(); GC.Collect();
        uint g0 = Gdi();
        for (int i = 0; i < 20; i++) OpenLifecycle(mode);
        GC.Collect(); GC.WaitForPendingFinalizers(); GC.Collect();
        return (long)Gdi() - g0;
    }

    static void OpenLifecycle(Mode mode)
    {
        var cfg = new FrameGlass.Config
        {
            Lines = new[] { "game", "fps", "cpu", "cputemp", "gpu", "ram", "vram" },
            CustomRows = new List<string> { "my {fps} row" },
        };
        var f = new FrameGlass.SettingsForm(
            cfg, () => FrameGlass.FrameStats.Empty, () => new FrameGlass.Reading(), _ => { });
        f.StartPosition = FormStartPosition.Manual;
        f.Location = new Point(40, 40);

        if (mode != Mode.NeverShow) f.Show();
        for (int i = 0; i < 3; i++) { Application.DoEvents(); Thread.Sleep(20); }

        switch (mode)
        {
            case Mode.Normal: f.Close(); f.Dispose(); break;
            case Mode.NeverShow: f.Dispose(); break;
            case Mode.DisposeOnly: f.Dispose(); break;
            case Mode.CloseOnly: f.Close(); break;
        }
    }

    static long SlopeForm(Action<Control>? sabotage)
    {
        for (int i = 0; i < 3; i++) OpenForm(sabotage);
        GC.Collect(); GC.WaitForPendingFinalizers(); GC.Collect();
        uint g0 = Gdi();
        for (int i = 0; i < 20; i++) OpenForm(sabotage);
        GC.Collect(); GC.WaitForPendingFinalizers(); GC.Collect();
        return (long)Gdi() - g0;
    }

    static void OpenForm(Action<Control>? sabotage)
    {
        var cfg = new FrameGlass.Config
        {
            Lines = new[] { "game", "fps", "cpu", "cputemp", "gpu", "ram", "vram" },
            CustomRows = new List<string> { "my {fps} row" },
        };
        using var f = new FrameGlass.SettingsForm(
            cfg, () => FrameGlass.FrameStats.Empty, () => new FrameGlass.Reading(), _ => { });
        f.StartPosition = FormStartPosition.Manual;
        f.Location = new Point(40, 40);
        sabotage?.Invoke(f);
        f.Show();
        for (int i = 0; i < 3; i++) { Application.DoEvents(); Thread.Sleep(20); }
        f.Close();
        f.Dispose();
    }

    static void OpenOnce(out int spinners, out int tips)
    {
        var cfg = new FrameGlass.Config
        {
            Lines = new[] { "game", "fps", "cpu", "cputemp", "gpu", "ram", "vram" },
            CustomRows = new List<string> { "my {fps} row" },
        };
        using var f = new FrameGlass.SettingsForm(
            cfg, () => FrameGlass.FrameStats.Empty, () => new FrameGlass.Reading(), _ => { });
        f.StartPosition = FormStartPosition.Manual;
        f.Location = new Point(40, 40);

        int n = 0, t = 0;
        void Walk(Control c)
        {
            foreach (Control k in c.Controls)
            {
                if (k is NumericUpDown) n++;
                if (k is ToolTip) t++;
                Walk(k);
            }
        }
        f.Show();
        for (int i = 0; i < 3; i++) { Application.DoEvents(); Thread.Sleep(20); }
        Walk(f);
        spinners = n; tips = t;
        f.Close();
        f.Dispose();
    }

    /// <summary>Six spinners spread over tab pages, with only the first <paramref name="shown"/> realised.</summary>
    static Control Tabbed(int spinners, int shown)
    {
        var tabs = new TabControl { Dock = DockStyle.Fill };
        for (int t = 0; t < shown; t++)
        {
            var page = new TabPage($"p{t}");
            for (int i = 0; i < spinners; i++)
                page.Controls.Add(new NumericUpDown { Dock = DockStyle.Top, Width = 120 });
            tabs.TabPages.Add(page);
        }
        return tabs;
    }

    /// <summary>The same six spinners with no TabControl in the way.</summary>
    static Control Flat(int n)
    {
        var host = new Panel { Dock = DockStyle.Fill };
        for (int i = 0; i < n; i++)
            host.Controls.Add(new NumericUpDown { Dock = DockStyle.Top, Width = 120 });
        return host;
    }

    /// <summary>Stock NumericUpDown that never steps, so the button strip is never used.</summary>
    static Control NoButtons() => new NumericUpDown { Dock = DockStyle.Fill, Increment = 0 };

    /// <summary>The base UpDownBase behaviour, without NumericUpDown's value handling.</summary>
    static Control UpDownBaseCase() => new UpDownBaseProbe { Dock = DockStyle.Fill };

    sealed class UpDownBaseProbe : UpDownBase
    {
        public override void UpButton() { }
        public override void DownButton() { }
        protected override void UpdateEditText() { }
    }

    static Control TwoSpin(int n)
    {
        var host = new Panel { Dock = DockStyle.Fill };
        for (int i = 0; i < n; i++)
            host.Controls.Add(new FrameGlass.Theme.ThemedSpinner { Dock = DockStyle.Top, Width = 120 });
        return host;
    }

    static Control TipCase(int n)
    {
        var host = new Panel { Dock = DockStyle.Fill };
        for (int i = 0; i < n; i++)
        {
            var b = new Button { Text = "x" + i, Dock = DockStyle.Top };
            var tt = new ToolTip();
            tt.SetToolTip(b, "hello");
            host.Controls.Add(b);
            host.Tag = tt;
        }
        return host;
    }

    static void Cycle(Func<Control> make)
    {
        using var f = new Form { Size = new Size(500, 400) };
        // A null factory means "the form itself is the variable": run the app's own window-chrome
        // call, which is the one form-level thing the per-control cases above never exercise.
        if (make is null) { Chrome(f); return; }
        var c = make();
        f.Controls.Add(c);
        f.Show();
        for (int i = 0; i < 3; i++) { Application.DoEvents(); Thread.Sleep(20); }
        f.Close();
        f.Dispose();
    }

    [DllImport("dwmapi.dll")] static extern int DwmSetWindowAttribute(IntPtr h, int attr, int val, int size);

    /// <summary>Mirrors Theme.ApplyWindowChrome: attribute 20 dark mode, 38 system backdrop.</summary>
    static void Chrome(Form f)
    {
        int mode = _chromeMode;               // 0 = neither, 1 = dark title bar, 2 = Mica
        f.Shown += (_, _) =>
        {
            if (mode >= 1) DwmSetWindowAttribute(f.Handle, 20, 1, 4);
            if (mode >= 2) DwmSetWindowAttribute(f.Handle, 38, 2, 4);
        };
        f.Show();
        for (int i = 0; i < 3; i++) { Application.DoEvents(); Thread.Sleep(20); }
        f.Close();
        f.Dispose();
    }

    static int _chromeMode;

    /// <summary>A real ToolTip held by a child, so it is not collected while the form is alive.</summary>
    static Control ToolTipCase()
    {
        var host = new Panel { Dock = DockStyle.Fill };
        var b = new Button { Text = "x", Dock = DockStyle.Top };
        var tt = new ToolTip();
        tt.SetToolTip(b, "hello");
        host.Controls.Add(b);
        host.Tag = tt;      // mirror the real field: alive for the form's lifetime
        return host;
    }

    /// <summary>
    /// The SettingsForm.PreviewPanel shape: a child control that owns a Bitmap, replaces it on
    /// every update, and never disposes the last one. One GDI object per form open, forever.
    /// </summary>
    static Control BitmapChildCase()
    {
        var host = new BitmapHolder { Dock = DockStyle.Fill };
        host.Refresh(new Bitmap(64, 24, System.Drawing.Imaging.PixelFormat.Format32bppArgb));
        return host;
    }

    sealed class BitmapHolder : Panel
    {
        Bitmap? _bmp;
        public void Refresh(Bitmap b) { _bmp?.Dispose(); _bmp = b; }
        // deliberately no Dispose(bool) override: this is the shape under suspicion
    }

    /// <summary>Reproduces the metrics-table glyph pattern: an ImageList fed two clones, never in Controls.</summary>
    static Control ImageListCase()
    {
        var host = new Panel { Dock = DockStyle.Fill };
        var list = new ImageList { ImageSize = new Size(16, 16), ColorDepth = ColorDepth.Depth32Bit };
        list.Images.Clear();
        using (var b = new Bitmap(16, 16))
        using (var g = Graphics.FromImage(b))
        using (var glyph = (Bitmap)b.Clone())
            list.Images.Add(glyph);
        using (var b = new Bitmap(16, 16))
        using (var g = Graphics.FromImage(b))
        using (var glyph = (Bitmap)b.Clone())
            list.Images.Add(glyph);
        // attached to a tag so it is not collected, mirroring the real field
        host.Tag = list;
        return host;
    }
}
